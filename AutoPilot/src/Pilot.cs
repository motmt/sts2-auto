using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.AutoSlay;
using MegaCrit.Sts2.Core.AutoSlay.Handlers;
using MegaCrit.Sts2.Core.AutoSlay.Helpers;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace AutoPilot;

/// <summary>
/// AutoPilot 主控引擎。
///
/// 设计原则（关键）：
///  官方 <see cref="AutoSlayer"/> 是“烟雾测试”用的，它把决策写死成随机 + 作弊保命，且完成后会
///  强制退出游戏。本模组**只复用它的房间/界面识别骨架**，把决策层换成 <see cref="Brain"/>：
///    · 战斗出牌顺序由 Brain 打分决定，而非随机
///    · 地图选路由 Brain 按血量/金币/牌组强度决定，而非“第一个子节点”
///    · 选卡/商店/事件同样交给 Brain
///  同时补上官方没有的能力：随时暂停/恢复、决策日志、局后记忆总结、人工偏好注入、云端胜率查询。
///
/// 线程模型：整个循环跑在 Godot 主线程的 Task 上（用 TaskHelper.RunSafely 启动），
///  与官方 AutoSlayer 一致，因此可以直接触碰节点树与游戏状态。
/// </summary>
public static class Pilot
{
    // ============ 运行状态 ============
    public static bool IsRunning { get; private set; }
    public static bool IsPaused { get; private set; }
    public static string Phase { get; private set; } = "待机";

    private static CancellationTokenSource _cts;
    private static Task _loopTask;
    private static readonly object _gate = new();

    // 决策留痕：供 AI 复盘与记忆总结使用
    private static readonly List<string> _decisionLog = new();
    private static readonly List<string> _consoleLog = new();

    // 本局统计
    public static int FloorsClimbed;
    public static int CombatsWon;
    public static int ElitesWon;
    public static int HpLostTotal;
    public static int CardsPicked;
    public static int CardsSkipped;
    public static string CurrentActSummary = "";

    /// <summary>本机玩家（可能为 null）。</summary>
    public static Player Me
    {
        get
        {
            try { return LocalContext.GetMe(RunManager.Instance?.DebugOnlyGetState()); }
            catch { return null; }
        }
    }

    public static RunState RunStateNow
    {
        get
        {
            try { return RunManager.Instance?.DebugOnlyGetState(); }
            catch { return null; }
        }
    }

    public static bool InRun => RunManager.Instance?.IsInProgress == true && RunStateNow != null;

    // ============ 启动 / 停止 / 暂停 ============
    public static string Start()
    {
        lock (_gate)
        {
            if (IsRunning) return IsPaused ? Resume("Start") : "AI 已在运行中（ap pause 可暂停）";
            if (!InRun) return "错误：当前没有进行中的对局。请先进一局，再输入 ap start";
            // 安装主线程帧驱动：让主循环的每个 await 都在 Godot 主线程上被唤醒。
            // 这是"战斗胜利后黑屏 + 主循环静默死亡"的根治手段（详见 MainThreadRunner 注释）。
            MainThreadRunner.Install();
            ResetStats();
            IsRunning = true;
            IsPaused = false;
            _cts = new CancellationTokenSource();
            _loopTask = TaskHelper.RunSafely(LoopAsync(_cts.Token));
            // 看门狗：主循环若在若干帧内毫无推进，则打印诊断并自动重启，避免"卡死到黑屏"。
            _watchdog = TaskHelper.RunSafely(WatchdogAsync(_cts.Token));
            SetPhase("启动");
            Log("自动游玩已启动" + (MainThreadRunner.Installed ? "（主线程帧驱动已启用）" : ""));
            return "▶ AI 已接管本局。玩家可随时 ap pause 暂停 / ap stop 停止。";
        }
    }

    // ============ 看门狗 ============
    private static Task _watchdog;
    private static long _lastProgressFrame;
    private static long _lastEngineFrame;
    private static string _lastProgressPhase = "";

    /// <summary>
    /// 卡死自愈：主循环每推进一步都会刷新 <see cref="_lastProgressFrame"/>。
    /// 若连续 ~30 秒（按 60FPS 约 1800 帧）主线程仍在跑帧、但主循环毫无进展，
    /// 说明某次 await 的续体丢了（战斗结算竞态等）→ 打印诊断并重建主循环。
    /// </summary>
    private static async Task WatchdogAsync(CancellationToken ct)
    {
        MarkProgress();
        while (!ct.IsCancellationRequested && IsRunning)
        {
            await MainThreadRunner.DelayAsync(3000, ct);
            if (ct.IsCancellationRequested || !IsRunning) return;

            long frames = MainThreadRunner.Frames;
            bool engineAlive = frames - _lastEngineFrame > 1;   // 主线程还在跑帧
            _lastEngineFrame = frames;
            if (!engineAlive) continue;                          // 引擎本身卡了，不归我们管

            long idle = frames - _lastProgressFrame;
            if (idle > 1800)                                     // ≈30 秒无进展
            {
                Warn($"看门狗：主循环 {idle} 帧无进展（阶段 {Phase}），自动重启主循环");
                _lastProgressFrame = frames;
                try { _cts?.Cancel(); } catch { }
                // 重建令牌与主循环（保留运行状态）
                CancellationTokenSource old = _cts;
                _cts = new CancellationTokenSource();
                CancellationToken nct = _cts.Token;
                try { old?.Dispose(); } catch { }
                _loopTask = TaskHelper.RunSafely(LoopAsync(nct));
                _watchdog = TaskHelper.RunSafely(WatchdogAsync(nct));
                return;                                           // 旧看门狗退出，新的接管
            }
        }
    }

    /// <summary>主循环调用：标记"有进展"，看门狗据此判断是否卡死。</summary>
    public static void MarkProgress()
    {
        _lastProgressFrame = MainThreadRunner.Frames;
        _lastProgressPhase = Phase;
    }

    public static string Pause()
    {
        lock (_gate)
        {
            if (!IsRunning) return "AI 未在运行";
            if (IsPaused) return "AI 已是暂停状态";
            IsPaused = true;
            SetPhase("已暂停");
            Log("已暂停，可以手动操作；ap resume 交还给 AI");
            return "⏸ 已暂停。现在你可以手动操作游戏；输入 ap resume 让 AI 继续。";
        }
    }

    public static string Resume(string who = "Resume")
    {
        lock (_gate)
        {
            if (!IsRunning) return Start();
            if (!IsPaused) return "AI 未处于暂停状态";
            IsPaused = false;
            SetPhase("恢复");
            Log("已恢复（" + who + "）");
            return "▶ AI 已恢复接管。";
        }
    }

    public static string Stop()
    {
        lock (_gate)
        {
            if (!IsRunning) return "AI 未在运行";
            IsRunning = false;
            IsPaused = false;
            try { _cts?.Cancel(); } catch { }
            _cts = null;
            SetPhase("已停止");
            Log("已停止，控制权完全交回玩家");
            WriteMemory("手动停止");
            return "⏹ 已停止。控制权已交回。";
        }
    }

    /// <summary>等待 AI 让渡控制权（暂停或停止时循环会在此阻塞）。</summary>
    public static async Task WaitIfPausedAsync(CancellationToken ct)
    {
        while (IsPaused && !ct.IsCancellationRequested)
        {
            await MainThreadRunner.DelayAsync(200, ct);
        }
    }

    // ============ 主循环 ============
    private static async Task LoopAsync(CancellationToken ct)
    {
        try
        {
            Log("进入主循环，等待房间就绪…");
            // 等待房间类型被游戏分配（与官方 AutoSlayer 一致）
            for (int i = 0; i < 100 && !ct.IsCancellationRequested; i++)
            {
                RunState rs = RunStateNow;
                if (rs?.CurrentRoom != null && rs.CurrentRoom.RoomType != RoomType.Unassigned) break;
                await MainThreadRunner.DelayAsync(200, ct);
            }

            while (!ct.IsCancellationRequested && IsRunning)
            {
                MarkProgress();
                await WaitIfPausedAsync(ct);
                if (ct.IsCancellationRequested || !IsRunning) break;

                RunState rs = RunStateNow;
                if (rs == null || rs.CurrentRoom == null || !RunManager.Instance.IsInProgress)
                {
                    Log("对局已结束或状态不可用，AI 退出");
                    break;
                }

                RoomType roomType = rs.CurrentRoom.RoomType;
                FloorsClimbed = rs.TotalFloor;
                SetPhase($"第 {rs.TotalFloor} 层 · {RoomTypeCn(roomType)}");

                try
                {
                    await Brain.HandleRoomAsync(roomType, ct);
                    MarkProgress();
                    await MainThreadRunner.DelayAsync(200, ct);
                    await DrainScreensAsync(ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception e)
                {
                    Warn($"处理 {roomType} 出错（自动恢复）：{e.Message}");
                    await MainThreadRunner.DelayAsync(1000, ct);
                }

                MarkProgress();
                await MainThreadRunner.DelayAsync(300, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停止
        }
        catch (Exception e)
        {
            ModEntry.Logger.Error("AutoPilot loop crashed: " + e);
            SetPhase("异常终止");
        }
        finally
        {
            IsRunning = false;
            IsPaused = false;
            if (Phase != "异常终止") SetPhase("待机");
            Log("主循环已退出");
        }
    }

    // ============ 浮层界面清空（复用官方 handler 集合） ============
    private static Dictionary<Type, Action<Task, CancellationToken>> _screenDrainers;

    /// <summary>把 Brain 的决策接到官方各 ScreenHandler 上：打开界面 → Brain 决策 → 点击。</summary>
    private static void EnsureScreenDrainers()
    {
        if (_screenDrainers != null) return;
        _screenDrainers = new Dictionary<Type, Action<Task, CancellationToken>>
        {
            // 由 Harmony 补丁在各界面打开时调用 Brain 的对应决策
        };
    }

    /// <summary>清空所有浮层界面（奖励、选卡、遗物、篝火升级等）。</summary>
    public static async Task DrainScreensAsync(CancellationToken ct)
    {
        ulong lastId = 0;
        int noProgress = 0;
        for (int guard = 0; guard < 60; guard++)
        {
            await WaitIfPausedAsync(ct);
            if (ct.IsCancellationRequested) return;

            NOverlayStack stack = NOverlayStack.Instance;
            if (stack == null || stack.ScreenCount == 0) return;

            IOverlayScreen top = stack.Peek();
            if (top == null) return;

            // 无进展检测：栈顶连续两次还是同一实例（AI 点了也没换下去）→ 及时退出，
            // 把控制权还给主循环，避免 60 轮空转 12 秒（也会让"卡住的界面"看起来像死机）。
            Node topNode = top as Node;
            ulong curId = topNode != null ? topNode.GetInstanceId() : 0;
            if (curId != 0 && curId == lastId)
            {
                if (++noProgress >= 2) return;
            }
            else noProgress = 0;
            lastId = curId;

            bool acted;
            try
            {
                acted = await ScreenRouter.HandleTopAsync(top, ct);
            }
            catch (Exception e)
            {
                Warn("处理界面 " + top.GetType().Name + " 失败: " + e.Message);
                acted = false;
            }

            if (!acted)
            {
                Log("界面 " + top.GetType().Name + " 无 AI 处理器，交还玩家处理");
                return;
            }
            await MainThreadRunner.DelayAsync(200, ct);
        }
    }

    // ============ 日志与统计 ============
    public static void Log(string msg)
    {
        string line = $"[{DateTime.Now:HH:mm:ss}] {msg}";
        _consoleLog.Add(line);
        if (_consoleLog.Count > 200) _consoleLog.RemoveAt(0);
        // v2.4.1：决策留痕与运行日志同步（此前 ReadMemory 读 _decisionLog 恒为空 —— 没有任何一处写入它）
        _decisionLog.Add(line);
        if (_decisionLog.Count > 500) _decisionLog.RemoveRange(0, 200);
        ModEntry.Logger.Info("[AutoPilot] " + msg);
        Brain.Explain(msg); // 同时进入 Brain 决策轨迹
    }

    public static void Warn(string msg) => ModEntry.Logger.Warn("[AutoPilot] " + msg);

    private static void SetPhase(string p) => Phase = p;

    public static void ResetStats()
    {
        FloorsClimbed = 0; CombatsWon = 0; ElitesWon = 0;
        HpLostTotal = 0; CardsPicked = 0; CardsSkipped = 0;
        _decisionLog.Clear();
        _consoleLog.Clear();
    }

    public static IReadOnlyList<string> RecentLog => _consoleLog;

    public static string StatusText()
    {
        string state = !IsRunning ? "未运行" : (IsPaused ? "已暂停" : "运行中");
        RunState rs = RunStateNow;
        string pos = rs == null ? "-" : $"第 {rs.TotalFloor} 层 · 第 {rs.CurrentActIndex + 1} 幕";
        return "AutoPilot 状态\n" +
               $"  运行: {state}   当前: {Phase}\n" +
               $"  位置: {pos}\n" +
               $"  战绩: 战斗胜 {CombatsWon} / 精英 {ElitesWon} / 已选卡 {CardsPicked} / 跳过 {CardsSkipped}\n" +
               $"  最近决策: {(RecentLog.Count == 0 ? "(无)" : RecentLog[^1])}";
    }

    public static string RecentLogText(int n = 20)
    {
        if (_consoleLog.Count == 0) return "(暂无日志)";
        return string.Join("\n", _consoleLog.TakeLast(n));
    }

    public static string RoomTypeCn(RoomType t) => t switch
    {
        RoomType.Monster => "战斗",
        RoomType.Elite => "精英",
        RoomType.Boss => "首领",
        RoomType.Event => "事件",
        RoomType.Shop => "商店",
        RoomType.Treasure => "宝箱",
        RoomType.RestSite => "休息点",
        _ => t.ToString(),
    };

    // ============ 记忆总结 ============
    /// <summary>把本局表现写成一份结构化“记忆”，供 AI 复盘与后续决策参考。</summary>
    public static void WriteMemory(string reason)
    {
        if (!PilotConfig.Current.EnableMemory) return;
        try
        {
            string dir = System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) ?? ".",
                PilotConfig.Current.MemoryDir);
            System.IO.Directory.CreateDirectory(dir);

            RunState rs = RunStateNow;
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("# AutoPilot 对局记忆");
            sb.AppendLine($"- 时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"- 结束原因: {reason}");
            if (rs != null)
            {
                sb.AppendLine($"- 角色: {Me?.Character?.Id.Entry ?? "?"}");
                sb.AppendLine($"- 层数: {rs.TotalFloor}  幕: {rs.CurrentActIndex + 1}");
                sb.AppendLine($"- 血量: {Me?.Creature?.CurrentHp ?? 0}/{Me?.Creature?.MaxHp ?? 0}");
                sb.AppendLine($"- 金币: {Me?.Gold ?? 0}");
                var deck = Me?.Deck?.Cards;
                if (deck != null)
                {
                    sb.AppendLine($"- 牌组({deck.Count}张): " + string.Join(", ", deck.Select(c => c.Id.Entry)));
                }
                var relics = Me?.Relics;
                if (relics != null)
                {
                    sb.AppendLine($"- 遗物({relics.Count}): " + string.Join(", ", relics.Select(r => r.Id.Entry)));
                }
            }
            sb.AppendLine($"- 统计: 战斗胜 {CombatsWon} / 精英 {ElitesWon} / 选卡 {CardsPicked} / 跳过 {CardsSkipped}");
            sb.AppendLine();
            sb.AppendLine("## 决策留痕（最后 40 条）");
            foreach (string d in _decisionLog.TakeLast(40)) sb.AppendLine("- " + d);

            string file = System.IO.Path.Combine(dir, $"run_{DateTime.Now:yyyyMMdd_HHmmss}.md");
            System.IO.File.WriteAllText(file, sb.ToString());
            ModEntry.Logger.Info("[AutoPilot] 记忆已写入 " + file);
        }
        catch (Exception e)
        {
            ModEntry.Logger.Warn("[AutoPilot] 写记忆失败: " + e);
        }
    }

    // ============ Harmony 钩子 ============
    public static void AttachHooks()
    {
        try
        {
            EnsureScreenDrainers();
        }
        catch (Exception e)
        {
            ModEntry.Logger.Warn("[AutoPilot] hook attach failed: " + e);
        }
    }
}
