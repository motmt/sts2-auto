using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Nodes;

namespace AutoPilot;

/// <summary>
/// v2.8.0 —— 战斗托管桥（把战斗决策整体交给 CombatSolver 引擎）。
///
/// 设计动机：
///   本地 CombatLookahead 是"当前回合 DFS + 手写数值估算"的玩具；CombatSolver
///   （原作者 Torch1230）是"完整状态镜像 + Beam Search + 跨回合 + 真实 RNG 克隆"
///   的工业级求解器，差距是量级级的，调参数永远补不上。
///   因此本模组不再自己算战斗，而是**搭便车**：只要 CombatSolver 已加载，
///   就把战斗内决策整体托管给它（通过它的公开 API SetFullAuto），
///   AutoPilot 只保留它不做的部分：LLM 房间决策、地图选路、卡牌奖励、事件、商店、篝火。
///
/// 为什么用反射：
///   CombatSolver 是外部 mod（独立 DLL，依赖 RitsuLib），AutoPilot 不能在编译期引用它；
///   而且用户可能**没装** CombatSolver —— 那时必须优雅降级到本地逻辑，不能崩。
///   所以这里做一次性的反射探测 + 缓存，之后零开销调用。
///
/// 探测目标（均为 CombatSolver.dll 的 public static 成员）：
///   CombatSolver.SolverController.SetFullAuto(NGame, CombatState, bool)
///   CombatSolver.SolverController.AdoptCurrentRoute()
///   CombatSolver.SolverController.IsSearching          (get)
///   CombatSolver.SolverController.CanExecuteCurrentTurn(get)
///   CombatSolver.SolverController.HasRetainedRoute     (get)
///   CombatSolver.SolverController.IsApplyingCurrentTurn(get)
///   CombatSolver.Entry.Enabled                          (get)
/// </summary>
public static class SolverBridge
{
    private const string ControllerTypeName = "CombatSolver.SolverController";
    private const string EntryTypeName = "CombatSolver.Entry";

    private static bool _probed;
    private static bool _available;

    private static MethodInfo? _setFullAuto;      // (NGame, CombatState, bool) -> void
    private static MethodInfo? _adoptRoute;       // () -> void
    private static PropertyInfo? _isSearching;    // bool
    private static PropertyInfo? _canExecute;     // bool
    private static PropertyInfo? _hasRoute;       // bool
    private static PropertyInfo? _isApplying;     // bool
    private static PropertyInfo? _entryEnabled;   // bool

    /// <summary>CombatSolver 是否可用（已加载且开箱可调用 SetFullAuto）。</summary>
    public static bool Available
    {
        get
        {
            // v2.8.0 关键修正：mod 加载有先后顺序（AutoPilot #8 < CombatSolver #10），
            // 若在 Init 阶段一次性探测并缓存 "未找到"，就会**永久**判为不可用。
            // 因此只在"已找到"时锁定；没找到就每次重扫，直到它加载进来。
            if (_available) return true;
            EnsureProbed();
            return _available;
        }
    }

    /// <summary>是否已开启"战斗托管"（AutoPilot 把战斗交给了 CombatSolver）。</summary>
    public static bool Delegated { get; private set; }

    /// <summary>上一次托管失败原因（用于 ap solver 显示）。</summary>
    public static string LastError { get; private set; } = "";

    private static void EnsureProbed()
    {
        // 已确认可用 → 无需重扫
        if (_available) return;

        try
        {
            Assembly? asm = FindCombatSolverAssembly();
            if (asm == null)
            {
                _probed = true;   // 记录"探过但没找到"，但**不锁定** _available，下次还会重扫
                return;
            }

            Type? ctrl = asm.GetType(ControllerTypeName, throwOnError: false);
            if (ctrl == null)
            {
                // 兼容：类型可能被重命名，退化为同名搜索
                ctrl = asm.GetTypes().FirstOrDefault(t =>
                    t.Name.Equals("SolverController", StringComparison.Ordinal));
            }
            if (ctrl == null)
            {
                LastError = $"未在 {asm.GetName().Name} 中找到 SolverController";
                _probed = true;
                return;
            }

            const BindingFlags SF = BindingFlags.Public | BindingFlags.Static;

            _setFullAuto = ctrl.GetMethod("SetFullAuto", SF, null,
                new[] { typeof(NGame), typeof(CombatState), typeof(bool) }, null);
            _adoptRoute = ctrl.GetMethod("AdoptCurrentRoute", SF, null, Type.EmptyTypes, null);
            _isSearching = ctrl.GetProperty("IsSearching", SF);
            _canExecute = ctrl.GetProperty("CanExecuteCurrentTurn", SF);
            _hasRoute = ctrl.GetProperty("HasRetainedRoute", SF);
            _isApplying = ctrl.GetProperty("IsApplyingCurrentTurn", SF);

            Type? entry = asm.GetType(EntryTypeName, throwOnError: false);
            _entryEnabled = entry?.GetProperty("Enabled", SF);

            // 唯一硬性要求：SetFullAuto 必须可用。找到才锁定 _available=true。
            if (_setFullAuto != null)
            {
                _available = true;
                _probed = true;
                LastError = "";
            }
            else
            {
                _probed = true;
                LastError = "SolverController.SetFullAuto 未找到（版本不兼容？）";
            }
        }
        catch (Exception e)
        {
            _probed = true;
            LastError = e.Message;
        }
    }

    /// <summary>在已加载的程序集里找 CombatSolver。</summary>
    private static Assembly? FindCombatSolverAssembly()
    {
        try
        {
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                string n = a.GetName().Name ?? "";
                if (n.Equals("CombatSolver", StringComparison.OrdinalIgnoreCase))
                    return a;
                // 避免为探测而触发加载：只在已加载程序集里找
                try
                {
                    if (a.GetType(ControllerTypeName, throwOnError: false) != null) return a;
                }
                catch { }
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// 开启/关闭"战斗托管"：true → 让 CombatSolver 全自动接管本场战斗。
    /// 必须在游戏主线程调用（CombatSolver 内部 AssertMainThread）。
    /// </summary>
    public static bool SetDelegated(NGame host, CombatState state, bool on)
    {
        EnsureProbed();
        if (!_available || _setFullAuto == null)
        {
            if (on) LastError = LastError.Length > 0 ? LastError : "CombatSolver 未安装或不可用";
            Delegated = false;
            return false;
        }
        try
        {
            _setFullAuto.Invoke(null, new object?[] { host, state, on });
            Delegated = on;
            LastError = "";
            return true;
        }
        catch (TargetInvocationException tie)
        {
            LastError = (tie.InnerException ?? tie).Message;
            Delegated = false;
            return false;
        }
        catch (Exception e)
        {
            LastError = e.Message;
            Delegated = false;
            return false;
        }
    }

    /// <summary>仅执行"当前回合"路线（不进入连续全自动）。</summary>
    public static bool TryAdoptCurrentRoute()
    {
        EnsureProbed();
        if (!_available || _adoptRoute == null) return false;
        try { _adoptRoute.Invoke(null, null); return true; }
        catch (TargetInvocationException tie) { LastError = (tie.InnerException ?? tie).Message; return false; }
        catch (Exception e) { LastError = e.Message; return false; }
    }

    private static bool GetBool(PropertyInfo? p, bool fallback = false)
    {
        if (p == null) return fallback;
        try { return p.GetValue(null) is bool b && b; }
        catch { return fallback; }
    }

    /// <summary>CombatSolver 是否正在搜索。</summary>
    public static bool IsSearching => GetBool(_isSearching);
    /// <summary>CombatSolver 认为"当前回合有可执行路线"。</summary>
    public static bool CanExecuteCurrentTurn => GetBool(_canExecute);
    /// <summary>CombatSolver 是否保有本场路线。</summary>
    public static bool HasRetainedRoute => GetBool(_hasRoute);
    /// <summary>CombatSolver 是否正在执行路线。</summary>
    public static bool IsApplyingCurrentTurn => GetBool(_isApplying);
    /// <summary>CombatSolver 自身总开关是否启用。</summary>
    public static bool EntryEnabled => GetBool(_entryEnabled, true);

    /// <summary>一行状态摘要（ap solver 用）。</summary>
    public static string Describe()
    {
        EnsureProbed();
        if (!_available)
            return $"CombatSolver: 不可用（{(_probed ? "未加载或接口不匹配" : "尚未探测")}" +
                   (LastError.Length > 0 ? $"，{LastError}" : "") + "）";
        return $"CombatSolver: 可用 | 托管中={Delegated} 搜索中={IsSearching} " +
               $"可执行={CanExecuteCurrentTurn} 有路线={HasRetainedRoute} 执行中={IsApplyingCurrentTurn}";
    }
}
