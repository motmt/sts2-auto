using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;

namespace StealthKit;

/// <summary>StealthKit 模组入口（游戏加载器调用）。必须带 ModInitializer 标注才会被调用。</summary>
[ModInitializer("Init")]
public static class ModEntry
{
    public const string ModId = "StealthKit";

    public static Logger Logger { get; } = new Logger(ModId, LogType.Generic);

    public static void Init()
    {
        try
        {
            Core.Config.Load();
            Core.EnsureCombatHook();
            Harmony harmony = new(ModId);
            harmony.PatchAll(Assembly.GetExecutingAssembly());
            Logger.Info("StealthKit initialized. Config: " + Core.Config.Path);
        }
        catch (System.Exception e)
        {
            Logger.Error("StealthKit init failed: " + e);
        }
    }
}
