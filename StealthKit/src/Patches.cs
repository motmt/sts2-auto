using HarmonyLib;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Rewards;

namespace StealthKit;

/// <summary>
/// 在每次“休息点流程开始”(RestSiteSynchronizer.BeginRestSite)后，
/// 如果开启了 autoheal，就在本机安排一次自动回复(伪装成正常休息)。
/// 本补丁只存在于装了 StealthKit 的机器上，触发的是官方同步的 ChooseLocalOption，
/// 其它机器执行原生 HEAL 逻辑，因此不需要队友装。
/// </summary>
[HarmonyPatch(typeof(RestSiteSynchronizer), "BeginRestSite")]
public static class Patch_RestSiteBegin
{
    public static void Postfix()
    {
        try
        {
            Core.ScheduleAutoRest();
        }
        catch (System.Exception e)
        {
            ModEntry.Logger.Warn("postfix BeginRestSite error: " + e);
        }
    }
}

/// <summary>
/// 本地玩家把一层的 3 选 1 卡牌奖励“全跳过”时，若本模组刚在该层悄悄送过卡，
/// 就把本机历史里的“跳过3张”改写成“获得愿望卡+跳过2张”，看起来像正常选了一张。
/// 注意：这只会修正“你本机”的历史记录；队友机器上各自记录的那份跳过来历我们无法改写。
/// </summary>
[HarmonyPatch(typeof(CardReward), "OnSkipped")]
public static class Patch_CardRewardOnSkipped
{
    public static void Postfix(CardReward __instance)
    {
        try
        {
            Core.BeautifySkippedReward(__instance);
        }
        catch (System.Exception e)
        {
            ModEntry.Logger.Warn("postfix CardReward.OnSkipped error: " + e);
        }
    }
}

/// <summary>
/// 三选一奖励生成后，若该奖励的归属玩家设了“注入愿望”，把最后一格替换成愿望卡。
/// 该替换在“每台机器”上以相同输入执行一次 → 全队看到的三选一完全一致 → 选卡序号同步无虞。
/// </summary>
[HarmonyPatch(typeof(CardReward), "Populate")]
public static class Patch_CardRewardPopulate
{
    public static void Postfix(CardReward __instance)
    {
        try
        {
            Core.InjectWishIntoReward(__instance);
        }
        catch (System.Exception e)
        {
            ModEntry.Logger.Warn("postfix CardReward.Populate error: " + e);
        }
    }
}
