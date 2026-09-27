using System;
using System.Collections.Generic;
using System.Linq;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;

namespace AutoPilot;

/// <summary>
/// 卡牌机制引擎 —— 解决“AI 对牌的理解太差”这一类问题。
///
/// 问题现场（v2.4.1 用户反馈）：
///   · 明明手上一张格挡都没有，AI 却打出**全身撞击**（BodySlam，伤害 = 当前格挡值）→ 打出 0 伤害；
///   · 同时该防御时不防御。
///
/// 根因：<see cref="CardKnowledge"/> 只会读 `DynamicVars` 里的**静态数值**，而全身撞击这类牌的
/// 伤害是**计算得出**的（`CalculatedDamageVar` = 基础 0 + 额外×当前格挡，见
/// `MegaCrit.Sts2.Core.Models.Cards.BodySlam` 与 `CalculatedVar.Calculate(target)`），
/// `DynamicVarSet.CalculatedDamage` 的 `IntValue` 只返回 base 值（=0）→ 画像显示“伤害 0”，
/// 但本地 <see cref="Brain.ScoreCard"/> 又把它当成普通攻击牌 +50 分，于是被优先打出。
///
/// 本类做两件事：
///   ① **条件机制库**：登记“伤害/价值依赖某个前置条件”的牌，给出**人类可读的条件说明**，
///      并按当前局面**实际求值**该条件是否满足（够不够、值不值）；
///   ② **可打性门控**：`Evaluate` 返回是否该打 + 该加减多少分 + 一句给 LLM 的解释，
///      由 <see cref="Brain"/> 在本地评分与提示词两处统一消费，保证“本地规则”与“大模型”
///      对同一张牌的理解一致。
///
/// 设计原则：只收录**已确认的机制**（读反编译源码 + id 命名），读不到的牌一律放行（不影响原行为）。
/// </summary>
public static class CardMechanics
{
    /// <summary>一次条件求值的结果。</summary>
    public sealed class Mech
    {
        public string CardId = "";
        public string CardName = "";
        /// <summary>机制的中文说明，如“伤害 = 当前格挡值（没有格挡就是 0 伤害）”。</summary>
        public string Rule = "";
        /// <summary>当前局面的实际结论，如“当前格挡 0 → 本牌伤害 0，不值得打”。</summary>
        public string Status = "";
        /// <summary>是否满足“打出有价值”的前置条件。</summary>
        public bool Useful = true;
        /// <summary>建议的分数修正（负=不该打，正=很该打）。0 表示中性。</summary>
        public double ScoreDelta;
    }

    /// <summary>
    /// 主入口：结合当前局面评估一张牌的**条件机制**。
    /// 返回 null 表示该牌没有登记特殊条件（按普通牌处理）。
    /// </summary>
    public static Mech Evaluate(CardModel c, Player me)
    {
        try
        {
            if (c == null || me == null) return null;
            string id = SafeId(c);
            if (string.IsNullOrEmpty(id)) return null;
            string up = id.ToUpperInvariant();

            int block = BlockOf(me);
            int hp = HpOf(me);
            int maxHp = MaxHpOf(me);
            int hand = HandCount(me);
            int energy = EnergyOf(me);
            int deck = DeckCount(me);
            int enemyCount = AliveEnemyCount(me);
            int enemyHp = TotalEnemyHp(me);

            // -----------------------------------------------------------------
            // 1) 全身撞击 / 盾击类：伤害 = 当前格挡值
            //    反编译 BodySlam.cs: CalculatedDamageVar = 0 + 1×(card.Owner.Creature.Block)
            // -----------------------------------------------------------------
            if (up.Contains("BODY_SLAM") || up.Contains("BODYSLAM"))
            {
                var m = New(c, "伤害 = 打出时你的**当前格挡值**（没有格挡 = 0 伤害）");
                m.ScoreDelta = -999;
                m.Useful = false;
                if (block <= 0)
                {
                    m.Status = $"当前格挡 0 → 本牌实际伤害 0，等同空过，**绝不能打**";
                }
                else if (block < 6 && enemyHp > block)
                {
                    m.Status = $"当前格挡仅 {block} → 伤害 {block}，收益很低，除非能量富余否则先打别的";
                    m.ScoreDelta = -30;
                }
                else
                {
                    m.Status = $"当前格挡 {block} → 伤害约 {block}，可以当输出打";
                    m.ScoreDelta = 25;
                    m.Useful = true;
                }
                return m;
            }

            // -----------------------------------------------------------------
            // 2) 完美打击 / 爪击类：伤害 = 基础 + 额外 ×（牌组里符合条件的牌数）
            //    反编译 PerfectedStrike: 6 + 2×「名字含 Strike 的牌数」
            // -----------------------------------------------------------------
            if (up.Contains("PERFECTED_STRIKE") || up.Contains("PERFECTEDSTRIKE"))
            {
                int strikeLike = CountDeckWhere(me, x => IsStrikeLike(x));
                var m = New(c, "伤害 = 基础值 + 额外值 × 牌组里「打击类(Strike)」牌的数量");
                m.ScoreDelta = strikeLike >= 4 ? 40 : (strikeLike >= 2 ? 10 : -10);
                m.Status = $"牌组中共 {strikeLike} 张打击类牌 → 本牌当前伤害较高，{(strikeLike >= 3 ? "值得打" : "收益一般")}";
                m.Useful = strikeLike >= 1;
                return m;
            }

            if (up.Contains("CLAW") || up.Contains("爪击"))
            {
                var m = New(c, "伤害随本场战斗每打出一张「爪击」永久 +2（成长型）");
                m.Status = "成长牌：越早打出越值钱，前期可优先";
                m.ScoreDelta = 15;
                return m;
            }

            // -----------------------------------------------------------------
            // 3) 重刃类：伤害 = 基础 + 额外 × 当前力量
            // -----------------------------------------------------------------
            if (up.Contains("HEAVY_BLADE") || up.Contains("HEAVYBLADE"))
            {
                int str = PowerAmount(me, "Strength");
                var m = New(c, "伤害 = 基础值 + 额外值 × 你的**力量**（力量为 0 时只有基础伤害）");
                m.Status = $"当前力量 {str} → {(str <= 0 ? "无加成，就是一张普通攻击" : $"有 {str} 倍加成，很值")}";
                m.ScoreDelta = str >= 3 ? 60 : (str <= 0 ? -15 : 0);
                m.Useful = true;
                return m;
            }

            // -----------------------------------------------------------------
            // 4) 催化/剧毒类：需要目标已有中毒才有价值
            // -----------------------------------------------------------------
            if (up.Contains("CATALYST") || up.Contains("催化"))
            {
                int poison = MaxEnemyPower(me, "Poison");
                var m = New(c, "把目标当前的**中毒层数翻倍**（目标没有中毒 = 毫无作用）");
                m.Useful = poison > 0;
                m.ScoreDelta = poison >= 5 ? 80 : (poison > 0 ? 20 : -999);
                m.Status = poison <= 0
                    ? "当前敌人均无中毒 → 本牌完全无效，**绝不能打**"
                    : $"敌人最高中毒 {poison} → 翻倍后约 {poison * 2}，值得打";
                return m;
            }

            // -----------------------------------------------------------------
            // 5) 需要消耗/弃牌/充能等前置资源，手上没有则打不出价值
            // -----------------------------------------------------------------
            if (up.Contains("SHIV") && (up.Contains("ACCURACY") == false) && c.Type == CardType.Attack
                && up.Contains("STORM_OF_STEEL") == false)
            {
                // 小刀类攻击牌需要手上/牌组有小刀来源；这里只做低置信提示，不加硬门槛
            }

            // -----------------------------------------------------------------
            // 6) 自伤代价牌：血量极低时不该打
            // -----------------------------------------------------------------
            if (up.Contains("BLOODLETTING") || up.Contains("OFFERING") || up.Contains("HEMOKINESIS") ||
                up.Contains("SEEING_RED") || up.Contains("RUPTURE"))
            {
                var m = New(c, "打出会**损失自身生命**换取能量/抽牌/力量");
                bool danger = hp <= Math.Max(6, maxHp * 0.25);
                m.Useful = !danger;
                m.ScoreDelta = danger ? -120 : 10;
                m.Status = danger
                    ? $"当前生命 {hp}/{maxHp} 偏低 → 自伤可能致死，**不建议打**"
                    : $"当前生命 {hp}/{maxHp} 尚可，可用作资源牌";
                return m;
            }

            // -----------------------------------------------------------------
            // 7) X 费牌：能量越多越值；没能量时打出去几乎无收益
            // -----------------------------------------------------------------
            try
            {
                if (c.HasStarCostX)
                {
                    var m = New(c, "X 费牌：效果强度 = **打出时剩余的全部能量**（消耗多少算多少）");
                    m.Useful = energy >= 2;
                    m.ScoreDelta = energy >= 4 ? 50 : (energy >= 2 ? 15 : -40);
                    m.Status = $"当前能量 {energy} → {(energy >= 2 ? $"可打出强度 {energy} 的效果" : "能量太少，效果微弱，建议留到下回合")}";
                    return m;
                }
            }
            catch { }

            // -----------------------------------------------------------------
            // 8) 群伤牌：敌人越多越值
            // -----------------------------------------------------------------
            string[] aoe = { "CLEAVE", "WHIRLWIND", "IMMOLATE", "CARNAGE", "DIE_DIE_DIE", "THUNDERCLAP", "BREATH" };
            if (aoe.Any(k => up.Contains(k)))
            {
                var m = New(c, "**群体伤害**：对全部敌人各结算一次");
                m.ScoreDelta = enemyCount >= 3 ? 45 : (enemyCount == 2 ? 20 : -10);
                m.Status = $"当前存活敌人 {enemyCount} 个 → {(enemyCount >= 2 ? "群伤收益放大" : "只有一个敌人，等同于单体攻击")}";
                return m;
            }

            // -----------------------------------------------------------------
            // 9) 纯过牌但手牌已满/牌堆已空 → 收益低
            // -----------------------------------------------------------------
            if (DrawAmount(c) > 0 && hand >= 9)
            {
                var m = New(c, "抽牌类：手牌已接近上限，抽上来可能被浪费");
                m.ScoreDelta = -15;
                m.Status = $"当前手牌 {hand} 张，已接近上限 → 过牌收益下降";
                return m;
            }

            // -----------------------------------------------------------------
            // 10) 格挡牌但本回合毫无威胁 → 过度防御（避免“该输出时不输出”）
            // -----------------------------------------------------------------
            int blk = BlockAmount(c);
            if (blk > 0)
            {
                var enemies = AliveEnemies(me);
                int incoming = enemies.Sum(EnemyIntentDamage);
                int curBlock = block;
                if (incoming <= curBlock)
                {
                    var m = New(c, "格挡类：但本回合**已无需再防**");
                    m.ScoreDelta = -20;
                    m.Status = $"本回合承伤 {incoming} ≤ 已有格挡 {curBlock} → 再叠格挡是浪费，优先输出";
                    m.Useful = false;
                    return m;
                }
                if (incoming > curBlock)
                {
                    var m = New(c, "格挡类：本回合存在**未覆盖的威胁**");
                    int gap = incoming - curBlock;
                    m.ScoreDelta = gap >= hp ? 200 : (gap >= hp * 0.4 ? 60 : 15);
                    m.Status = $"本回合承伤 {incoming}，已有格挡 {curBlock} → 还缺 {gap} 点" +
                               (gap >= hp ? "，**不挡会死，必须优先格挡**" : "，值得格挡");
                    m.Useful = true;
                    return m;
                }
            }

            return null;
        }
        catch
        {
            return null;   // 机制读取失败一律放行，绝不因新逻辑把牌卡死
        }
    }

    /// <summary>
    /// 给手牌做“条件机制体检”，生成一段给 LLM 的**逐牌机制提示**。
    /// 只列出**有登记的机制**的牌，且明确指出“现在能不能打/值不值”。
    /// </summary>
    public static string DescribeHandMechanics(IReadOnlyList<CardModel> hand, Player me)
    {
        try
        {
            if (hand == null || hand.Count == 0 || me == null) return "";
            var lines = new List<string>();
            for (int i = 0; i < hand.Count; i++)
            {
                CardModel c = hand[i];
                Mech m = Evaluate(c, me);
                if (m == null) continue;
                string warn = m.Useful ? "可打" : "⚠此时无价值";
                lines.Add($"  {i + 1}. {m.CardName}【{warn}】机制: {m.Rule}；当前: {m.Status}");
            }
            return lines.Count == 0 ? "" : string.Join("\n", lines);
        }
        catch { return ""; }
    }

    /// <summary>
    /// 交给大模型的“条件机制硬规则”段落（放在战斗提示词里）。
    /// </summary>
    public const string PromptRules =
        "【条件机制硬规则——违反即视为出牌错误】\n" +
        "· **全身撞击 / 盾击类**（伤害 = 当前格挡值）：若你**当前格挡为 0**，打出它造成的伤害是 **0**，" +
        "  属于纯粹浪费一次出牌与能量，**绝对不许打**。反之格挡很高时它是强力输出。\n" +
        "· **催化类**（中毒翻倍）：目标身上**没有中毒**时完全无效，不许打。\n" +
        "· **完美打击**（伤害随牌组里的打击类牌数量成长）：牌组里打击类牌很少时收益低。\n" +
        "· **重刃类**（伤害随力量成长）：力量为 0 时等同于普通攻击，不值得特意优先。\n" +
        "· **自伤牌**（以损失生命换资源）：生命低于 25% 时不许打。\n" +
        "· **X 费牌**：效果强度等于打出的能量，剩余能量 ≤1 时几乎无收益，应留到下回合。\n" +
        "· **格挡牌**：只在「本回合承伤 > 已有格挡」时才值得打；已经防够了就不要继续叠格挡，改为输出。\n";

    // =====================================================================
    //  内部工具
    // =====================================================================
    private static Mech New(CardModel c, string rule) => new Mech
    {
        CardId = SafeId(c),
        CardName = Brain.SafeTitle(c),
        Rule = rule,
        Useful = true,
        ScoreDelta = 0,
    };

    private static string SafeId(CardModel c)
    {
        try { return c.Id.Entry ?? ""; } catch { return ""; }
    }

    private static int BlockOf(Player me) { try { return me.Creature?.Block ?? 0; } catch { return 0; } }
    private static int HpOf(Player me) { try { return me.Creature?.CurrentHp ?? 1; } catch { return 1; } }
    private static int MaxHpOf(Player me) { try { return me.Creature?.MaxHp ?? 1; } catch { return 1; } }
    private static int EnergyOf(Player me) { try { return me.PlayerCombatState?.Energy ?? 0; } catch { return 0; } }
    private static int DeckCount(Player me) { try { return me.Deck?.Cards?.Count ?? 0; } catch { return 0; } }

    private static int HandCount(Player me)
    {
        try { return PileType.Hand.GetPile(me)?.Cards?.Count ?? 0; } catch { return 0; }
    }

    private static List<Creature> AliveEnemies(Player me)
    {
        try
        {
            var cs = me.Creature?.CombatState;
            if (cs == null) return new List<Creature>();
            return cs.Enemies.Where(e => e.IsAlive).ToList();
        }
        catch { return new List<Creature>(); }
    }

    private static int AliveEnemyCount(Player me) => AliveEnemies(me).Count;

    private static int TotalEnemyHp(Player me)
    {
        try { return AliveEnemies(me).Sum(e => Math.Max(0, e.CurrentHp)); } catch { return 0; }
    }

    private static int PowerAmount(Player me, string powerName)
    {
        try
        {
            var p = me.Creature?.Powers?.FirstOrDefault(x =>
                x.GetType().Name.Equals(powerName + "Power", StringComparison.OrdinalIgnoreCase));
            return p?.Amount ?? 0;
        }
        catch { return 0; }
    }

    private static int MaxEnemyPower(Player me, string powerName)
    {
        try
        {
            int best = 0;
            foreach (var e in AliveEnemies(me))
            {
                var p = e.Powers?.FirstOrDefault(x =>
                    x.GetType().Name.Equals(powerName + "Power", StringComparison.OrdinalIgnoreCase));
                if (p != null && p.Amount > best) best = p.Amount;
            }
            return best;
        }
        catch { return 0; }
    }

    private static int CountDeckWhere(Player me, Func<CardModel, bool> pred)
    {
        try
        {
            var deck = me.Deck?.Cards;
            if (deck == null) return 0;
            int n = 0;
            foreach (var c in deck) { try { if (c != null && pred(c)) n++; } catch { } }
            return n;
        }
        catch { return 0; }
    }

    private static bool IsStrikeLike(CardModel c)
    {
        try
        {
            if (c.Tags != null && c.Tags.Contains(CardTag.Strike)) return true;
            var p = CardKnowledge.Profile(c);
            return p != null && p.Has(CardKnowledge.Tag.StrikeLike);
        }
        catch { return false; }
    }

    /// <summary>读“抽牌数量”（DynamicVars.Cards）。</summary>
    private static int DrawAmount(CardModel c)
    {
        try
        {
            var vars = c.DynamicVars;
            if (vars != null && vars.ContainsKey("Cards")) return vars["Cards"]?.IntValue ?? 0;
        }
        catch { }
        return 0;
    }

    /// <summary>读“格挡数值”。优先 DynamicVars，其次卡面画像。</summary>
    private static int BlockAmount(CardModel c)
    {
        try
        {
            var vars = c.DynamicVars;
            if (vars != null)
            {
                if (vars.ContainsKey("Block")) return vars["Block"]?.IntValue ?? 0;
                if (vars.ContainsKey("CalculatedBlock")) return vars["CalculatedBlock"]?.IntValue ?? 0;
            }
        }
        catch { }
        try
        {
            var p = CardKnowledge.Profile(c);
            return p?.Block ?? 0;
        }
        catch { return 0; }
    }

    /// <summary>单只敌人的本回合意图伤害（走官方 API，与 GameState.IntentDamage 一致）。</summary>
    private static int EnemyIntentDamage(Creature e) => GameState.IntentDamage(e);
}
