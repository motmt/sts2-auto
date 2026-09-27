using System;
using System.Collections.Generic;
using System.Text;

namespace AutoPilot;

/// <summary>
/// 怪物**机制**知识库（v2.6.0 新增，v2.6.1 按实测 Id 重写）—— 回答"这只怪有什么特殊行为"，
/// 补齐 spire-codex 数据（只有招式名/伤害/血量，**没有机制叙事**）的空白。
///
/// 血泪来源（用户实测）：AI 完全不懂怪物联动机制——
///   · 「噬尸蛞蝓」打死一只 → 其余同类**全部休眠(Stun)一回合**（下回合 0 承伤）但各 **+4/5 力量**；
///     且它的 GOOP 给的是**虚弱(Frail，格挡 −25%)**，AI 却按面板格挡估 → 战损算错；
///   · 「往生雕像」精英首回合**睡觉(0 伤害)**，AI 白白打防；
///   · 「胖哥布林」被**生成即眩晕、随后自己逃跑(Flee)**，AI 却把输出全砸在它身上。
/// 用户直言："不懂蛞蝓机制，打死一只其他都休眠，能够降低战损就不用打防了……这些问题都很影响"。
///
/// ⚠️ 键值来源（血泪）：<b>Id.Entry 不是猜的</b>。实测从 `godot.log` 的
/// `Monster &lt;ID&gt; performing move` 行与 `.decomp` 逐条核对得到，形如
/// `CORPSE_SLUG` / `BYGONE_EFFIGY` / `FAT_GREMLIN`（全大写下划线）。
/// 新增机制时先跑一局、grep 日志确认 id 再填表，**不要臆造**。
/// </summary>
public static class MonsterMechanics
{
    // =====================================================================
    //  机制表：monsterId(归一化) → 机制叙事
    // =====================================================================
    private static readonly Dictionary<string, string> Table =
        new(StringComparer.OrdinalIgnoreCase)
    {
        ["CORPSE_SLUG"] =
            "【噬尸蛞蝓·关键机制】\n" +
            "  ① **吞噬/休眠联动**：当场上有同类蛞蝓时，**任意一只死亡**会让**其余所有存活的蛞蝓**\n" +
            "     立刻进入「吞噬」状态——它们**下一回合全部休眠(Stun)，完全不行动、造成 0 伤害**，\n" +
            "     但**每只同时永久获得 +4 力量（进阶 +5）**（之后打得更疼）。\n" +
            "     ▶ 战术含义：\n" +
            "       · **可以在多只时主动优先击杀最脆的一只**，从而白嫖之后一整回合的 0 承伤；\n" +
            "       · 那个「0 承伤回合」**不需要打格挡牌**，应把全部能量用于输出/铺垫/能力牌；\n" +
            "       · 但**别把三只都养成大力量**：每死一只其余 +4 力量，越晚杀它们打得越疼，\n" +
            "         故应在「力量还没堆太高」时尽快清场，不要平均输出拖回合。\n" +
            "  ② **黏液(GOOP_MOVE)**：不造成伤害，但给你叠 **2 层虚弱(Frail)**——\n" +
            "     **虚弱期间你的格挡效果 −25%**。▶ 算战损时，若你身上有虚弱，\n" +
            "     打出的每张格挡牌要按 **×0.75 向下取整** 折算，不能按面板值算。\n" +
            "  ③ 招式固定循环：**鞭打(WHIP_SLAP 3×2=6) → 扑上(GLOMP 8 / 进阶 9) → 黏液(GOOP 叠虚弱) → 循环**。\n" +
            "     知道它下一步出什么，就能提前决定该挡还是该抢输出。\n" +
            "  ④ 初战开局：多只蛞蝓的**第一招各不相同**（游戏刻意错开），\n" +
            "     所以首回合承伤 = 各只首招之和，别以为它们同步。",

        ["BYGONE_EFFIGY"] =
            "【往生雕像·精英机制】\n" +
            "  ① **首回合睡觉(SLEEP_MOVE)**：完全不打人、造成 **0 伤害**。\n" +
            "     ▶ **第一回合不需要任何格挡**，应全力抢输出或铺能力牌（这是免费回合）。\n" +
            "  ② 第二回合**苏醒(WAKE_MOVE)**：不攻击，但自己 **+10 力量**。\n" +
            "  ③ 之后**永久循环重砍(SLASHES_MOVE) 13（进阶 15）伤害**，不会停。\n" +
            "     ▶ 含 SlowPower（每回合首次受击额外增伤）——**多打几次能让它受更多伤害**，\n" +
            "       但它的 13/15 伤害是持续且稳定的，必须在它 +10 力量前尽量压血。\n" +
            "  ④ 血量很高(127~132)，是一场持久战：前两回合是白给窗口，务必用来建立优势。",

        ["FAT_GREMLIN"] =
            "【胖哥布林·机制】\n" +
            "  ① **生成即眩晕(SPAWNED_MOVE / StunIntent)**：出场第一回合不动。\n" +
            "  ② 之后**自己逃跑(FLEE_MOVE / EscapeIntent)** —— **它不会被击杀，而是直接离场**。\n" +
            "     ▶ **不要浪费任何输出与铺垫在它身上**！它迟早会自己走，血量也毫无意义。\n" +
            "  ③ 血量很低(13~18)，看起来很好杀，但那纯属陷阱——**把伤害留给真正会打的敌人**。",

        ["SNEAKY_GREMLIN"] =
            "【鬼祟哥布林·机制】\n" +
            "  ① **生成即眩晕一回合(SPAWNED_MOVE)**，之后才会**猛扑(TACKLE_MOVE) 攻击**。\n" +
            "     ▶ 第一回合它不造成伤害，不用为它打防。",

        ["CALCIFIED_CULTIST"] =
            "【钙化信徒·机制】\n" +
            "  ① **起手必定「咒仪(INCANTATION_MOVE)」：+3 力量（RitualPower），本回合不打人**。\n" +
            "     ▶ 第一回合它 0 伤害 → **首回合不用为它打格挡**，全力抢输出/铺垫；\n" +
            "  ② 之后循环 **黑暗打击(DARK_STRIKE_MOVE)**，且力量每回合持续上涨。\n" +
            "     ▶ 必须**尽早集火杀掉**，拖得越久它打得越疼（多只信徒叠加更致命）。",

        ["LEAF_SLIME_M"] =
            "【叶史莱姆(大)·机制】招式固定交替：\n" +
            "  ① **黏液射击(STICKY_SHOT)**：不造成伤害，往你**弃牌堆塞 2 张「黏液」(Slimed)污染牌**。\n" +
            "     ▶ 这招 0 伤害，**该回合不需要为它打格挡**；但要注意牌堆被污染。\n" +
            "  ② **团块射击(CLUMP_SHOT)**：8（进阶 9）单段伤害。\n" +
            "  ③ 起手固定为 STICKY_SHOT（0 伤害），**首回合它是白给回合**。",

        ["LEAF_SLIME_S"] =
            "【叶史莱姆(小)·机制】\n" +
            "  ① **撞击(TACKLE_MOVE)**：单段攻击；\n" +
            "  ② **黏液(GOOP_MOVE)**：0 伤害，给你叠**虚弱(虚弱期间格挡 −25%)**。\n" +
            "  ③ 血量极低(约 12)，通常一两次攻击即可清理。",

        ["TWIG_SLIME_M"] =
            "【枝史莱姆(大)·机制】招式：\n" +
            "  ① **戳刺扑击(POKEY_POUNCE_MOVE)**：攻击；\n" +
            "  ② **黏液射击(STICKY_SHOT_MOVE)**：0 伤害，向你弃牌堆塞「黏液」污染牌。\n" +
            "  ③ 血量约 27。",

        ["TWIG_SLIME_S"] =
            "【枝史莱姆(小)·机制】只有 **撞击(TACKLE_MOVE)**，血量极低(约 8)，随手清理即可。",

        ["SHRINKER_BEETLE"] =
            "【缩身甲虫·机制】\n" +
            "  ① **缩身(SHRINKER_MOVE)**：给你叠 **ShrinkPower（缩小）**——\n" +
            "     **你的攻击伤害会按比例降低**（不是普通力量增益能抵的）。\n" +
            "     ▶ 被缩后应优先**防守/铺能力**，或先用高倍率牌打完，避免输出被浪费；\n" +
            "  ② 还有 **嚼咬(CHOMP_MOVE)** 与 **踩踏(STOMP_MOVE)** 两种攻击。\n" +
            "  ③ 血量约 40，优先在它反复缩你之前解决。",

        ["TOADPOLE"] =
            "【蝌蚪·机制】\n" +
            "  ① **尖刺喷洒(SPIKE_SPIT_MOVE)** / **旋涡(WHIRL_MOVE)** 攻击；\n" +
            "  ② **立刺(SPIKEN_MOVE)**：给自己叠 **尖刺(ThornsPower)**——\n" +
            "     **此后你每次用攻击牌打它都会被反弹伤害**。\n" +
            "     ▶ 它立刺后，要么用**非攻击牌**处理，要么**一次性高伤秒掉**，别用多段小攻击喂反伤。",

        ["SEWER_CLAM"] =
            "【下水道蛤蜊·机制】\n" +
            "  ① 出场自带 **装甲(PlatingPower) + 力量(StrengthPower)**，受伤会被装甲减免。\n" +
            "  ② 招式 **加压(PRESSURIZE_MOVE)** / **喷射(JET_MOVE)**。\n" +
            "     ▶ 血量较高(约 58)、有减伤，需要靠**高单段伤害**或**易伤削弱**来突破，别用多段小牌硬磨。",

        ["LIVING_FOG"] =
            "【活体雾·机制】\n" +
            "  ① 自带 **SmoggyPower（烟雾）**（受击会给攻击者施加负面效果）。\n" +
            "  ② 招式：**强化毒气(ADVANCED_GAS_MOVE)**、**膨胀(BLOAT_MOVE)**、**超级毒气弹(SUPER_GAS_BLAST_MOVE)**。\n" +
            "  ③ 血量高(约 82)，是消耗战——注意别被烟雾叠加的减益拖死，优先速战。",

        ["FUZZY_WURM_CRAWLER"] =
            "【绒毛蠕虫·机制】\n" +
            "  ① 招式 **酸液黏液(FIRST_ACID_GOOP / ACID_GOOP)** 会给你叠负面（虚弱类）；\n" +
            "  ② **吸气(INHALE)** 会给自己 **+力量(StrengthPower)**，越拖越强。\n" +
            "     ▶ 尽早集火杀掉，别给它多次吸气机会。",

        ["WRIGGLER"] =
            "【扭动虫·机制】\n" +
            "  ① **生成即眩晕一回合(SPAWNED_MOVE)**，之后 **恶咬(NASTY_BITE_MOVE)** / **扭动(WRIGGLE_MOVE)**；\n" +
            "  ② 会给自身/同伴 **+力量(StrengthPower)**。\n" +
            "     ▶ 首回合它不造成伤害；血量低(约 18)，趁早清理。",

        ["VINE_SHAMBLER"] =
            "【藤蔓行者·机制】\n" +
            "  ① 自带 **TangledPower（缠绕）**——会**束缚/限制你的出牌**（通常表现为施加负面牌或锁手牌）。\n" +
            "  ② 招式：**缠绕藤蔓(GRASPING_VINES_MOVE)**、**横扫(SWIPE_MOVE)**、**嚼咬(CHOMP_MOVE)**。\n" +
            "     ▶ 血量约 64，被缠住时输出受限，优先解控或速杀。",

        ["HAUNTED_SHIP"] =
            "【幽灵船·机制】\n" +
            "  ① 招式 **横扫(SWIPE_MOVE)** / **踩踏(STOMP_MOVE)** / **作祟(HAUNT_MOVE)**；\n" +
            "  ② **作祟(HAUNT_MOVE)** 会给你叠 **虚弱(WeakPower)**——\n" +
            "     **虚弱期间你的攻击伤害 −25%**。\n" +
            "     ▶ 被作祟后若攻击被削，可先防守/铺能力，等虚弱消退再爆发。",

        ["GREMLIN_MERC"] =
            "【哥布林佣兵·机制】\n" +
            "  ① 招式 **给我(GIMME_MOVE)** / **双重猛击(DOUBLE_SMASH_MOVE)** / **嘿嘿(HEHE_MOVE)**；\n" +
            "  ② 会给自身叠 **力量(StrengthPower)**，并带 **SurprisePower/虚弱(WeakPower)**。\n" +
            "     ▶ 血量中等(约 51)，拖久了它越打越疼，尽早处理。",

        ["PUNCH_CONSTRUCT"] =
            "【拳击构造体·机制】\n" +
            "  ① 自带 **神器(ArtifactPower)**（可免疫一次负面效果——**易伤/虚弱可能被吃掉**）；\n" +
            "  ② 招式 **准备(READY_MOVE)** / **重拳(STRONG_PUNCH_MOVE)** / **快拳(FAST_PUNCH_MOVE)**，\n" +
            "     并给你叠 **虚弱(FrailPower)**。\n" +
            "     ▶ 注意 Artifact：挂易伤前先确认它没有神器层数，否则易伤白挂。",

        ["CUBEX_CONSTRUCT"] =
            "【立方构造体·机制】\n" +
            "  ① 自带 **神器(ArtifactPower) + 力量(StrengthPower)**；\n" +
            "  ② 招式 **充能(CHARGE_UP_MOVE)** → **连射(REPEATER_BLAST_MOVE)** → **排斥(EXPEL_MOVE)**。\n" +
            "     ▶ Artifact 会免疫负面——**别指望用易伤/虚弱锁它**，应靠纯数值输出。",

        ["SKULKING_COLONY"] =
            "【潜行集群·机制】\n" +
            "  ① 出场自带 **硬化外壳(HardenedShellPower) 20 点**（等效减伤），受伤会被大幅削减。\n" +
            "     ▶ **必须用高单段伤害或先破甲**，多段小伤害会被外壳吃掉大半。\n" +
            "  ② 招式循环：**疾冲(ZOOM_MOVE 14/进阶16)** → **疾冲2** → **惯性(INERTIA_MOVE 9/进阶11 +2/进阶4力量)**\n" +
            "     → **穿刺(PIERCING_STABS_MOVE 7/进阶8 ×2)**。\n" +
            "  ③ 血量高(75~80)，且力量持续上涨 → 属于**速杀型消耗战**，需尽早解决。",

        ["BYRDONIS"] =
            "【鸟龙·机制】\n" +
            "  ① 自带 **领域(TerritorialPower)**——被激怒后会强化。\n" +
            "  ② 招式 **啄击(PECK_MOVE)** / **俯冲(SWOOP_MOVE)**。\n" +
            "  ③ 血量高(约 90)，注意别拖成持久战。",

        ["SEAPUNK"] =
            "【海朋克·机制】招式 **海踢(SEA_KICK_MOVE)** / **回旋踢(SPINNING_KICK_MOVE)** / **吐泡泡(BUBBLE_BURP_MOVE)**，\n" +
            "  且会给自身 **+力量(StrengthPower)**。▶ 持续上涨型，尽早清。",

        ["TERROR_EEL"] =
            "【恐鳗·机制】\n" +
            "  ① 招式 **冲撞(CRASH_MOVE)** / **鞭打(THRASH_MOVE)** / **眩晕(STUN_MOVE)** / **恐吓(TERROR_MOVE)**；\n" +
            "  ② 带 **尖啸(ShriekPower) / 活力(VigorPower) / 易伤(VulnerablePower)**——\n" +
            "     **它自己会给你挂易伤**，也带 Vigor（爆发增伤）。\n" +
            "  ③ 血量极高(约 150)，是 BOSS 级消耗战：注意 **STUN_MOVE 是它自己的 0 伤害回合**（可白嫖输出）。",

        ["WATERFALL_GIANT"] =
            "【瀑流巨人·Boss 机制】\n" +
            "  ① 招式极多：**加压(PRESSURIZE)** / **踩踏(STOMP)** / **冲撞(RAM)** / **虹吸(SIPHON)** /\n" +
            "     **压力枪(PRESSURE_GUN)** / **增压(PRESSURE_UP)** / **即将爆发(ABOUT_TO_BLOW)** / **爆炸(EXPLODE)**；\n" +
            "  ② 带 **蒸汽喷发(SteamEruptionPower)** 与给玩家的 **虚弱(WeakPower)**。\n" +
            "  ③ **ABOUT_TO_BLOW → EXPLODE 是蓄力大招**：见到「即将爆发」必须**全力打防或斩杀**，\n" +
            "     否则下回合爆炸伤害极高。血量 250，超长线 Boss 战。",
    };

    // =====================================================================
    //  API
    // =====================================================================

    /// <summary>归一化怪物 id（去命名空间前缀 / 去 '+' 后缀，转大写）。</summary>
    public static string Norm(string id)
    {
        if (string.IsNullOrEmpty(id)) return "";
        int i = id.LastIndexOf('.');
        if (i >= 0) id = id.Substring(i + 1);
        int j = id.IndexOf('+');
        if (j > 0) id = id.Substring(0, j);
        return id.Trim().ToUpperInvariant();
    }

    /// <summary>取单只怪的机制说明（无则返回空串）。</summary>
    public static string Describe(string monsterId)
    {
        string k = Norm(monsterId);
        return (!string.IsNullOrEmpty(k) && Table.TryGetValue(k, out var v)) ? v : "";
    }

    private static readonly Dictionary<string, string> ShortTable =
        new(StringComparer.OrdinalIgnoreCase)
    {
        ["CORPSE_SLUG"]   = "⚡机制：任一同伴死亡→其余休眠1回合(0伤)但各+4力；GOOP叠2虚弱(格挡-25%)",
        ["BYGONE_EFFIGY"] = "⚡机制：首回合睡觉0伤(白给)→次回合+10力→之后重砍13/15无限",
        ["FAT_GREMLIN"]   = "⚡机制：生成即眩晕→随后自己逃跑，别浪费输出去打它",
        ["SNEAKY_GREMLIN"]= "⚡机制：生成即眩晕1回合，之后才攻击",
        ["CALCIFIED_CULTIST"] = "⚡机制：起手咒仪+3力(0伤)，之后黑暗打击且力量持续上涨→需速杀",
        ["LEAF_SLIME_M"]  = "⚡机制：STICKY_SHOT 0伤(塞2张黏液牌)/CLUMP_SHOT 8-9伤，交替；首招0伤",
        ["LEAF_SLIME_S"]  = "⚡机制：撞击攻击/GOOP叠虚弱(格挡-25%)",
        ["TWIG_SLIME_M"]  = "⚡机制：戳刺攻击/黏液射击0伤塞黏液牌",
        ["TWIG_SLIME_S"]  = "⚡机制：只有撞击，血量极低",
        ["SHRINKER_BEETLE"] = "⚡机制：缩身→降低你的攻击伤害，被缩后宜防守或打高倍率牌",
        ["TOADPOLE"]      = "⚡机制：立刺→自身叠尖刺，之后你打它会被反伤，宜非攻击牌或一击秒",
        ["SEWER_CLAM"]    = "⚡机制：自带装甲(减伤)+力量，需高单段伤害/易伤突破",
        ["LIVING_FOG"]    = "⚡机制：自带烟雾(受击给攻击者负面)，高血量消耗战",
        ["FUZZY_WURM_CRAWLER"] = "⚡机制：吸气自+力量，越拖越强→速杀",
        ["WRIGGLER"]      = "⚡机制：生成即眩晕1回合，之后恶咬，血量低",
        ["VINE_SHAMBLER"] = "⚡机制：自带缠绕(限制出牌)→优先解控或速杀",
        ["HAUNTED_SHIP"]  = "⚡机制：作祟给你叠虚弱(攻击-25%)",
        ["GREMLIN_MERC"]  = "⚡机制：给自身叠力量，越拖越疼",
        ["PUNCH_CONSTRUCT"] = "⚡机制：自带神器(免疫一次负面，易伤/虚弱可能被吃)，会给你叠虚弱",
        ["CUBEX_CONSTRUCT"] = "⚡机制：自带神器+力量，负面免疫→靠纯数值输出",
        ["SKULKING_COLONY"] = "⚡机制：自带硬化外壳20(减伤)→需高单段/破甲；力量持续上涨→速杀",
        ["BYRDONIS"]      = "⚡机制：自带领域，被激怒强化；高血量",
        ["SEAPUNK"]       = "⚡机制：给自身叠力量，持续上涨→尽早清",
        ["TERROR_EEL"]    = "⚡机制：它会给你挂易伤；自身有STUN_MOVE=0伤回合可白嫖；血量150",
        ["WATERFALL_GIANT"] = "⚡机制：ABOUT_TO_BLOW→EXPLODE蓄力大招，见到必须全力防/斩杀；血量250",
    };

    /// <summary>单行机制摘要（挂在敌人明细那行后面，保证"序号↔机制"对应）。</summary>
    public static string ShortLine(string monsterId)
    {
        string k = Norm(monsterId);
        return (!string.IsNullOrEmpty(k) && ShortTable.TryGetValue(k, out var v)) ? v : "";
    }

    /// <summary>
    /// 给一批在场敌人生成机制提示段落（去重，只输出有机制的）。
    /// 注入到战斗提示词里，让 LLM 在**当下这场战斗**就遵守这些机制规则。
    /// </summary>
    public static string Prompt(IEnumerable<string> monsterIds)
    {
        if (monsterIds == null) return "";
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sb = new StringBuilder();
        foreach (var id in monsterIds)
        {
            string k = Norm(id);
            if (string.IsNullOrEmpty(k) || !seen.Add(k)) continue;
            if (Table.TryGetValue(k, out var v))
                sb.Append(v).Append('\n');
        }
        return sb.Length == 0 ? "" : "【本场敌人的特殊机制——务必按此判断战损与目标】\n" + sb;
    }

    /// <summary>该怪是否有已知机制（用于日志/诊断）。</summary>
    public static bool Has(string monsterId) => Table.ContainsKey(Norm(monsterId));

    /// <summary>已收录机制的怪物数量（诊断用）。</summary>
    public static int Count => Table.Count;
}
