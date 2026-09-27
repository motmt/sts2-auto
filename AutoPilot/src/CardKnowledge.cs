using System;
using System.Collections.Generic;
using System.Linq;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;

namespace AutoPilot;

/// <summary>
/// 卡牌情报中心：把一张 <see cref="CardModel"/> 拆成**可计算的机制标签与真实数值**，
/// 供选卡评分、升级推荐、删卡推荐、遗物协同、牌组诊断统一使用。
///
/// 借鉴《尖塔军师》的思路（80+ 特征标签 → 流派原型 → 协同判定），
/// 但**不依赖联网数据**：我们用游戏本体的
///   · <c>CardModel.DynamicVars</c>（真实数值：伤害/格挡/抽牌/能量/毒/力量…）
///   · <c>CardModel.CardTags</c> / <c>CardModel.Keywords</c>（Strike/Defend/Exhaust/Ethereal…）
///   · <c>CardModel</c> 的 id 命名（游戏用统一命名法，如 STRIKE_IRONCLAD / DEFEND_SILENT）
/// 自己推出标签与流派归属，全程离线、可解释。
/// </summary>
public static class CardKnowledge
{
    // =====================================================================
    //  一、机制标签（CardTag 集合）
    // =====================================================================
    public enum Tag
    {
        Damage,        // 造成伤害
        Block,         // 提供格挡
        Draw,          // 抽牌
        Energy,        // 回/加能量
        MultiHit,      // 多段攻击
        Power,         // 能力牌（永久增益）
        AoE,           // 群体伤害
        Vulnerable,    // 施加易伤
        Weak,          // 施加虚弱
        Poison,        // 施加中毒
        Strength,      // 加力量
        Dexterity,     // 加敏捷
        Scaling,       // 成长型（每次打出变强/永久成长）
        Exhaust,       // 消耗
        Ethereal,      // 虚无（回合结束未打出则消耗）
        Innate,        // 固有（开局必在手）
        Retain,        // 保留
        Unplayable,    // 不可打出
        Curse,         // 诅咒
        Status,        // 状态
        StrikeLike,    // Strike 类
        DefendLike,    // Defend 类
        XCost,         // X 费
        HpCost,        // 自伤代价
        Heal,          // 回血
        GoldGain,      // 得金币
        Summon,        // 召唤
        Osty,          // Osty 相关
        Shiv,          // 小刀
        Star,          // 星星资源
        ZeroCost,      // 0 费
        HighCost,      // >=3 费
    }

    /// <summary>一张卡的完整画像。</summary>
    public sealed class CardProfile
    {
        public CardModel Card;
        public string Id = "";
        public string Name = "?";
        public CardType Type = CardType.Skill;
        public CardRarity Rarity = CardRarity.Common;
        public int Cost;
        public bool Upgraded;

        public int Damage;         // 期望单次伤害（用于估算）
        public int Block;
        public int Hits = 1;       // 命中次数
        public int Draw;
        public int EnergyGain;
        public int Poison;
        public int StrengthGain;
        public int DexterityGain;
        public int VulnerableStacks;
        public int WeakStacks;
        public int HealAmount;
        public int CardsGenerated; // 生成/加入手牌的卡数

        public HashSet<Tag> Tags = new HashSet<Tag>();

        /// <summary>总伤害（多段 × 命中数）。</summary>
        public int TotalDamage => Damage * Math.Max(1, Hits);

        public bool Has(Tag t) => Tags.Contains(t);

        /// <summary>标签的中文短语（供提示词/日志）。</summary>
        public string TagText
        {
            get
            {
                if (Tags.Count == 0) return "";
                return string.Join("/", Tags.OrderBy(t => t.ToString()).Select(TagCn).ToArray());
            }
        }
    }

    public static string TagCn(Tag t) => t switch
    {
        Tag.Damage => "伤害",
        Tag.Block => "格挡",
        Tag.Draw => "抽牌",
        Tag.Energy => "充能",
        Tag.MultiHit => "多段",
        Tag.Power => "能力",
        Tag.AoE => "群伤",
        Tag.Vulnerable => "易伤",
        Tag.Weak => "虚弱",
        Tag.Poison => "中毒",
        Tag.Strength => "力量",
        Tag.Dexterity => "敏捷",
        Tag.Scaling => "成长",
        Tag.Exhaust => "消耗",
        Tag.Ethereal => "虚无",
        Tag.Innate => "固有",
        Tag.Retain => "保留",
        Tag.Unplayable => "不可打",
        Tag.Curse => "诅咒",
        Tag.Status => "状态",
        Tag.StrikeLike => "打击类",
        Tag.DefendLike => "防御类",
        Tag.XCost => "X费",
        Tag.HpCost => "自伤",
        Tag.Heal => "治疗",
        Tag.GoldGain => "得金",
        Tag.Summon => "召唤",
        Tag.Osty => "Osty",
        Tag.Shiv => "小刀",
        Tag.Star => "星星",
        Tag.ZeroCost => "0费",
        Tag.HighCost => "高费",
        _ => t.ToString(),
    };

    // =====================================================================
    //  二、单卡画像提取
    // =====================================================================
    private static readonly Dictionary<string, CardProfile> _cache = new();
    private static readonly object _lock = new();

    public static CardProfile Profile(CardModel c)
    {
        if (c == null) return null;
        string key = CacheKey(c);
        lock (_lock)
        {
            if (_cache.TryGetValue(key, out var hit)) return hit;
        }

        var p = Build(c);
        lock (_lock)
        {
            if (_cache.Count > 4000) _cache.Clear();   // 简单容量保护
            _cache[key] = p;
        }
        return p;
    }

    private static string CacheKey(CardModel c)
    {
        try { return c.Id.Entry + (c.IsUpgraded ? "+" : ""); }
        catch { return Guid.NewGuid().ToString(); }
    }

    private static CardProfile Build(CardModel c)
    {
        var p = new CardProfile { Card = c, Type = CardType.Skill, Rarity = CardRarity.Common };
        try { p.Id = c.Id.Entry ?? ""; } catch { p.Id = ""; }
        try { p.Name = Brain.SafeTitle(c); } catch { p.Name = p.Id; }
        try { p.Type = c.Type; } catch { }
        try { p.Rarity = c.Rarity; } catch { }
        try { p.Upgraded = c.IsUpgraded; } catch { }
        p.Cost = GameState.SafeCost(c);

        string id = (p.Id ?? "").ToUpperInvariant();
        string pool = TryPool(c);

        // ---- ① 真实数值：DynamicVars（游戏本体已求值，最可靠） ----
        TryReadDynamicVars(c, p);

        // ---- ② 类型标签 ----
        if (p.Type == CardType.Attack) p.Tags.Add(Tag.Damage);
        if (p.Type == CardType.Power) p.Tags.Add(Tag.Power);
        if (p.Type == CardType.Curse) p.Tags.Add(Tag.Curse);
        if (p.Type == CardType.Status) p.Tags.Add(Tag.Status);

        // ---- ③ 关键词（CardKeyword） ----
        try
        {
            foreach (CardKeyword k in c.Keywords)
            {
                switch (k)
                {
                    case CardKeyword.Exhaust: p.Tags.Add(Tag.Exhaust); break;
                    case CardKeyword.Ethereal: p.Tags.Add(Tag.Ethereal); break;
                    case CardKeyword.Innate: p.Tags.Add(Tag.Innate); break;
                    case CardKeyword.Retain: p.Tags.Add(Tag.Retain); break;
                    case CardKeyword.Unplayable: p.Tags.Add(Tag.Unplayable); break;
                    case CardKeyword.Sly: p.Tags.Add(Tag.Shiv); break;
                }
            }
        }
        catch { }

        // ---- ④ 卡牌标签（CardTag） ----
        bool strikeTag = false, defendTag = false, shivTag = false;
        try
        {
            foreach (CardTag t in c.Tags)
            {
                switch (t)
                {
                    case CardTag.Strike: strikeTag = true; break;
                    case CardTag.Defend: defendTag = true; break;
                    case CardTag.Shiv: shivTag = true; break;
                    case CardTag.Minion: p.Tags.Add(Tag.Summon); break;
                    case CardTag.OstyAttack: p.Tags.Add(Tag.Osty); p.Tags.Add(Tag.Damage); break;
                }
            }
        }
        catch { }
        if (strikeTag) p.Tags.Add(Tag.StrikeLike);
        if (defendTag) p.Tags.Add(Tag.DefendLike);
        if (shivTag) p.Tags.Add(Tag.Shiv);

        // 关键词/命名补充（游戏命名法很规整，id 里就能看出机制）
        if (id.Contains("DEFEND")) p.Tags.Add(Tag.DefendLike);
        if (id.Contains("STRIKE")) p.Tags.Add(Tag.StrikeLike);
        if (id.Contains("SHIV")) p.Tags.Add(Tag.Shiv);

        // ---- ⑤ 费用标签 ----
        if (p.Cost <= 0) p.Tags.Add(Tag.ZeroCost);
        if (p.Cost >= 3) p.Tags.Add(Tag.HighCost);
        try { if (c.HasStarCostX) p.Tags.Add(Tag.Star); } catch { }
        if (id.Contains("_X") || id.EndsWith("X") || id.Contains("XCOST")) p.Tags.Add(Tag.XCost);

        // ---- ⑥ id 命名推断（补 DynamicVars 读不到的机制） ----
        InferFromId(id, pool, p);

        // ---- ⑦ 数值兜底（DynamicVars 读不到时按 id/类型给经验值） ----
        if (p.Damage <= 0 && p.Has(Tag.Damage) && p.Type == CardType.Attack)
            p.Damage = GuessBaseDamage(p.Id, p.Upgraded);

        return p;
    }

    private static string TryPool(CardModel c)
    {
        try { return c.Pool?.Id?.Entry ?? ""; } catch { return ""; }
    }

    /// <summary>从 DynamicVars 读真实数值。整块 try/catch，任一读不到不影响其它。</summary>
    private static void TryReadDynamicVars(CardModel c, CardProfile p)
    {
        DynamicVarSet vars;
        try { vars = c.DynamicVars; } catch { return; }
        if (vars == null) return;

        p.Damage = ReadInt(vars, "Damage");
        if (p.Damage == 0) p.Damage = ReadInt(vars, "CalculatedDamage");
        if (p.Damage == 0) p.Damage = ReadInt(vars, "OstyDamage");
        p.Damage += ReadInt(vars, "ExtraDamage");

        p.Block = ReadInt(vars, "Block");
        if (p.Block == 0) p.Block = ReadInt(vars, "CalculatedBlock");

        p.Draw = ReadInt(vars, "Cards");
        p.EnergyGain = ReadInt(vars, "Energy");
        p.Poison = ReadInt(vars, "PoisonPower");
        p.StrengthGain = ReadInt(vars, "StrengthPower");
        p.DexterityGain = ReadInt(vars, "DexterityPower");
        p.VulnerableStacks = ReadInt(vars, "VulnerablePower");
        p.WeakStacks = ReadInt(vars, "WeakPower");
        p.HealAmount = ReadInt(vars, "Heal");
        if (p.HealAmount == 0) p.HealAmount = ReadInt(vars, "HpLoss");

        int repeat = ReadInt(vars, "Repeat");
        if (repeat > 1) { p.Hits = repeat; p.Tags.Add(Tag.MultiHit); }

        int summon = ReadInt(vars, "Summon");
        if (summon > 0) p.Tags.Add(Tag.Summon);

        int forge = ReadInt(vars, "Forge");
        if (forge > 0) p.Tags.Add(Tag.Scaling);

        int gold = ReadInt(vars, "Gold");
        if (gold > 0) p.Tags.Add(Tag.GoldGain);

        // 由数值推导标签
        if (p.Damage > 0) p.Tags.Add(Tag.Damage);
        if (p.Block > 0) p.Tags.Add(Tag.Block);
        if (p.Draw > 0) p.Tags.Add(Tag.Draw);
        if (p.EnergyGain > 0) p.Tags.Add(Tag.Energy);
        if (p.Poison > 0) p.Tags.Add(Tag.Poison);
        if (p.StrengthGain > 0) p.Tags.Add(Tag.Strength);
        if (p.DexterityGain > 0) p.Tags.Add(Tag.Dexterity);
        if (p.VulnerableStacks > 0) p.Tags.Add(Tag.Vulnerable);
        if (p.WeakStacks > 0) p.Tags.Add(Tag.Weak);
        if (p.HealAmount > 0) p.Tags.Add(Tag.Heal);
    }

    private static int ReadInt(DynamicVarSet vars, string key)
    {
        try
        {
            if (!vars.ContainsKey(key)) return 0;
            var v = vars[key];
            return v == null ? 0 : v.IntValue;
        }
        catch { return 0; }
    }

    /// <summary>按 id 命名推断机制（覆盖 DynamicVars 表达不出的：群伤/易伤/虚弱/成长…）。</summary>
    private static void InferFromId(string id, string pool, CardProfile p)
    {
        string[] aoeKeys = { "CLEAVE", "WHIRLWIND", "THUNDERCLAP", "BREATH", "CARNAGE", "IMMOLATE", "ELECTRODYNAMICS", "DIE_DIE_DIE" };
        string[] vulnKeys = { "BASH", "THUNDERCLAP", "UPPERCUT", "SHRUG", "BREAK", "CRUSH_JOINTS" };
        string[] weakKeys = { "CLOTHESLINE", "INTIMIDATE", "LEG_SWEEP", "MALAISE", "PIERCING_WAIL" };
        string[] strKeys = { "INFLAME", "FLEX", "DEMON_FORM", "LIMIT_BREAK", "SPOT_WEAKNESS" };
        string[] dexKeys = { "FOOTWORK", "AFTER_IMAGE" };
        string[] scalingKeys = { "RAMPAGE", "PERFECTED_STRIKE", "HEAVY_BLADE", "BODY_SLAM", "BARRICADE", "CATALYST", "ENVENOM", "ACCURACY", "NOXIOUS_FUMES" };
        string[] hpCostKeys = { "BLOODLETTING", "OFFERING", "HEMOKINESIS", "RUPTURE", "COMBUST", "SEEING_RED" };
        string[] shivGen = { "BLADE_DANCE", "CLOAK_AND_DAGGER", "INFINITE_BLADES", "ACCURACY", "AFTER_IMAGE", "STORM_OF_STEEL" };
        string[] starKeys = { "STAR", "WISH", "ASTROLABE", "CONSTELLATION" };

        if (aoeKeys.Any(k => id.Contains(k))) p.Tags.Add(Tag.AoE);
        if (vulnKeys.Any(k => id.Contains(k))) p.Tags.Add(Tag.Vulnerable);
        if (weakKeys.Any(k => id.Contains(k))) p.Tags.Add(Tag.Weak);
        if (strKeys.Any(k => id.Contains(k))) p.Tags.Add(Tag.Strength);
        if (dexKeys.Any(k => id.Contains(k))) p.Tags.Add(Tag.Dexterity);
        if (scalingKeys.Any(k => id.Contains(k))) p.Tags.Add(Tag.Scaling);
        if (hpCostKeys.Any(k => id.Contains(k))) p.Tags.Add(Tag.HpCost);
        if (shivGen.Any(k => id.Contains(k))) p.Tags.Add(Tag.Shiv);
        if (starKeys.Any(k => id.Contains(k))) p.Tags.Add(Tag.Star);
        if (id.Contains("MULTI") || id.Contains("DOUBLE") || id.Contains("TRIPLE")) p.Tags.Add(Tag.MultiHit);
    }

    /// <summary>基础伤害经验值（仅在 DynamicVars 完全读不到时使用）。</summary>
    private static int GuessBaseDamage(string id, bool upgraded)
    {
        if (id.Contains("STRIKE")) return upgraded ? 9 : 6;
        if (id.Contains("DEFEND")) return 0;
        if (id.Contains("NEUTRALIZE")) return 3;
        if (id.Contains("BASH")) return upgraded ? 10 : 8;
        return upgraded ? 9 : 7;
    }

    // =====================================================================
    //  三、流派原型（Archetype）
    // =====================================================================
    public sealed class Archetype
    {
        public string Name = "";
        public string Pool = "";            // IRONCLAD / SILENT / DEFECT / WATCHER / ...
        public Tag[] CoreTags = Array.Empty<Tag>();     // 核心机制
        public string[] CoreIds = Array.Empty<string>(); // 核心牌命名关键字
        public string[] SupportIds = Array.Empty<string>();
    }

    private static readonly List<Archetype> _archetypes = new()
    {
        // ---------------- Ironclad 铁甲战士 ----------------
        new Archetype { Name="力量流", Pool="IRONCLAD",
            CoreTags=new[]{ Tag.Strength, Tag.Scaling },
            CoreIds=new[]{ "INFLAME","DEMON_FORM","LIMIT_BREAK","SPOT_WEAKNESS","FLEX" },
            SupportIds=new[]{ "HEAVY_BLADE","SWORD_BOOMERANG","WHIRLWIND","PUMMEL","STRIKE" } },
        new Archetype { Name="格挡/壁垒流", Pool="IRONCLAD",
            CoreTags=new[]{ Tag.Block, Tag.Scaling },
            CoreIds=new[]{ "BARRICADE","BODY_SLAM","ENTRENCH","JUGGERNAUT" },
            SupportIds=new[]{ "DEFEND","SHRUG","IRON_WAVE","FEEL_NO_PAIN" } },
        new Archetype { Name="自伤/血怒流", Pool="IRONCLAD",
            CoreTags=new[]{ Tag.HpCost, Tag.Strength },
            CoreIds=new[]{ "RUPTURE","BLOODLETTING","OFFERING","COMBUST","BRUTALITY" },
            SupportIds=new[]{ "HEMOKINESIS","SEEING_RED","BLOOD_FOR_BLOOD" } },
        new Archetype { Name="完美打击/打击流", Pool="IRONCLAD",
            CoreTags=new[]{ Tag.StrikeLike },
            CoreIds=new[]{ "PERFECTED_STRIKE" },
            SupportIds=new[]{ "STRIKE","POMMEL_STRIKE","TWIN_STRIKE","WILD_STRIKE" } },

        // ---------------- Silent 静默猎手 ----------------
        new Archetype { Name="中毒流", Pool="SILENT",
            CoreTags=new[]{ Tag.Poison, Tag.Scaling },
            CoreIds=new[]{ "NOXIOUS_FUMES","CATALYST","ENVENOM","CORPSE_EXPLOSION" },
            SupportIds=new[]{ "DEADLY_POISON","BOUNCING_FLASK","POISONED_STAB","CALTROPS" } },
        new Archetype { Name="小刀流", Pool="SILENT",
            CoreTags=new[]{ Tag.Shiv },
            CoreIds=new[]{ "ACCURACY","INFINITE_BLADES","AFTER_IMAGE" },
            SupportIds=new[]{ "BLADE_DANCE","CLOAK_AND_DAGGER","STORM_OF_STEEL","FINISHER" } },
        new Archetype { Name="抽牌/连击流", Pool="SILENT",
            CoreTags=new[]{ Tag.Draw, Tag.Energy },
            CoreIds=new[]{ "ADRENALINE","TACTICIAN","REFLEX","ACROBATICS" },
            SupportIds=new[]{ "PREPARED","BACKFLIP","CALCULATED_GAMBLE" } },
        new Archetype { Name="虚弱/减益流", Pool="SILENT",
            CoreTags=new[]{ Tag.Weak, Tag.Vulnerable },
            CoreIds=new[]{ "MALAISE","PIERCING_WAIL" },
            SupportIds=new[]{ "CLOTHESLINE","LEG_SWEEP","SUCKER_PUNCH" } },

        // ---------------- Defect 机煲 ----------------
        new Archetype { Name="充能球流", Pool="DEFECT",
            CoreTags=new[]{ Tag.Energy, Tag.Scaling },
            CoreIds=new[]{ "ELECTRODYNAMICS","LOOP","CAPACITOR","FISSION" },
            SupportIds=new[]{ "ZAP","DUALCAST","BALL_LIGHTNING","COLD_SNAP" } },
        new Archetype { Name="集中/力量流", Pool="DEFECT",
            CoreTags=new[]{ Tag.Strength, Tag.Scaling },
            CoreIds=new[]{ "DEFRAGMENT","BIASED_COGNITION","ECHO_FORM" },
            SupportIds=new[]{ "BLIZZARD","THUNDER_STRIKE","DOOM" } },
        new Archetype { Name="爪击流", Pool="DEFECT",
            CoreTags=new[]{ Tag.Scaling },
            CoreIds=new[]{ "CLAW","ALL_FOR_ONE" },
            SupportIds=new[]{ "SCRAPE","REBOUND","HYPERBEAM" } },

        // ---------------- Watcher 观者 ----------------
        new Archetype { Name="姿态/愤怒流", Pool="WATCHER",
            CoreTags=new[]{ Tag.Damage, Tag.Scaling },
            CoreIds=new[]{ "ERUPTION","VIGILANCE","MENTAL_FORTRESS","TANTRUM" },
            SupportIds=new[]{ "WRATH","CALM" } },
        new Archetype { Name="真言/神性流", Pool="WATCHER",
            CoreTags=new[]{ Tag.Energy, Tag.Scaling },
            CoreIds=new[]{ "DEVOTION","PRAY","WORSHIP","PROSTRATE" },
            SupportIds=new[]{ "OMNISCIENCE","VAULT","SCRAWL" } },
    };

    /// <summary>牌组里各流派的命中强度（0~1，已归一化），降序。</summary>
    public static List<(Archetype Arch, double Score, int Have, int Need)> DetectArchetypes(IEnumerable<CardModel> deck, int max = 6)
    {
        var list = new List<(Archetype, double, int, int)>();
        var cards = (deck ?? Enumerable.Empty<CardModel>()).Where(c => c != null).ToList();
        if (cards.Count == 0) return list;

        string pool = "";
        try { pool = cards.Select(c => { try { return c.Pool?.Id?.Entry ?? ""; } catch { return ""; } })
                          .FirstOrDefault(x => !string.IsNullOrEmpty(x)) ?? ""; } catch { }

        var profiles = cards.Select(Profile).Where(p => p != null).ToList();

        foreach (Archetype a in _archetypes)
        {
            // 职业不符不参与（若有明确职业）
            if (!string.IsNullOrEmpty(pool) && !string.IsNullOrEmpty(a.Pool) &&
                !pool.ToUpperInvariant().Contains(a.Pool)) continue;

            int have = 0, need = 0;
            foreach (var p in profiles)
            {
                bool core = a.CoreIds.Any(k => p.Id.Contains(k)) ||
                            (a.CoreTags.Length > 0 && a.CoreTags.Count(t => p.Has(t)) >= 2);
                bool support = a.SupportIds.Any(k => p.Id.Contains(k)) ||
                               (a.CoreTags.Length > 0 && a.CoreTags.Any(t => p.Has(t)));
                if (core) have += 3;
                else if (support) have += 1;
            }
            // 缺口 = 该流派"核心牌"里当前一张都没命中的数量
            need = a.CoreIds.Count(k => !profiles.Any(p => p.Id.Contains(k)));

            double score = have / (double)(have + need * 3 + 1);
            if (have > 0) list.Add((a, score, have, need));
        }

        return list.OrderByDescending(x => x.Item2).Take(max).ToList();
    }

    /// <summary>候选卡是否与该流派协同（返回协同强度 0~1）。</summary>
    public static double ArchetypeSynergy(CardProfile candidate, Archetype arch)
    {
        if (candidate == null || arch == null) return 0;
        double s = 0;
        if (arch.CoreIds.Any(k => candidate.Id.Contains(k))) s += 1.0;
        if (arch.SupportIds.Any(k => candidate.Id.Contains(k))) s += 0.6;
        if (arch.CoreTags.Length > 0)
        {
            int hits = arch.CoreTags.Count(t => candidate.Has(t));
            s += Math.Min(0.8, hits * 0.4);
        }
        return Math.Min(1.0, s);
    }

    /// <summary>遗物与卡组的协同度（0~1）：能吃到该遗物效果的牌数 ÷ 卡组张数。</summary>
    public static double RelicSynergy(string relicId, IReadOnlyList<CardProfile> deck)
    {
        if (string.IsNullOrEmpty(relicId) || deck == null || deck.Count == 0) return 0;
        string id = relicId.ToUpperInvariant();

        Tag[] needed;
        switch (id)
        {
            case var _ when id.Contains("SHIV"): needed = new[] { Tag.Shiv }; break;
            case var _ when id.Contains("POISON"): needed = new[] { Tag.Poison }; break;
            case var _ when id.Contains("STRENGTH"): needed = new[] { Tag.Strength }; break;
            case var _ when id.Contains("DEXTERITY"): needed = new[] { Tag.Dexterity }; break;
            case var _ when id.Contains("BLOCK") || id.Contains("ARMOR"): needed = new[] { Tag.Block }; break;
            case var _ when id.Contains("ATTACK"): needed = new[] { Tag.Damage }; break;
            case var _ when id.Contains("ENERGY"): needed = new[] { Tag.Energy }; break;
            case var _ when id.Contains("DRAW"): needed = new[] { Tag.Draw }; break;
            case var _ when id.Contains("EXHAUST"): needed = new[] { Tag.Exhaust }; break;
            case var _ when id.Contains("POWER"): needed = new[] { Tag.Power }; break;
            case var _ when id.Contains("VULNERABLE"): needed = new[] { Tag.Vulnerable }; break;
            case var _ when id.Contains("WEAK"): needed = new[] { Tag.Weak }; break;
            case var _ when id.Contains("STRIKE"): needed = new[] { Tag.StrikeLike }; break;
            case var _ when id.Contains("DEFEND"): needed = new[] { Tag.DefendLike }; break;
            case var _ when id.Contains("CURSE") || id.Contains("OMAMORI"): needed = new[] { Tag.Curse }; break;
            case var _ when id.Contains("STAR"): needed = new[] { Tag.Star }; break;
            case var _ when id.Contains("SUMMON") || id.Contains("MINION") || id.Contains("OSTY"): needed = new[] { Tag.Summon, Tag.Osty }; break;
            default: return 0;   // 与该遗物无明确协同关系
        }

        int count = deck.Count(p => p != null && needed.Any(t => p.Has(t)));
        return (double)count / deck.Count;
    }
}
