using System;
using System.IO;
using System.Reflection;
using System.Text.Json;

namespace ClearConscience;

/// <summary>
/// 问心无愧核心状态。
///
/// 原理（基于 v0.111 反编译）：
///   RunManager.OnEnded(bool isVictory) 是每一局结束的唯一入口：
///     - 战败：CreatureCmd → OnEnded(isVictory:false)
///     - 胜利：RunManager → OnEnded(isVictory:true)
///     - 主动放弃：先 IsAbandoned=true，再走 OnEnded(false)
///   结束流程里会调用 RunSaveManager.DeleteCurrentRun()（单机）或
///   DeleteCurrentMultiplayerRun()（房主），把 run 存档与 .backup 一起删掉 —— 于是『继续/读取』按钮消失。
///
///   我们在 OnEnded 前记下『这次是战败』，然后在删除方法的 Prefix 里跳过那一次删除：
///   存档保留 → 单机主菜单『继续』、联机多人菜单『读取』重新可用 → 从本层重打。
///   联机时 run 存档只在房主端，所以【房主保档 = 全队回档】，状态依旧全端一致，不会不同步。
/// </summary>
public static class Guard
{
    /// <summary>是否启用（默认开）。战败时会在结算屏出现『问心无愧』按钮，点了才保档；不点直接返回主菜单则正常清档。</summary>
    public static bool Enabled = true;

    /// <summary>本次 run 结束是否为『战败』（胜利/主动放弃为 false）。</summary>
    public static bool LastEndWasDefeat;

    /// <summary>战败已暂缓清档，等待结算屏『问心无愧』按钮做决定。</summary>
    public static bool PendingDefeatDecision;

    /// <summary>玩家点过『问心无愧』：保留存档。</summary>
    public static bool KeepSaveRequested;

    /// <summary>点了『问心无愧』后自动继续（单机）：返回主菜单后自动点『继续』，直达本层。</summary>
    public static bool AutoContinue;

    /// <summary>内部穿透标记：需要真正执行删除时置位（跳过暂缓逻辑）。</summary>
    public static bool ForceDelete;

    /// <summary>最近一次 run 是否为胜利（仅用于日志/状态展示）。</summary>
    public static bool LastEndWasVictory;

    public static readonly string ConfigPath = Path.Combine(
        Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? ".",
        "ClearConscience_config.json");

    public static void Load()
    {
        try
        {
            if (!File.Exists(ConfigPath)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(ConfigPath));
            if (doc.RootElement.TryGetProperty("enabled", out var e)) Enabled = e.GetBoolean();
        }
        catch (Exception ex)
        {
            ModEntry.Logger.Warn("config load failed: " + ex);
        }
    }

    public static void Save()
    {
        try
        {
            var obj = new { enabled = Enabled };
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(obj, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            ModEntry.Logger.Warn("config save failed: " + ex);
        }
    }

    public static void Note(string msg) => ModEntry.Logger.Info("[问心无愧] " + msg);

    /// <summary>
    /// 删除存档时的统一判定：返回 true = 放行（真删）；false = 拦截（暂缓）。
    /// 只有『战败』这一种情况会被暂缓，交给结算屏按钮决定。
    /// </summary>
    public static bool HandleDeleteAttempt(string kind)
    {
        if (ForceDelete) return true;       // 内部要求真删（玩家没点按钮）
        if (!LastEndWasDefeat) return true; // 胜利 / 主动放弃 / 菜单清理 → 正常删除
        LastEndWasDefeat = false;           // 一次性
        PendingDefeatDecision = true;
        KeepSaveRequested = false;
        Note(kind + "：战败 → 暂缓清档，等待结算屏『问心无愧』按钮决定");
        return false;
    }

    public static string Status()
    {
        return "问心无愧 v1.1.0\n" +
               "  状态: " + (Enabled ? "开启" : "关闭（战败正常清档）") + "\n" +
               "  最近一次结束: " + (LastEndWasVictory ? "胜利" : "未胜利/未发生") + "\n" +
               "  待决定: " + (PendingDefeatDecision ? (KeepSaveRequested ? "已选择保留" : "等待结算屏按钮") : "无") + "\n" +
               "  用法: cc on | cc off | cc\n" +
               "  效果: 战败时结算屏会出现『问心无愧』按钮——点它 = 保留存档\n" +
               "        （单机回主菜单『继续』/ 联机多人菜单『读取』，回到本层重打）；\n" +
               "        不点、直接点『返回主菜单』= 正常清档；胜利与主动放弃也正常清档。\n" +
               "        联机时只需房主安装（存档只在房主端）。";
    }
}
