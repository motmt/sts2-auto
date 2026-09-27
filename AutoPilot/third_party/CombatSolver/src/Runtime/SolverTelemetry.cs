using System;
using System.Collections.Generic;
using System.Linq;

namespace CombatSolver;

/// <summary>
/// 本局战斗累计统计（可由 AutoPilot 跨场累加，故只给"本场"口径）。
/// </summary>
internal readonly record struct SolverBattleStats(
    int FightsStarted,
    int SearchesStarted,
    long ReviewedWorldlines);

/// <summary>
/// 战斗求解遥测快照 —— 供外部宿主（AutoPilot）读取，替代被隐藏的求解器浮层。
///
/// 设计动机：
///   求解器浮层（SolverOverlay）承载了"战损 / 搜索耗时 / 世界线吞吐"这些关键
///   可观测信息；浮层被 AutoPilot 隐藏后，这些信息在游戏里就再也看不到了。
///   本类型把这些数据固化成一份**不可变的纯数据快照**，由 AutoPilot 通过反射
///   取出并显示在自己的控制台上。
///
/// 为什么单独开文件而不是往 SolverResult 上加方法：
///   本文件是**融合仓库新增文件**，升级上游 CombatSolver 时不会被覆盖，
///   改动面最小（其它源文件保持与原版逐字一致，便于 diff 与回滚）。
/// </summary>
public readonly record struct SolverTelemetrySnapshot(
    int Version,
    bool CombatActive,
    string Phase,
    bool FullAutoEnabled,
    bool RouteFrozen,
    bool IsSearching,
    bool IsApplying,
    bool HasResult,

    int StartTurn,
    int SearchedTurns,
    int SearchBoundary,
    string SearchBoundaryCn,

    int HpLostThisFight,
    int ProjectedBattleHpLost,
    bool ProjectedBattleHpLossKnown,
    bool OnlyDeathRoutesFound,
    int UnavoidableHpLost,
    int SoldHp,
    int FutureSoldHp,

    double SearchSeconds,
    double SearchSecondsTotal,
    double RouteSeconds,
    long ExpandedNodes,
    int TurnExpandedNodes,
    long ReviewedWorldlines,

    int CurrentTurn,
    int CurrentTurnHpLoss,
    int CurrentTurnHpRecovered,
    int CurrentTurnEnergyLeft)
{
    /// <summary>
    /// 由 SolverController 调用；必须传入其静态会话字段。
    /// </summary>
    internal static SolverTelemetrySnapshot Build(
        SolverCombatSession combat,
        SolverSearchSession? search,
        SolverDeploymentSession? deployment,
        bool solverDisabled)
    {
        SolverResult? result = combat.LatestResult;

        bool combatActive = false;
        int currentTurn = 0;
        int curLoss = 0;
        int curRecovered = 0;
        int curEnergyLeft = 0;
        try
        {
            var state = MegaCrit.Sts2.Core.Combat.CombatManager.Instance.DebugOnlyGetState();
            combatActive = state != null && MegaCrit.Sts2.Core.Combat.CombatManager.Instance.IsInProgress;
            if (state != null)
            {
                var player = state.Players.Count == 1 ? state.Players[0] : null;
                currentTurn = player?.PlayerCombatState?.TurnNumber ?? 0;
                if (result != null && currentTurn > 0)
                {
                    curLoss = result.HpLostByTurn.GetValueOrDefault(currentTurn);
                    curRecovered = result.HpRecoveredByTurn.GetValueOrDefault(currentTurn);
                    curEnergyLeft = result.EnergyLeftByTurn.GetValueOrDefault(currentTurn);
                }
            }
        }
        catch { }

        string phase = solverDisabled ? "已禁用"
            : deployment != null ? "执行路线"
            : search != null ? "搜索中"
            : result != null ? "方案就绪"
            : "待机";

        return new SolverTelemetrySnapshot(
            Version: 1,
            CombatActive: combatActive,
            Phase: phase,
            FullAutoEnabled: combat.FullAutoEnabled,
            RouteFrozen: combat.RouteFrozen,
            IsSearching: search != null,
            IsApplying: deployment != null,
            HasResult: result != null,

            StartTurn: result?.StartTurnNumber ?? 0,
            SearchedTurns: result?.SearchedTurns ?? 0,
            SearchBoundary: result is null ? 0 : (int)result.BoundaryReason,
            SearchBoundaryCn: BoundaryText(result?.BoundaryReason),

            HpLostThisFight: BattleDamageTracker.HpLostSoFar,
            ProjectedBattleHpLost: result?.ProjectedBattleHpLost ?? 0,
            ProjectedBattleHpLossKnown: result?.CombatEndedTurn.HasValue ?? false,
            OnlyDeathRoutesFound: result?.OnlyDeathRoutesFound ?? false,
            UnavoidableHpLost: result?.UnavoidableHpLost ?? 0,
            SoldHp: result?.SoldHp ?? 0,
            FutureSoldHp: result?.FutureSoldHp ?? 0,

            SearchSeconds: result?.TotalSearchElapsed.TotalSeconds ?? 0d,
            SearchSecondsTotal: BattleDamageTracker.SearchElapsedTotal.TotalSeconds,
            RouteSeconds: result?.Elapsed.TotalSeconds ?? 0d,
            ExpandedNodes: result is null ? 0 : Math.Max(result.TotalExpandedNodes, result.ExpandedNodes),
            TurnExpandedNodes: result?.ExpandedNodes ?? 0,
            ReviewedWorldlines: combat.ReviewedWorldlinesTotal,

            CurrentTurn: currentTurn,
            CurrentTurnHpLoss: curLoss,
            CurrentTurnHpRecovered: curRecovered,
            CurrentTurnEnergyLeft: curEnergyLeft);
    }

    private static string BoundaryText(SearchBoundaryReason? reason) => reason switch
    {
        SearchBoundaryReason.Shuffle => "下次洗牌",
        SearchBoundaryReason.NoCards => "无牌可抽",
        SearchBoundaryReason.UnsupportedEffect => "未镜像死亡效果",
        SearchBoundaryReason.DynamicResolution => "真实结算后重搜",
        SearchBoundaryReason.PendingChoice => "展开选牌",
        SearchBoundaryReason.EventDefeat => "事件挑战失败",
        SearchBoundaryReason.TurnLimit => "回合上限",
        SearchBoundaryReason.NodeLimit => "节点上限",
        SearchBoundaryReason.TimeLimit => "时间预算",
        SearchBoundaryReason.MemoryNoProgress => "内存无进展",
        SearchBoundaryReason.None => "战斗结束",
        null => "-",
        _ => reason.Value.ToString(),
    };
}
