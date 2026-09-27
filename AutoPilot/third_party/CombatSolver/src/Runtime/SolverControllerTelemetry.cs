using System;

namespace CombatSolver;

/// <summary>
/// SolverController 的**融合仓库补充分部** —— 只为对外暴露一份只读遥测快照。
///
/// 为什么单开文件：
///   上游 SolverController.cs 保持逐字不改，升级时只需重新应用 UI 抑制补丁；
///   遥测部分集中在本文件与 SolverTelemetry.cs，diff 一目了然。
///
/// 注意：本类型与 SolverController 同为 internal，方法为 public static，
///       AutoPilot 侧通过反射调用（AutoPilot 不能编译期引用本程序集）。
/// </summary>
internal static partial class SolverController
{
    /// <summary>
    /// 取一份当前的求解遥测快照（纯数据、不可变、无 UI 依赖）。
    /// 必须在游戏主线程调用（会读 CombatManager 状态）。
    /// </summary>
    public static SolverTelemetrySnapshot CaptureTelemetry()
    {
        SolverTelemetrySnapshot snapshot = SolverTelemetrySnapshot.Build(
            _combat, _search, _deployment, _solverDisabled);
        BattleDamageTracker.RecordSearchElapsed(_combat.LatestResult);
        return snapshot;
    }
}
