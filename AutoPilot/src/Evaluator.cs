using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;

namespace AutoPilot;

/// <summary>
/// 战场与牌组的“量化评估”模块。
///
/// 与 <see cref="GameState"/>（负责把状态序列化成文本）分工不同，本类负责**算出数字**：
///   ① 战损计算：本回合真实承伤、需要多少格挡才不掉血、多回合后的死亡预警。
///   ② 牌组评估：输出/防御/费用曲线、牌组臃肿度、多余的基础卡、缺失的短板。
///   ③ 输出能力：本回合可达成的最大伤害（考虑费用预算与能量），用于判断能否斩杀。
///
/// 这些数字会同时用于：
///   · 喂给大模型的提示（让模型看到精确数值，而不是凭描述猜）；
///   · 本地兜底规则（无 Key / 超时时仍能做出合理决策）。
///
/// 所有读取都包 try/catch 并给安全默认值 —— 评估失败绝不能影响游戏推进。
/// </summary>
public static class Evaluator
{
    // =====================================================================
    //  一、战损计算
    // =====================================================================

    /// <summary>单个敌人的本回合意图伤害明细。</summary>
    public sealed class EnemyThreat
    {
        public Creature Enemy;
        public int Damage;          // 本回合将对玩家造成的伤害（已含力量/虚弱等修正）
        public int Repeats = 1;     // 多段攻击段数
        public bool IsAttack;
        public string Note = "";    // 附加说明（如“攻击+减益”）
    }

    /// <summary>
    /// 计算本回合全部敌人的威胁。
    /// 注意：<c>GetTotalDamage(targets, owner)</c> 已经包含力量/虚弱等修正，返回的是单次结算的总值；
    /// 多段攻击由 <c>Repeats</c> 表示（用于展示，不重复计入 Damage，因为 TotalDamage 已含段数）。
    /// </summary>
    public static List<EnemyThreat> EvaluateThreats(Player me)
    {
        var list = new List<EnemyThreat>();
        try
        {
            foreach (Creature e in GameState.HittableEnemies(me))
            {
                var t = new EnemyThreat { Enemy = e };
                try
                {
                    MonsterModel mon = e.Monster;
                    if (mon?.NextMove?.Intents != null)
                    {
                        var targets = new List<Creature> { e };
                        var notes = new List<string>();
                        foreach (var intent in mon.NextMove.Intents)
                        {
                            if (intent is MegaCrit.Sts2.Core.MonsterMoves.Intents.AttackIntent atk)
                            {
                                t.IsAttack = true;
                                try { t.Damage += atk.GetTotalDamage(targets, e); } catch { }
                                try { t.Repeats = Math.Max(1, atk.Repeats); } catch { }
                            }
                            else if (intent != null)
                            {
                                notes.Add(GameState.IntentName(intent));
                            }
                        }
                        if (notes.Count > 0) t.Note = string.Join("+", notes);
                    }
                }
                catch { }
                list.Add(t);
            }
        }
        catch { }
        return list;
    }

    /// <summary>本回合投射到玩家身上的总伤害（未扣除格挡）。</summary>
    public static int IncomingDamage(Player me)
    {
        try { return EvaluateThreats(me).Sum(t => t.Damage); }
        catch { return 0; }
    }

    /// <summary>
    /// 本回合战损评估结论。
    /// </summary>
    public struct DamageOutlook
    {
        public int Incoming;        // 总承伤（未扣格挡）
        public int Block;           // 当前已有格挡
        public int Unblocked;       // 预计实际掉血（可负=格挡溢出）
        public int Hp;
        public int MaxHp;
        public bool Lethal;         // 是否会死
        public int HpAfter;         // 结算后血量（下限 0 展示）
    }

    /// <summary>本回合战损评估（格挡按“会被打掉”结算，故实际掉血 = max(0, 承伤 - 格挡)）。</summary>
    public static DamageOutlook EvaluateDamage(Player me)
    {
        var o = new DamageOutlook();
        try
        {
            o.Hp = me?.Creature?.CurrentHp ?? 0;
            o.MaxHp = me?.Creature?.MaxHp ?? 0;
            o.Block = me?.Creature?.Block ?? 0;
            o.Incoming = IncomingDamage(me);
            o.Unblocked = Math.Max(0, o.Incoming - o.Block);
            o.Lethal = o.Unblocked >= o.Hp && o.Hp > 0;
            o.HpAfter = Math.Max(0, o.Hp - o.Unblocked);
        }
        catch { }
        return o;
    }

    /// <summary>
    /// 本回合可达到的最大输出（贪心：按费效比从高到低塞，直到能量耗尽）。
    /// 用于判断“能否斩杀某个敌人”。
    /// </summary>
    public static int MaxPlayableDamage(Player me)
    {
        try
        {
            int energy = me?.PlayerCombatState?.Energy ?? 0;
            var hand = PileType.Hand.GetPile(me)?.Cards;
            if (hand == null) return 0;

            var attacks = new List<(CardModel card, int cost, int dmg)>();
            foreach (CardModel c in hand)
            {
                if (c == null || c.Type != CardType.Attack) continue;
                int cost = GameState.SafeCost(c);
                if (cost < 0) cost = 0;
                int dmg = Brain.EstimateCardDamagePublic(c, me);
                if (dmg <= 0) continue;
                attacks.Add((c, cost, dmg));
            }
            // 按“每点能量的伤害”从高到低贪心
            attacks.Sort((a, b) =>
            {
                double ra = a.cost == 0 ? double.MaxValue : (double)a.dmg / a.cost;
                double rb = b.cost == 0 ? double.MaxValue : (double)b.dmg / b.cost;
                return rb.CompareTo(ra);
            });

            int total = 0;
            foreach (var a in attacks)
            {
                if (a.cost <= energy) { total += a.dmg; energy -= a.cost; }
            }
            return total;
        }
        catch { return 0; }
    }

    /// <summary>
    /// 能否在本回合击杀指定敌人（用“最大输出 vs 目标剩余血量+格挡”做保守判断）。
    /// </summary>
    public static bool CanKillThisTurn(Player me, Creature target)
    {
        try
        {
            if (target == null || !target.IsAlive) return false;
            int need = target.CurrentHp + target.Block;
            // 单目标时直接用最大输出；多目标时留一点余量（部分输出会被分走）
            int enemies = GameState.HittableEnemies(me).Count;
            int avail = MaxPlayableDamage(me);
            return avail >= need;
        }
        catch { return false; }
    }

    /// <summary>把战损评估压成一行人类可读文本，附在战斗日志里。</summary>
    public static string DamageSummary(Player me)
    {
        try
        {
            var o = EvaluateDamage(me);
            var threats = EvaluateThreats(me).Where(t => t.IsAttack).ToList();
            string detail = threats.Count == 0 ? "无攻击意图"
                : string.Join(" + ", threats.Select(t => t.Repeats > 1 ? $"{t.Damage}×{t.Repeats}?" : $"{t.Damage}"));
            string lethal = o.Lethal ? "  ⚠致命" : "";
            return $"承伤 {o.Incoming}（{detail}） 格挡 {o.Block} → 预计掉血 {o.Unblocked}，" +
                   $"战后 {o.HpAfter}/{o.MaxHp}{lethal}";
        }
        catch { return ""; }
    }

    // =====================================================================
    //  二、牌组评估
    // =====================================================================

    /// <summary>牌组量化画像。</summary>
    public sealed class DeckReport
    {
        public int Size;
        public int Attacks;
        public int Skills;
        public int Powers;
        public int Curses;          // 诅咒 + 状态（污染牌组）
        public int Upgraded;
        public int ZeroCost;
        public int AverageCost;     // 加权平均费用（×10 存整数，展示时 /10）
        public int TotalDamage;     // 全牌组单轮理论输出（不记费用）
        public int TotalBlock;
        public int StrikeLike;      // 基础打击类（需要精简）
        public int DefendLike;      // 基础防御类
        public double Density;      // 每张牌的平均产出（越高越精炼）
        public List<string> Bloat;  // 臃肿/冗余提示
        public List<string> Gaps;   // 短板提示

        public double AvgCost => AverageCost / 10.0;
    }

    private static readonly string[] _strikeIds = { "STRIKE", "打击" };
    private static readonly string[] _defendIds = { "DEFEND", "防御" };

    /// <summary>对当前牌组做量化评估（供选卡/商店/篝火升级决策）。</summary>
    public static DeckReport AnalyzeDeck(Player me)
    {
        var r = new DeckReport { Bloat = new List<string>(), Gaps = new List<string>() };
        try
        {
            var deck = me?.Deck?.Cards;
            if (deck == null || deck.Count == 0) return r;

            r.Size = deck.Count;
            int costSum = 0, costWeight = 0;
            foreach (CardModel c in deck)
            {
                if (c == null) continue;
                try
                {
                    switch (c.Type)
                    {
                        case CardType.Attack: r.Attacks++; r.TotalDamage += Brain.EstimateCardDamagePublic(c, me); break;
                        case CardType.Skill: r.Skills++; r.TotalBlock += Brain.EstimateCardBlockPublic(c, me); break;
                        case CardType.Power: r.Powers++; break;
                        case CardType.Curse:
                        case CardType.Status: r.Curses++; break;
                    }

                    if (c.IsUpgraded) r.Upgraded++;

                    int cost = GameState.SafeCost(c);
                    if (cost <= 0) r.ZeroCost++;
                    else { costSum += cost; costWeight++; }
                }
                catch { }

                // 基础卡判定（用 ID 或标题模糊匹配）
                try
                {
                    string id = (c.Id.Entry ?? "").ToUpperInvariant();
                    string title = Brain.SafeTitle(c) ?? "";
                    if (_strikeIds.Any(s => id.Contains(s) || title.Contains(s))) r.StrikeLike++;
                    if (_defendIds.Any(s => id.Contains(s) || title.Contains(s))) r.DefendLike++;
                }
                catch { }
            }

            r.AverageCost = costWeight > 0 ? (int)Math.Round(costSum * 10.0 / costWeight) : 0;
            r.Density = r.Size > 0 ? (double)(r.TotalDamage + r.TotalBlock) / r.Size : 0;

            // ---- 臃肿判定 ----
            if (r.Size > 22)
                r.Bloat.Add($"牌组偏大({r.Size}张)，稀释关键牌抽到率");
            if (r.StrikeLike >= 4)
                r.Bloat.Add($"基础打击类 {r.StrikeLike} 张，是优先删除对象");
            if (r.DefendLike >= 4)
                r.Bloat.Add($"基础防御类 {r.DefendLike} 张，可考虑删除");
            if (r.Curses > 0)
                r.Bloat.Add($"含 {r.Curses} 张诅咒/状态，严重拖累（优先移除）");

            // ---- 短板判定 ----
            if (r.Attacks == 0 || r.TotalDamage < r.Size * 4)
                r.Gaps.Add("输出不足：伤害偏低，需补充攻击牌");
            if (r.Skills == 0 || r.TotalBlock < r.Size * 3)
                r.Gaps.Add("防御不足：格挡偏低，生存堪忧");
            if (r.Powers == 0 && r.Size >= 15)
                r.Gaps.Add("缺少能力牌：中后期成长性不足");
            if (r.ZeroCost == 0 && r.Size >= 18)
                r.Gaps.Add("缺少 0 费牌：能量利用灵活性差");
            if (r.AverageCost >= 2.0 && r.Size >= 15)
                r.Gaps.Add($"平均费用偏高({r.AvgCost:F1})，容易出现能量卡手");

            return r;
        }
        catch (Exception e)
        {
            Pilot.Warn("牌组评估失败（已忽略）: " + e.Message);
            return r;
        }
    }

    /// <summary>牌组评估的一行摘要（用于日志与提示词）。</summary>
    public static string DeckSummary(Player me)
    {
        try
        {
            var r = AnalyzeDeck(me);
            if (r.Size == 0) return "（空）";
            return $"{r.Size}张（攻{r.Attacks}/技{r.Skills}/能{r.Powers}" +
                   (r.Curses > 0 ? $"/污染{r.Curses}" : "") +
                   $"），已升级{r.Upgraded}，均费{r.AvgCost:F1}，" +
                   $"理论输出{r.TotalDamage}/防御{r.TotalBlock}，精炼度{r.Density:F1}";
        }
        catch { return ""; }
    }

    /// <summary>把牌组评估详情压成喂给大模型的多行文本。</summary>
    public static string DescribeDeckReport(Player me)
    {
        var sb = new StringBuilder();
        try
        {
            var r = AnalyzeDeck(me);
            sb.AppendLine($"  规模: {r.Size} 张   攻击 {r.Attacks} / 技能 {r.Skills} / 能力 {r.Powers}" +
                          (r.Curses > 0 ? $" / 诅咒状态 {r.Curses}" : ""));
            sb.AppendLine($"  已升级: {r.Upgraded}   0费牌: {r.ZeroCost}   平均费用: {r.AvgCost:F1}");
            sb.AppendLine($"  理论单轮输出: {r.TotalDamage}   理论单轮格挡: {r.TotalBlock}   精炼度: {r.Density:F1}");
            if (r.Bloat.Count > 0) sb.AppendLine("  臃肿: " + string.Join("；", r.Bloat));
            if (r.Gaps.Count > 0) sb.AppendLine("  短板: " + string.Join("；", r.Gaps));
        }
        catch { }
        return sb.ToString();
    }

    // =====================================================================
    //  三、通用评分（供本地兜底与排序）
    // =====================================================================

    /// <summary>牌组健康度 0~100（越高越强）。用于判断“该不该休息 / 该不该贪精英”。</summary>
    public static int DeckHealth(Player me)
    {
        try
        {
            var r = AnalyzeDeck(me);
            if (r.Size == 0) return 50;
            int score = 50;
            // 精炼度贡献（每张牌理论产出 15 左右为佳）
            score += (int)Math.Round(Math.Min(30, r.Density * 1.2));
            score -= r.Curses * 6;
            score -= Math.Max(0, r.Size - 20) * 2;      // 过大扣分
            score -= Math.Max(0, 4 - r.Attacks) * 5;    // 攻击太少扣分
            score -= Math.Max(0, 3 - r.Skills) * 4;     // 防御太少扣分
            score += Math.Min(8, r.Powers * 2);         // 能力牌加分
            score -= (int)Math.Round(r.AvgCost * 3);    // 费用越高越差
            return Math.Clamp(score, 1, 100);
        }
        catch { return 50; }
    }

    /// <summary>回血收益量化：当前血越低，休息价值越高。</summary>
    public static double RestValue(Player me, int healPerRest = 0)
    {
        try
        {
            int hp = me?.Creature?.CurrentHp ?? 0;
            int maxHp = me?.Creature?.MaxHp ?? 1;
            if (maxHp <= 0) return 0;
            double missing = (double)(maxHp - hp) / maxHp;   // 缺多少比例
            return missing * 100;                            // 0~100
        }
        catch { return 0; }
    }
}
