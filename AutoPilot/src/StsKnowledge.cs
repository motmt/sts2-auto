using System;

namespace AutoPilot;

/// <summary>
/// 《杀戮尖塔2》机制知识库（统一注入所有 LLM 提示词）。
///
/// 设计动机（v2.3.6）：此前机制规则散落在 <see cref="Brain"/>（战斗/选路）与
/// <see cref="RoomBrain"/>（篝火/商店/事件）各处，各写一份，容易互相矛盾且难以维护。
/// 现在把"游戏机制的硬事实"集中到这里，各决策点只拼接自己关心的片段，
/// 保证 AI 对机制的理解前后一致，也让规则可以被单点修订。
///
/// 编排：
///   · <see cref="Core"/>          —— 所有场景通用（费用、牌型、格挡、力量敏捷）
///   · <see cref="Combat"/>        —— Brain.LlmPlanTurnAsync（出牌）
///   · <see cref="CardPicking"/>   —— 选卡奖励 / 牌组操作
///   · <see cref="Map"/>           —— Brain.LlmChooseMapPoint（路线）
///   · <see cref="Rest"/> / <see cref="Shop"/> / <see cref="Event"/> —— RoomBrain
///
/// ⚠ 只写"确认无误的机制事实"，不写可调策略（策略留在各决策点的提示词里，
///   由 <see cref="PilotConfig"/> 控制）。
/// </summary>
public static class StsKnowledge
{
    /// <summary>战斗通用机制（出牌决策的核心前提）。</summary>
    public const string Combat =
        "【战斗机制】\n" +
        "· 能量是每回合有限的资源，回合开始时回满；没用完的能量不会累积到下一回合。\n" +
        "· 费用 0 的牌不消耗能量，可以无限次打出（只要还在手牌且未消耗），能打就打。\n" +
        "· 每回合结束时，若回合结束前有未使用的格挡，格挡会在下回合开始时清空（除非有壁垒类能力）。\n" +
        "· 攻击牌造成伤害；技能牌多数提供格挡或抽牌/加能量；能力牌(Power)打一次后永久生效、越早打越值钱。\n" +
        "· 状态牌(Status)和诅咒牌(Curse)通常是有害的，占手牌且多数不可打或打出有负面效果。\n" +
        "· 【易伤】(Vulnerable)：目标受到的攻击伤害 +50%。【虚弱】(Weak)：目标造成的伤害 -25%。\n" +
        "  先打易伤/虚弱牌铺垫，再打攻击牌才能吃到加成。\n" +
        "· 【力量】(Strength) 每点给每次攻击 +1 伤害；【敏捷】(Dexterity) 每点给每次格挡 +1。\n" +
        "· 多段攻击(如 ×3)每次命中都会单独计算力量与易伤加成，收益更高。\n" +
        "· 杀死敌人可以立即免除它本回合的意图（包括攻击），所以斩杀兼具进攻与防御价值。\n";

    /// <summary>费用相关的硬规则（用于修正"零费卡不打"的理解偏差）。</summary>
    public const string EnergyRules =
        "【费用判定规则——务必遵守】\n" +
        "· 手牌方括号 [费用N] 是**当前真实费用**（已含所有增益/减益修正）。\n" +
        "· 费用为 0：不消耗能量，任何时刻只要能打就应打（尤其是过牌/加能量/加格挡类）。\n" +
        "· 只有 费用>=1 的牌才需要从当前能量里扣除。规划时只用累加这些牌的费用。\n" +
        "· 出牌顺序：先 0 费牌 → 过牌/充能 → 铺垫(易伤/虚弱) → 高价值输出。\n" +
        "· 若手牌里有 0 费牌而你没打就结束回合，那是浪费。\n";

    /// <summary>选卡/牌组操作机制。</summary>
    public const string CardPicking =
        "【选卡机制】\n" +
        "· 卡组越小越稳定：牌越多越难抽到核心牌。\n" +
        "· 优先选能补足当前短板/能与已有牌联动的牌；纯防御或纯输出过多都会失衡。\n" +
        "· 稀有度(Rare > Uncommon > Common)通常代表强度，但不是唯一标准。\n" +
        "· 诅咒/状态牌应尽早移除（在商店或事件中可付费移除）。\n" +
        "· 升级优先给核心输出牌或关键能力牌。\n";

    /// <summary>路线规划机制。</summary>
    public const string Map =
        "【路线机制】\n" +
        "· 精英战(Elite)给遗物，但难度明显高于普通战斗；状态好时可打，残血时应避开。\n" +
        "· 休息点(RestSite)可回血(约 30% 最大生命)或升级卡牌；残血时价值极高。\n" +
        "· 商店(Shop)用金币买卡/遗物/药水，还可付费移除卡牌；金币充足(≥120)时价值高。\n" +
        "· 宝箱(Treasure)免费拿遗物，稳赚。\n" +
        "· 未知(Unknown)会随机变成事件/战斗/商店/宝箱等，有惊喜也有风险。\n" +
        "· 幕数越深、生命越低，越要选稳健路线。\n";

    /// <summary>篝火机制。</summary>
    public const string Rest =
        "【篝火机制】\n" +
        "· 休息：回复约 30% 最大生命。\n" +
        "· 打铁(升级)：将一张牌升级为强化版本，永久生效。\n" +
        "· 生命低于 50~60% 时休息通常优于升级；满血/高血时升级收益更高。\n";

    /// <summary>商店机制。</summary>
    public const string Shop =
        "【商店机制】\n" +
        "· 出售卡牌、遗物、药水，以及“移除卡牌”服务（价格随次数递增）。\n" +
        "· 遗物通常性价比最高，其次是能补短板的卡，再次是药水。\n" +
        "· 金币不够时不要乱花，留给后续的商店或事件。\n" +
        "· 卡组里诅咒/无用牌多时，优先付费移除。\n";

    /// <summary>事件机制。</summary>
    public const string Event =
        "【事件机制】\n" +
        "· 事件是随机剧情，选项各有代价与收益（回血/加钱/得卡/受伤/被诅咒等）。\n" +
        "· 生存第一：会导致死亡的选项绝对不选。\n" +
        "· 残血时优先选回血或安全的选项；满血时可博取高收益选项。\n";

    /// <summary>拼装：把通用核心 + 指定片段合并为一段系统提示前缀。</summary>
    public static string Compose(params string[] sections)
    {
        if (sections == null || sections.Length == 0) return "";
        var sb = new System.Text.StringBuilder();
        foreach (string s in sections)
        {
            if (!string.IsNullOrWhiteSpace(s)) sb.Append(s).Append('\n');
        }
        return sb.ToString();
    }
}
