using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;

namespace ConsoleSyncGuard;

/// <summary>
/// 模组入口：被游戏 Mod 加载器以反射方式调用（[ModInitializer]）。
/// 这里只负责应用 Harmony patch。
/// </summary>
[ModInitializer(nameof(Init))]
public static class ModEntry
{
    public const string ModId = "ConsoleSyncGuard";

    /// <summary>游戏日志（Generic 类别），便于与状态同步日志一起排查。</summary>
    public static Logger Logger { get; } = new Logger(ModId, LogType.Generic);

    /// <summary>GameSync 类别的日志（与 ChecksumTracker 同级），联机不同步排查更直观。</summary>
    public static Logger SyncLogger { get; } = new Logger(ModId, LogType.GameSync);

    public static void Init()
    {
        try
        {
            Harmony harmony = new(ModId);
            harmony.PatchAll(Assembly.GetExecutingAssembly());
            Logger.Info("ConsoleSyncGuard v0.1.0 initialized.");
            SyncLogger.Info("ConsoleSyncGuard initialized. Default mode: hostOnly (clients' state commands are intercepted).");
        }
        catch (System.Exception e)
        {
            Logger.Error("ConsoleSyncGuard failed to initialize: " + e);
        }
    }
}
