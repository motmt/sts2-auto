using System;
using HarmonyLib;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes.Screens.GameOverScreen;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Managers;

namespace ClearConscience;

/// <summary>
/// 记录『这一局是怎么结束的』：只有战败才走"暂缓清档 + 结算屏按钮决定"的流程。
/// 顺序保证：主动放弃会先设置 RunManager.IsAbandoned = true，再走向 OnEnded，所以这里能读出来。
/// </summary>
[HarmonyPatch(typeof(RunManager), "OnEnded")]
public static class Patch_RunManagerOnEnded
{
    public static void Prefix(bool isVictory)
    {
        try
        {
            bool abandoned = false;
            try
            {
                abandoned = RunManager.Instance != null && RunManager.Instance.IsAbandoned;
            }
            catch (Exception)
            {
                // 取不到就按未放弃处理
            }
            Guard.LastEndWasVictory = isVictory;
            Guard.LastEndWasDefeat = Guard.Enabled && !isVictory && !abandoned;
            Guard.PendingDefeatDecision = false;
            Guard.KeepSaveRequested = false;
            Guard.Note("run 结束: victory=" + isVictory + " abandoned=" + abandoned +
                       " -> " + (Guard.LastEndWasDefeat ? "战败(稍后由结算屏按钮决定是否保档)" : "正常清档"));
        }
        catch (Exception e)
        {
            ModEntry.Logger.Warn("OnEnded prefix error: " + e);
        }
    }
}

/// <summary>单机：战败时先不删 current_run.save（及其 .backup），把决定权交给结算屏按钮。</summary>
[HarmonyPatch(typeof(RunSaveManager), "DeleteCurrentRun")]
public static class Patch_DeleteCurrentRun
{
    public static bool Prefix()
    {
        try
        {
            return Guard.HandleDeleteAttempt("单机");
        }
        catch (Exception e)
        {
            ModEntry.Logger.Warn("DeleteCurrentRun prefix error: " + e);
            return true;
        }
    }
}

/// <summary>联机：run 存档只在房主端，同样暂缓，等房主在结算屏做决定。</summary>
[HarmonyPatch(typeof(RunSaveManager), "DeleteCurrentMultiplayerRun")]
public static class Patch_DeleteCurrentMultiplayerRun
{
    public static bool Prefix()
    {
        try
        {
            return Guard.HandleDeleteAttempt("联机");
        }
        catch (Exception e)
        {
            ModEntry.Logger.Warn("DeleteCurrentMultiplayerRun prefix error: " + e);
            return true;
        }
    }
}

/// <summary>结算屏出现时（仅战败且已暂缓），在上面加一个『问心无愧』按钮。</summary>
[HarmonyPatch(typeof(NGameOverScreen), "_Ready")]
public static class Patch_GameOverScreenReady
{
    public static void Postfix(NGameOverScreen __instance)
    {
        try
        {
            UiInject.TryInject(__instance);
        }
        catch (Exception e)
        {
            ModEntry.Logger.Warn("GameOverScreen _Ready postfix error: " + e);
        }
    }
}

/// <summary>
/// 玩家点『返回主菜单』时做最终决定：
///   - 点过『问心无愧』→ 保留存档；
///   - 没点 → 按塔一的做法正常清档。
/// </summary>
[HarmonyPatch(typeof(NGameOverScreen), "OnMainMenuButtonPressed")]
public static class Patch_MainMenuButtonPressed
{
    public static void Prefix(NGameOverScreen __instance)
    {
        try
        {
            if (!Guard.PendingDefeatDecision) return;
            Guard.PendingDefeatDecision = false;
            if (Guard.KeepSaveRequested)
            {
                Guard.KeepSaveRequested = false;
                Guard.Note("玩家选择『问心无愧』：保留存档（可在主菜单/多人菜单继续）");
                return;
            }
            // 未选择 → 正常清档（穿透我们自己的暂缓逻辑）
            try
            {
                Guard.ForceDelete = true;
                SaveManager sm = SaveManager.Instance;
                if (sm != null)
                {
                    RunManager rm = RunManager.Instance;
                    bool hostLike = rm?.NetService != null && rm.NetService.Type == NetGameType.Host;
                    if (hostLike) sm.DeleteCurrentMultiplayerRun();
                    else sm.DeleteCurrentRun();
                }
            }
            finally
            {
                Guard.ForceDelete = false;
            }
            Guard.Note("未选择『问心无愧』：已正常清档");
        }
        catch (Exception e)
        {
            ModEntry.Logger.Warn("OnMainMenuButtonPressed prefix error: " + e);
        }
    }
}

/// <summary>
/// 主菜单刷新按钮后（存档仍在 → 『继续』已可用）：若玩家点过『问心无愧』，
/// 就自动帮他把『继续』点掉，直接从结算屏回到本层战斗，不用手动选。
/// </summary>
[HarmonyPatch(typeof(NMainMenu), "RefreshButtons")]
public static class Patch_MainMenuRefreshButtons
{
    public static void Postfix(NMainMenu __instance)
    {
        try
        {
            if (!Guard.AutoContinue) return;
            Guard.AutoContinue = false; // 只自动继续一次
            UiInject.StartAutoContinue(__instance);
        }
        catch (Exception e)
        {
            ModEntry.Logger.Warn("RefreshButtons postfix error: " + e);
        }
    }
}
