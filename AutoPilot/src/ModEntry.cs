using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;

namespace AutoPilot;

/// <summary>AutoPilot 模组入口（游戏加载器调用）。必须带 ModInitializer 标注才会被调用。</summary>
[ModInitializer("Init")]
public static class ModEntry
{
    public const string ModId = "AutoPilot";

    public static Logger Logger { get; } = new Logger(ModId, LogType.Generic);

    public static void Init()
    {
        try
        {
            PilotConfig.Load();
            Pilot.Log("=== AutoPilot 已加载，配置: " + PilotConfig.Path);
            RunStats.EnsureLoaded();
            Pilot.AttachHooks();
            Harmony harmony = new(ModId);
            harmony.PatchAll(Assembly.GetExecutingAssembly());
            Pilot.Log("Harmony 补丁已应用。输入 ap 查看菜单。");

            // v2.8.0：战斗引擎配置。注意此处**不判定 CombatSolver 是否可用** ——
            // mod 加载有先后（AutoPilot 早于 CombatSolver），此刻探测必然"未加载"。
            // 桥接层是惰性 + 可重试的，等真正进战斗时自然会探到。
            Pilot.Log("⚔ 战斗引擎=" + PilotConfig.Current.CombatEngine + "（CombatSolver 惰性探测）");
        }
        catch (System.Exception e)
        {
            Logger.Error("AutoPilot init failed: " + e);
        }
    }
}
