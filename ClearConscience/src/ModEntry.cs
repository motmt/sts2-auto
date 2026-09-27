using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;

namespace ClearConscience;

/// <summary>问心无愧（ClearConscience）模组入口。必须带 ModInitializer 标注才会被游戏调用。</summary>
[ModInitializer("Init")]
public static class ModEntry
{
    public const string ModId = "ClearConscience";

    public static Logger Logger { get; } = new Logger(ModId, LogType.Generic);

    public static void Init()
    {
        try
        {
            Guard.Load();
            Harmony harmony = new(ModId);
            harmony.PatchAll(Assembly.GetExecutingAssembly());
            Logger.Info("问心无愧 initialized. enabled=" + Guard.Enabled + " config=" + Guard.ConfigPath);
        }
        catch (System.Exception e)
        {
            Logger.Error("问心无愧 init failed: " + e);
        }
    }
}
