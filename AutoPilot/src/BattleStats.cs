using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Godot;
using MegaCrit.Sts2.Core.Nodes;

namespace AutoPilot;

/// <summary>
/// 战斗观测量（AutoPilot 控制台版）—— 取代被隐藏的 CombatSolver 浮层。
///
/// 背景：
///   CombatSolver 的浮层（左上角那个面板）承载了两件在实战里最关键的可观测信息：
///     ① 战损数：本局已经掉了几点血、这条路线预计还要掉几点血；
///     ② 搜索耗时：这一次求解花了几秒、一共查了多少条世界线。
///   我方把浮层整体隐藏后（UiSuppressed），这些信息在游戏里就看不见了。
///   本模块把它们搬进 `ap` 控制台，作为**唯一可见出口**。
///
/// 数据来源：
///   CombatSolver 侧的 SolverController.CaptureTelemetry()（本仓库新增的 internal API），
///   返回 SolverTelemetrySnapshot 只读记录。AutoPilot 通过反射读取其属性。
///
/// 线程约定：
///   必须在游戏主线程调用（CombatManager 状态非线程安全）。
///   控制台命令通常已在主线程；若不在（结果非 Void 类型即代表非主线程），
///   用 NGame.CallDeferred 回到主线程再取。
/// </summary>
public static class BattleStats
{
    // ── 累计统计（跨场，供 ap battle total 查看）─────────────────────────
    public static int FightsSeen { get; private set; }
    public static int HpLostAccumulated { get; private set; }
    public static double SearchSecondsAccumulated { get; private set; }
    public static double LastFightHpLost { get; private set; }
    public static double LastFightSeconds { get; private set; }

    private static int _lastFightHp = -1;      // 上一场战斗读到的"本场掉血"

    // ── 反射缓存 ────────────────────────────────────────────────────────
    private static bool _probed;
    private static bool _available;
    private static string _lastError = "";

    private static MethodInfo? _captureTelemetry;
    private static Type? _snapshotType;
    private static readonly Dictionary<string, PropertyInfo> Props = new();

    /// <summary>CombatSolver 遥测是否可用。</summary>
    public static bool Available
    {
        get
        {
            if (_available) return true;
            EnsureProbed();
            return _available;
        }
    }

    public static string LastError => _lastError;

    private static void EnsureProbed()
    {
        if (_available) return;
        try
        {
            Assembly? asm = null;
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                string n = a.GetName().Name ?? "";
                if (n.Equals("CombatSolver", StringComparison.OrdinalIgnoreCase)) { asm = a; break; }
            }
            if (asm == null)
            {
                _lastError = "CombatSolver 未加载";
                _probed = true;
                return;
            }

            Type? ctrl = asm.GetType("CombatSolver.SolverController", throwOnError: false);
            if (ctrl == null)
            {
                _lastError = "未找到 CombatSolver.SolverController";
                _probed = true;
                return;
            }

            _captureTelemetry = ctrl.GetMethod("CaptureTelemetry",
                BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
            _snapshotType = asm.GetType("CombatSolver.SolverTelemetrySnapshot", throwOnError: false);

            if (_captureTelemetry == null || _snapshotType == null)
            {
                _lastError = "遥测接口缺失（CombatSolver 版本过旧？）";
                _probed = true;
                return;
            }

            _available = true;
            _probed = true;
            _lastError = "";
        }
        catch (Exception e)
        {
            _probed = true;
            _lastError = e.Message;
        }
    }

    /// <summary>拉取一份遥测快照；失败返回 null。</summary>
    private static object? CaptureRaw()
    {
        if (!Available || _captureTelemetry == null) return null;
        try { return _captureTelemetry.Invoke(null, null); }
        catch (TargetInvocationException tie) { _lastError = (tie.InnerException ?? tie).Message; return null; }
        catch (Exception e) { _lastError = e.Message; return null; }
    }

    private static T Get<T>(object snap, string name, T fallback = default!)
    {
        if (!Props.TryGetValue(name, out PropertyInfo? p))
        {
            p = _snapshotType?.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (p == null) return fallback;
            Props[name] = p;
        }
        try
        {
            object? v = p.GetValue(snap);
            if (v == null) return fallback;
            return (T)Convert.ChangeType(v, typeof(T));
        }
        catch { return fallback; }
    }

    /// <summary>
    /// 渲染一行/多行战报（ap battle 用）。必须主线程。
    /// </summary>
    public static string Report(bool includeTotals)
    {
        if (!Available)
        {
            return "CombatSolver 遥测不可用：" + (_lastError.Length > 0 ? _lastError : (_probed ? "接口缺失" : "尚未探测")) +
                   "\n（ap solver 可查看桥接状态；战斗引擎需为 solver 才有遥测）";
        }

        object? snap = CaptureRaw();
        if (snap == null) return "读取遥测失败：" + _lastError;

        // 战斗结束点：本场掉血/耗时在此定稿并计入累计
        int hpThisFight = Get(snap, "HpLostThisFight", 0);
        double searchThisFight = Get(snap, "SearchSecondsTotal", 0d);
        bool combatActive = Get(snap, "CombatActive", false);
        if (!combatActive && _lastFightHp != hpThisFight)
        {
            // 一场战斗刚刚结束（或首次读到结束态）—— 结算累计
            if (_lastFightHp >= 0)
            {
                FightsSeen++;
                HpLostAccumulated += hpThisFight;
                SearchSecondsAccumulated += searchThisFight;
                LastFightHpLost = hpThisFight;
                LastFightSeconds = searchThisFight;
            }
            _lastFightHp = hpThisFight;
        }

        string phase = Get(snap, "Phase", "?");
        bool fullAuto = Get(snap, "FullAutoEnabled", false);
        bool searching = Get(snap, "IsSearching", false);
        bool applying = Get(snap, "IsApplying", false);
        bool hasResult = Get(snap, "HasResult", false);

        int currentTurn = Get(snap, "CurrentTurn", 0);
        int curLoss = Get(snap, "CurrentTurnHpLoss", 0);
        int curRecv = Get(snap, "CurrentTurnHpRecovered", 0);

        int projected = Get(snap, "ProjectedBattleHpLost", 0);
        bool projectedKnown = Get(snap, "ProjectedBattleHpLossKnown", false);
        bool deathOnly = Get(snap, "OnlyDeathRoutesFound", false);
        int unavoidable = Get(snap, "UnavoidableHpLost", 0);
        int soldHp = Get(snap, "SoldHp", 0);
        int futureSold = Get(snap, "FutureSoldHp", 0);

        double searchSec = Get(snap, "SearchSeconds", 0d);
        double routeSec = Get(snap, "RouteSeconds", 0d);
        long nodes = Get(snap, "ExpandedNodes", 0L);
        long worldlines = Get(snap, "ReviewedWorldlines", 0L);
        int startTurn = Get(snap, "StartTurn", 0);
        int searchedTurns = Get(snap, "SearchedTurns", 0);
        string boundary = Get(snap, "SearchBoundaryCn", "-");

        var sb = new StringBuilder();
        sb.AppendLine("╔══ 战斗观测（CombatSolver 遥测）══╗");

        // ── 战损 ─────────────────────────────────────────────
        sb.AppendLine($"  战损 · 本场已掉血   {hpThisFight} HP" +
                      (_lastFightHp >= 0 && _lastFightHp != hpThisFight ? $"（上一场 {LastFightHpLost:F0}）" : ""));
        if (hasResult)
        {
            string proj = projectedKnown ? $"预计还要掉 {projected} HP" : "预计战损 未知（尚未算完）";
            sb.AppendLine($"  战损 · 路线预测     {proj}" +
                          (deathOnly ? "   ⚠ 只有致死路线" : ""));
            sb.AppendLine($"  战损 · 卖血         路线未来 {futureSold}  │  本场累计 {soldHp}  │  不可避免 {unavoidable}");
        }
        else
        {
            sb.AppendLine("  战损 · 路线预测     （本场尚无求解结果）");
        }

        // ── 耗时 ─────────────────────────────────────────────
        sb.AppendLine($"  耗时 · 本次搜索     {searchSec:F1} 秒" +
                      (nodes > 0 ? $"  │  节点 {nodes:N0}" : ""));
        if (worldlines > 0)
            sb.AppendLine($"  耗时 · 世界线       本场累计查阅 {worldlines:N0} 条" +
                          (searchThisFight > 0d ? $"  │  本场累计 {searchThisFight:F1} 秒" : ""));
        if (hasResult && searchedTurns > 0)
            sb.AppendLine($"  耗时 · 路线跨度     第 {startTurn} 回合起，共 {searchedTurns} 回合  │  路线用时 {routeSec:F1} 秒");
        sb.AppendLine($"  边界 · 停止原因     {boundary}");

        // ── 状态 ─────────────────────────────────────────────
        string tags = (searching ? "搜索中 " : "") + (applying ? "执行中 " : "") +
                      (fullAuto ? "全自动" : "未托管");
        sb.AppendLine($"  状态 · {phase}（{tags.Trim()}）" +
                      (combatActive && currentTurn > 0
                          ? $"  │  第 {currentTurn} 回合  本回合 -{curLoss}" + (curRecv > 0 ? $"/+{curRecv}" : "")
                          : "  │  不在战斗中"));

        if (includeTotals)
        {
            sb.AppendLine("  ──────────────────────────");
            sb.AppendLine($"  累计 · 已结算 {FightsSeen} 场  │  共掉血 {HpLostAccumulated} HP  │  共搜索 {SearchSecondsAccumulated:F1} 秒");
        }

        sb.AppendLine("╚══════════════════════════════╝");
        sb.AppendLine("  ap battle total  看跨场累计    ap status  看整体状态");
        return sb.ToString().TrimEnd();
    }

    /// <summary>重置跨场累计。</summary>
    public static string ResetTotals()
    {
        FightsSeen = 0;
        HpLostAccumulated = 0;
        SearchSecondsAccumulated = 0;
        LastFightHpLost = 0;
        LastFightSeconds = 0;
        _lastFightHp = -1;
        return "战斗累计统计已清零。";
    }

    /// <summary>
    /// 控制台入口：直接取报告。控制台命令本身在游戏主线程执行，
    /// 因此无需跨线程 marshal；捕获异常避免命令把日志刷脏。
    /// </summary>
    public static string ReportOnMainThread(bool includeTotals)
    {
        try
        {
            return Report(includeTotals);
        }
        catch (Exception e)
        {
            return "读取战斗观测失败：" + e.Message;
        }
    }
}
