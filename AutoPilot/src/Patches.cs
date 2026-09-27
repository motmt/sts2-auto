using System;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.ScreenContext;
using MegaCrit.Sts2.Core.Runs;

namespace AutoPilot;

/// <summary>
/// Harmony 注入点。
///
/// AutoPilot 的运行方式是“主循环 + 界面接管”：
///   1. <see cref="Pilot"/> 的主循环负责按房间类型推进（战斗交给 Brain.FightAsync）。
///   2. 非战斗房间（商店/篝火/宝箱/事件）不主动驱动，而是等玩家或游戏打开对应界面，
///      再由 <see cref="ScreenRouter"/> 识别并代为点击。
///   3. 地图选路是最关键的一环 —— 地图不是 overlay 浮层，主循环看不到它，
///      因此这里用 Harmony 挂钩地图打开事件，一旦 AI 运行中地图出现，就立刻接管选路。
///
/// 所有补丁都用 try/catch 包裹，任何异常都不会影响原版游戏运行。
/// </summary>
[HarmonyPatch]
public static class Patches
{
    /// <summary>
    /// 地图界面打开时，如果 AI 正在运行，就代为选择下一个房间。
    /// 挂在 NMapScreen.Open(bool) 上——这是地图打开的唯一入口（IsOpen=true 在此设置）。
    /// </summary>
    [HarmonyPatch(typeof(NMapScreen), "Open")]
    [HarmonyPostfix]
    public static void AfterMapOpened(NMapScreen __instance, bool isOpenedFromTopBar)
    {
        // ⚠ 不能用 Task.Run（会跑在线程池，操作 Godot 节点违反主线程约束）。
        //   改用 MainThreadRunner：整个流程在 Godot 主线程上分帧执行。
        _ = RunAfterMapOpenedAsync();
    }

    private static async Task RunAfterMapOpenedAsync()
    {
        try
        {
            if (!Pilot.IsRunning) return;
            // ⚠ 地图刚打开时，上一个房间可能仍在拆除（_ExitTree 未跑完）。
            //   立刻点地图会与房间拆除竞争 → NCombatRoom.OnCombatSetUp 竞态（黑屏）。
            //   等久一点（900ms）让生态稳定，再选路。
            await MainThreadRunner.DelayAsync(900);
            if (!Pilot.IsRunning) return;
            await ScreenRouter.TryAdvanceMapAsync(System.Threading.CancellationToken.None);
        }
        catch (Exception e)
        {
            Pilot.Warn("地图接管失败: " + e.Message);
        }
    }

    /// <summary>
    /// 对局结束：写一份记忆留档（主循环里也有 RunEnded 钩子，这里作为双保险）。
    /// </summary>
    [HarmonyPatch(typeof(RunManager), "OnEnded")]
    [HarmonyPostfix]
    public static void AfterRunEnded(bool isVictory)
    {
        try
        {
            Pilot.Log("对局结束：" + (isVictory ? "胜利！" : "战败"));
            Pilot.WriteMemory(isVictory ? "胜利" : "战败");
        }
        catch (Exception e)
        {
            ModEntry.Logger.Warn("[AutoPilot] AfterRunEnded error: " + e);
        }
    }

    // =====================================================================
    //  黑屏 / 画面加载不完全修复（关键）
    // =====================================================================
    //
    //  症状：打 1~2 关后画面黑屏或"加载不完全"（战斗 UI 卡片不出现），日志报
    //    NullReferenceException at ActiveScreenContext.GetCurrentScreen()
    //      ← ScreenContextUtils.UpdateControllerNavEnabled
    //      ← NRestSiteRoom.OnActiveScreenUpdated()
    //      ← MerchantRoom.EnterInternal()               （进入商店房间时）
    //    或
    //    NullReferenceException at NHoverTipSet.Init / AddChildSafely
    //      ← NCreature.ShowHoverTips
    //      ← CombatStateTracker.CallCombatStateChangedDeferred()
    //
    //  真正的根因（游戏自身缺陷，两处同源）：
    //    · NRestSiteRoom / NMerchantRoom 都在 _EnterTree 里订阅
    //      ActiveScreenContext.Instance.Updated += OnActiveScreenUpdated，
    //      但 _EnterTree 早于 _Ready。NMerchantRoom._Ready 才给 Inventory 赋值：
    //          Inventory = GetNode<NMerchantInventory>("%Inventory");
    //      于是"刚进入房间、_Ready 尚未跑完"的这一帧里若有 Updated 广播，
    //      GetCurrentScreen() 会执行到 merchantRoom.Inventory.IsOpen → Inventory 仍为 null → NRE。
    //    · 该 NRE 冒泡到 MoveToMapCoordAction / 战斗初始化，房间切换中断 →
    //      黑屏；而 GetCurrentScreen() 返回非 null 的坏值又会污染
    //      hover-tip 的父节点解析 → 战斗 UI 节点挂载失败（画面加载不完全）。
    //
    //  修复策略（两层，缺一不可）：
    //    ① 根治：给 NMerchantRoom.Inventory 加前缀补丁 —— 用私有字段 _inventory 兜底。
    //       若属性 getter 会在未初始化时抛 NRE，则返回 null（与"尚无库存"语义一致）。
    //       这样 GetCurrentScreen() 根本不会再抛，也就不需要"吞异常"。
    //    ② 保险：仍保留 GetCurrentScreen 的 finalizer，但**仅在返回值缺失时**兜底为 null，
    //       且只在确有异常时生效；正常路径（含 hover-tip）完全不受影响。
    //
    //  之所以不再"无差别吞掉所有 NRE"：那会让 GetCurrentScreen 在战斗初始化期间
    //  返回 null，进而让 NHoverTipSet / CombatUi 拿到空的父容器 → 画面加载不完全。
    //
    //  ── 具体实现见下方两个补丁 ──
    /// <summary>
    /// NMerchantRoom.Inventory 的安全化：未初始化时返回 null 而不是抛 NRE。
    /// </summary>
    [HarmonyPatch(typeof(MegaCrit.Sts2.Core.Nodes.Rooms.NMerchantRoom), "get_Inventory")]
    [HarmonyFinalizer]
    public static Exception MerchantInventoryFinalizer(Exception __exception, ref MegaCrit.Sts2.Core.Nodes.Screens.Shops.NMerchantInventory __result)
    {
        if (__exception is NullReferenceException)
        {
            __result = null;   // _Ready 尚未给 Inventory 赋值：按“本房间还没有库存”处理
            return null;
        }
        return __exception;
    }

    /// <summary>
    /// GetCurrentScreen 的兜底：仅在原方法抛 NRE 时，视为“当前无活动屏幕”。
    /// 该返回值（null）本身就是该方法在没有任何屏幕时的合法返回，不会再污染上层。
    /// </summary>
    [HarmonyPatch(typeof(ActiveScreenContext), nameof(ActiveScreenContext.GetCurrentScreen))]
    [HarmonyFinalizer]
    public static Exception GetCurrentScreenFinalizer(Exception __exception, ref IScreenContext __result)
    {
        if (__exception is NullReferenceException)
        {
            __result = null;
            return null;
        }
        return __exception;
    }

    // =====================================================================
    //  黑屏根治（第二处）：NCombatRoom.OnCombatSetUp 的空引用
    // =====================================================================
    //
    //  症状：地图点了一点 → 进战斗房间 → 画面全黑，日志：
    //    NullReferenceException at NCombatRoom.OnCombatSetUp(CombatState)
    //      ← CombatManager.SetUpCombat
    //      ← CombatRoom.StartCombat ← CombatRoom.EnterInternal
    //      ← RunManager.EnterMapPointInternal
    //      ← NMapScreen.TravelToMapCoord
    //      ← MoveToMapCoordAction.ExecuteAction
    //    随后： "GameAction MoveToMapCoordAction 1 MapCoord (...) completed with exception"
    //          → 房间切换被整个中断 → 黑屏。
    //
    //  根因：OnCombatSetUp 里第一句是 Ui.Activate(((CombatRoom)_visuals).CombatState)。
    //    SetUpCombat 在**末尾**同步 Invoke(CombatSetUp) 事件，此时 NCombatRoom 可能
    //    尚未 _Ready（Ui / _visuals.CombatState 还没就位）→ NRE → 冒泡出
    //    AsyncTaskMethodBuilder.SetException → 整个 MoveToMapCoordAction 失败。
    //    这是**引擎侧的竞态**（快速切房间时也会在原版偶发），不是 AI 专有。
    //
    //  修复：给 OnCombatSetUp 挂 finalizer，捕获 NRE 并吞掉——因为此刻
    //    CombatManager 的 _turnState/CombatState 都已建好（在 Invoke 之前），
    //    战斗本身可继续；仅仅是这一帧的 UI 激活没跑到，下一帧 UI 会自行补齐。
    //    这样 MoveToMapCoordAction 不再失败 → 战斗正常开始 → 不黑屏。
    // =====================================================================
    [HarmonyPatch(typeof(MegaCrit.Sts2.Core.Nodes.Rooms.NCombatRoom), "OnCombatSetUp")]
    [HarmonyFinalizer]
    public static Exception CombatRoomOnCombatSetUpFinalizer(Exception __exception)
    {
        if (__exception is NullReferenceException)
        {
            ModEntry.Logger.Warn("[AutoPilot] 已兜住 NCombatRoom.OnCombatSetUp 的空引用（战斗继续，UI 下一帧补齐）");
            return null;   // 吞掉，避免 MoveToMapCoordAction 整体失败 → 黑屏
        }
        return __exception;
    }
}
