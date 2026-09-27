using System;
using System.Collections.Generic;
using System.Linq;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;

namespace AutoPilot;

/// <summary>
/// 选卡打分引擎。把《尖塔军师》的选牌公式**离线复刻**为可计算的本地模型：
///
///   选牌分 = 战力 + 成长 + 一致性 + 生存 + 遗物放大 + 生存风险 + 流派协同 − 牌组稀释
///
/// 与军师的关键差异（也是我们的优势）：
///   · 军师的"社区胜率/选牌率"来自约 100 万局线上数据；我们**没有联网数据**，
///     因此改用"**本体真实数值 + 升级前后数值差 + 牌组内相对强弱**"做代理指标，
///     全程离线、且对任意 mod 卡也成立（军师的数据库覆盖不到 mod 卡）。
///   · 权重是**动态**的：血少 → 生存权重涨；牌组臃肿 → 稀释惩罚涨；
///     临近 Boss → 成长权重涨；幕数越深 → 判定更严格（照搬军师的设计）。
///
/// 输出：每张候选卡的分数、三档结论（拿/考虑/跳过）、以及人类可读的理由列表。
/// </summary>
public static class Selector
{
    // =====================================================================
    //  结果模型
    // =====================================================================
    public enum Verdict { Take, Consider, Skip }

    public sealed class CandidateScore
    {
        public CardModel Card;
        public CardKnowledge.CardProfile Profile;
        public double Score;                 // 0~100 综合分
        public Verdict Verdict = Verdict.Consider;
        public List<string> Reasons = new(); // 人类可读理由
        public List<string> SynergyNotes = new();
        public bool IsBest;                  // 本屏最优（★）
        public string ArchetypeName = "";

        public string VerdictCn => Verdict switch
        {
            Verdict.Take => "拿",
            Verdict.Consider => "考虑",
            Verdict.Skip => "跳过",
            _ => "?",
        };
    }

    // =====================================================================
    //  动态权重
    // =====================================================================
    private sealed class Weights
    {
        public double Combat = 1.0;      // 战力
        public double Growth = 1.0;      // 成长
        public double Consistency = 1.0; // 一致性（流派协同）
        public double Survival = 1.0;    // 生存
        public double RelicAmp = 1.0;    // 遗物放大
        public double Risk = 1.0;        // 事件风险
        public double Dilution = 1.0;    // 牌组稀释惩罚
        public double Threshold = 0.0;   // 分数线偏移（正=更严格）
    }

    private static Weights ComputeWeights(Player me, int deckSize, int floor, int act)
    {
        var w = new Weights();
        try
        {
            int hp = me?.Creature?.CurrentHp ?? 1;
            int maxHp = me?.Creature?.MaxHp ?? 1;
            double hpRatio = maxHp > 0 ? (double)hp / maxHp : 1;

            // 血少 → 生存权重涨
            if (hpRatio < 0.5) { w.Survival += 0.8; w.Risk += 0.5; }
            else if (hpRatio < 0.7) { w.Survival += 0.3; }

            // 牌组臃肿 → 稀释惩罚涨
            if (deckSize > 25) w.Dilution += 0.7;
            else if (deckSize > 20) w.Dilution += 0.3;

            // 临近 Boss（层数 15/16 或幕内 >12 层）→ 成长权重涨
            if (floor >= 14 || (floor % 17) >= 13) { w.Growth += 0.5; w.Combat += 0.2; }

            // 幕越深 → 判定更严格
            w.Threshold += Math.Max(0, act - 1) * 2.0;

            // 血少 → 判定更严格（不轻易加牌污染牌组）
            if (hpRatio < 0.5) w.Threshold += 3.0;

            // 人工偏好会额外影响（见 ApplyPreferences）
        }
        catch { }
        return w;
    }

    // =====================================================================
    //  主入口：给一组候选卡打分
    // =====================================================================
    /// <summary>
    /// 为候选卡打分。返回与输入等长的评分数组（index 对应）。
    /// </summary>
    public static List<CandidateScore> ScoreOptions(IReadOnlyList<CardModel> options, Player me, bool isCombatReward = false)
    {
        var results = new List<CandidateScore>();
        if (options == null || options.Count == 0) return results;

        var deck = SafeDeck(me);
        var deckProfiles = deck.Select(CardKnowledge.Profile).Where(p => p != null).ToList();
        int deckSize = deck.Count;
        int floor = 1, act = 1;
        try
        {
            var rs = Pilot.RunStateNow;
            floor = rs?.TotalFloor ?? 1;
            act = (rs?.CurrentActIndex ?? 0) + 1;
        }
        catch { }

        var w = ComputeWeights(me, deckSize, floor, act);
        var archs = CardKnowledge.DetectArchetypes(deck);
        var relics = SafeRelics(me);
        var deckReport = Evaluator.AnalyzeDeck(me);

        foreach (CardModel c in options)
        {
            var cs = new CandidateScore { Card = c, Profile = CardKnowledge.Profile(c) };
            try { cs.Score = ScoreOne(cs, w, deck, deckProfiles, archs, relics, deckReport, me, floor, act); }
            catch (Exception e) { Pilot.Warn("选卡评分异常: " + e.Message); cs.Score = 40; cs.Reasons.Add("评分异常"); }
            results.Add(cs);
        }

        // 归一化到 0~100 并给结论
        if (results.Count > 0)
        {
            double max = results.Max(r => r.Score);
            double min = results.Min(r => r.Score);
            foreach (var r in results)
            {
                double norm = (max > min) ? (r.Score - min) / (max - min) : 1.0;
                r.Score = Math.Round(30 + norm * 65, 0);   // 映射到 30~95，避免出现 0 分观感
                double skipValue = SkipValue(w, deckSize, act, deckReport);
                double cut = 55 + w.Threshold - skipValue;
                r.Verdict = r.Score >= cut + 12 ? Verdict.Take
                          : r.Score >= cut - 8 ? Verdict.Consider
                          : Verdict.Skip;
            }

            var best = results.OrderByDescending(r => r.Score).First();
            if (best.Verdict != Verdict.Skip) best.IsBest = true;

            // 三档被归一化后可能都变成"拿" → 强制拉开：只保留最高分为"拿"
            if (results.Count(r => r.Verdict == Verdict.Take) == results.Count)
            {
                foreach (var r in results) r.Verdict = Verdict.Consider;
                best.Verdict = Verdict.Take;
            }
        }
        return results;
    }

    /// <summary>"跳过的价值"：不需要这张卡时，跳过等于保持牌组精简，本身有分。</summary>
    private static double SkipValue(Weights w, int deckSize, int act, Evaluator.DeckReport r)
    {
        double v = 0;
        if (deckSize > 22) v += 6;      // 牌组已臃肿 → 跳过更值
        if (deckSize > 28) v += 6;
        v += Math.Max(0, act - 1) * 2;  // 后期不轻易加牌
        if (r.Curses > 0) v -= 3;       // 有诅咒时更需要好牌
        return v;
    }

    // =====================================================================
    //  单卡打分
    // =====================================================================
    private static double ScoreOne(CandidateScore cs, Weights w,
        List<CardModel> deck, List<CardKnowledge.CardProfile> deckProfiles,
        List<(CardKnowledge.Archetype Arch, double Score, int Have, int Need)> archs,
        List<string> relics, Evaluator.DeckReport rep, Player me, int floor, int act)
    {
        var p = cs.Profile;
        if (p == null) return 40;

        double combat = 0, growth = 0, consistency = 0, survival = 0, relicAmp = 0, risk = 0, dilution = 0;

        // ---------- 诅咒 / 状态：负分（除非是无害的特殊牌） ----------
        if (p.Has(CardKnowledge.Tag.Curse))
        {
            cs.Reasons.Add("诅咒牌，污染牌组");
            return 5;
        }
        if (p.Has(CardKnowledge.Tag.Status))
        {
            cs.Reasons.Add("状态牌（除非能打出收益）");
            return 20;
        }

        int deckAttacks = rep.Attacks;
        int deckSkills = rep.Skills;

        // ---------- ① 战力（按数值密度：每点能量的产出） ----------
        int costE = Math.Max(1, p.Cost);
        if (p.TotalDamage > 0) combat += p.TotalDamage * 1.6 / costE;
        if (p.Block > 0) combat += p.Block * 1.1 / costE;
        if (p.Poison > 0) combat += p.Poison * 2.2;           // 毒是"穿透格挡"的持续伤害
        if (p.Tags.Contains(CardKnowledge.Tag.AoE)) combat += 14;   // 群伤在多怪战极强
        if (p.Tags.Contains(CardKnowledge.Tag.MultiHit)) combat += 6; // 多吃力量/易伤加成
        if (p.Has(CardKnowledge.Tag.HighCost) && p.TotalDamage == 0 && p.Block == 0 && p.Type == CardType.Attack)
            combat -= 8;   // 高费纯攻击但数值不明，谨慎
        if (p.TagText.Length > 0) cs.Reasons.Add("机制: " + p.TagText);

        // ---------- ② 成长（永久变强 / 后期价值） ----------
        if (p.Type == CardType.Power)
        {
            growth += 16;
            // 能力牌越早越值钱：前期（floor 小）加成更高
            growth += Math.Max(0, 12 - floor) * 0.8;
            cs.Reasons.Add("能力牌（永久生效，越早越强）");
        }
        if (p.Has(CardKnowledge.Tag.Scaling)) { growth += 12; cs.Reasons.Add("成长型（越打越强）"); }
        if (p.Has(CardKnowledge.Tag.Strength)) { growth += 10; cs.Reasons.Add("加力量（全局放大输出）"); }
        if (p.Has(CardKnowledge.Tag.Dexterity)) { growth += 8; cs.Reasons.Add("加敏捷（全局放大格挡）"); }
        if (p.Has(CardKnowledge.Tag.Heal)) { growth += 5; }

        // ---------- ③ 一致性 / 流派协同 ----------
        foreach (var (arch, archScore, have, need) in archs.Take(3))
        {
            double syn = CardKnowledge.ArchetypeSynergy(p, arch);
            if (syn <= 0) continue;
            double contrib = syn * 18 * archScore;
            consistency += contrib;
            cs.SynergyNotes.Add($"{arch.Name}协同（已有强度{archScore * 100:F0}%）");
        }
        // 补短板：缺输出/缺防御时相应牌加分
        if (p.Has(CardKnowledge.Tag.Damage) && deckAttacks < 7) { consistency += 8; cs.Reasons.Add("补输出短板"); }
        if (p.Has(CardKnowledge.Tag.Block) && deckSkills < 6) { consistency += 7; cs.Reasons.Add("补防御短板"); }
        if (p.Has(CardKnowledge.Tag.Draw) || p.Has(CardKnowledge.Tag.Energy))
        { consistency += 9; cs.Reasons.Add("过牌/充能（提升牌组循环）"); }

        // ---------- ④ 生存 ----------
        int hp = me?.Creature?.CurrentHp ?? 1;
        int maxHp = me?.Creature?.MaxHp ?? 1;
        double hpRatio = maxHp > 0 ? (double)hp / maxHp : 1;
        if (p.Has(CardKnowledge.Tag.Block) || p.Has(CardKnowledge.Tag.Weak))
        {
            survival += (1.6 - hpRatio) * 16;   // 越缺血越值
            if (hpRatio < 0.6) cs.Reasons.Add("血线吃紧，防御优先");
        }
        if (p.Has(CardKnowledge.Tag.Heal) || p.Has(CardKnowledge.Tag.Summon)) survival += 8;

        // ---------- ⑤ 遗物放大 ----------
        foreach (string rid in relics)
        {
            double amp = CardKnowledge.RelicSynergy(rid, deckProfiles);
            if (amp <= 0.20) continue;
            // 遗物放大：本卡与"遗物关心的机制"一致时，额外加权
            double bonus = amp * 10;
            if (p.Has(CardKnowledge.Tag.Damage) && rid.ToUpperInvariant().Contains("ATTACK")) bonus += 4;
            if (bonus > 0 && relicAmp + bonus > relicAmp)
            {
                relicAmp += bonus;
                cs.SynergyNotes.Add($"{rid} 协同（{(int)(amp * 100)}% 卡组可吃到）");
            }
        }
        if (decksHaveTag(deckProfiles, CardKnowledge.Tag.Strength) && p.Has(CardKnowledge.Tag.MultiHit))
        { relicAmp += 10; cs.Reasons.Add("已堆力量，多段收益翻倍"); }
        if (decksHaveTag(deckProfiles, CardKnowledge.Tag.Vulnerable) && p.TotalDamage > 0)
        { relicAmp += 6; cs.Reasons.Add("已有易伤来源，攻击吃加成"); }

        // ---------- ⑥ 生存风险（自伤 / 虚无 / 诅咒代价） ----------
        if (p.Has(CardKnowledge.Tag.HpCost))
        {
            risk += 8;
            if (hpRatio < 0.5) { risk += 14; cs.Reasons.Add("残血不宜用自伤牌"); }
        }
        if (p.Has(CardKnowledge.Tag.Ethereal) && deck.Count < 12) risk += 4;
        if (p.Has(CardKnowledge.Tag.Unplayable)) risk += 10;

        // ---------- ⑦ 牌组稀释 ----------
        if (p.TotalDamage == 0 && p.Block == 0 && !p.Has(CardKnowledge.Tag.Draw)
            && !p.Has(CardKnowledge.Tag.Energy) && p.Type != CardType.Power)
        {
            dilution += 8;   // 没有明确收益的牌会稀释牌组
            cs.Reasons.Add("收益不明确，稀释牌组");
        }
        if (p.Cost == 0 && p.TotalDamage == 0 && p.Block == 0 && p.Type == CardType.Skill
            && !p.Has(CardKnowledge.Tag.Draw))
        {
            dilution += 4;
        }

        // ---------- ⑧ 真实对局统计（来自 593 局真实存档训练的知识库） ----------
        double statsBonus = 0;
        try
        {
            if (cs.Card != null)
            {
                string cid = CardIdOf(cs.Card);
                string chId = me?.Character?.Id?.Entry ?? null;
                string note = null;
                statsBonus = RunStats.ScoreDelta(cid, chId, out note);
                if (note != null) cs.Reasons.Add(note);

                // 真实协同（PMI）：与牌组里已有的牌是否构成本作常见组合
                var deckIds = deckProfiles.Where(d => d != null).Select(d => d.Id).ToList();
                double syn = RunStats.SynergyWith(deckIds, cid);
                if (syn > 0.8)
                {
                    statsBonus += Math.Min(18, syn * 7);
                    string sn = RunStats.DescribeSynergy(deckIds, cid, 2);
                    if (sn != null) cs.SynergyNotes.Add(sn);
                }
            }
        }
        catch { statsBonus = 0; }

        // ---------- 加权汇总 ----------
        double score = w.Combat * combat
                     + w.Growth * growth
                     + w.Consistency * consistency
                     + w.Survival * survival
                     + w.RelicAmp * relicAmp
                     + w.Risk * risk
                     - w.Dilution * dilution
                     + statsBonus;

        // ---------- 人工偏好（硬覆盖） ----------
        string pref = PreferenceOf(p);
        if (pref == "like") { score += 50; cs.Reasons.Insert(0, "人工偏好：优先拿"); }
        else if (pref == "ban") { score -= 200; cs.Reasons.Insert(0, "人工禁选"); }

        return score;
    }

    private static bool decksHaveTag(List<CardKnowledge.CardProfile> deck, CardKnowledge.Tag t)
    {
        int n = deck.Count(p => p != null && p.Has(t));
        return n >= 2;
    }

    /// <summary>取卡的短 ID（去前缀），供 RunStats 查真实统计用。</summary>
    private static string CardIdOf(CardModel card)
    {
        try
        {
            string raw = card?.Pool?.Id?.Entry;
            if (!string.IsNullOrEmpty(raw))
            {
                int i = raw.LastIndexOf('.');
                return i >= 0 ? raw.Substring(i + 1) : raw;
            }
        }
        catch { }
        return null;
    }

    /// <summary>人工偏好：PreferredCards → like；BannedCards → ban。</summary>
    public static string PreferenceOf(CardKnowledge.CardProfile p)
    {
        try
        {
            string id = p.Id ?? "", name = p.Name ?? "";
            var cfg = PilotConfig.Current;
            if (cfg?.BannedCards != null && cfg.BannedCards.Any(b => !string.IsNullOrWhiteSpace(b) &&
                (id.Equals(b, StringComparison.OrdinalIgnoreCase) || name.Contains(b, StringComparison.OrdinalIgnoreCase))))
                return "ban";
            if (cfg?.PreferredCards != null && cfg.PreferredCards.Any(b => !string.IsNullOrWhiteSpace(b) &&
                (id.Equals(b, StringComparison.OrdinalIgnoreCase) || name.Contains(b, StringComparison.OrdinalIgnoreCase))))
                return "like";
        }
        catch { }
        return "";
    }

    // =====================================================================
    //  辅助
    // =====================================================================
    private static List<CardModel> SafeDeck(Player me)
    {
        try { return me?.Deck?.Cards?.Where(c => c != null).ToList() ?? new List<CardModel>(); }
        catch { return new List<CardModel>(); }
    }

    private static List<string> SafeRelics(Player me)
    {
        var l = new List<string>();
        try
        {
            var r = me?.Relics;
            if (r == null) return l;
            foreach (var x in r) { try { l.Add(x.Id.Entry ?? ""); } catch { } }
        }
        catch { }
        return l;
    }

    // =====================================================================
    //  给 LLM 的文本描述（替代原来简陋的 BestIndexOf）
    // =====================================================================
    /// <summary>把候选卡渲染成带"本地预评分"的文本，供 LLM 参考（LLM 仍是最终决策者）。</summary>
    public static string DescribeScoredOptions(IReadOnlyList<CardModel> options, Player me, bool isCombatReward)
    {
        var sb = new System.Text.StringBuilder();
        try
        {
            var scores = ScoreOptions(options, me, isCombatReward);
            var archs = CardKnowledge.DetectArchetypes(SafeDeck(me));
            if (archs.Count > 0)
            {
                sb.AppendLine("  【牌组流派倾向】" + string.Join("、",
                    archs.Take(3).Select(a => $"{a.Arch.Name}({a.Score * 100:F0}%{(a.Need > 0 ? $"，缺{a.Need}张核心" : "，已成型")})")));
            }
            for (int i = 0; i < scores.Count; i++)
            {
                var s = scores[i];
                var p = s.Profile;
                sb.AppendLine($"  {i + 1}. {p.Name} [费用{p.Cost}] {GameState.TypeCn(p.Type)}·{GameState.RarityCn(p.Rarity)}" +
                              $"  本地评分 {s.Score:F0} → {s.VerdictCn}{(s.IsBest ? " ★最优" : "")}");
                if (p.TagText.Length > 0) sb.AppendLine($"     机制: {p.TagText}  数值: {NumText(p)}");
                if (s.Reasons.Count > 0) sb.AppendLine($"     理由: {string.Join("；", s.Reasons.Take(4))}");
                if (s.SynergyNotes.Count > 0) sb.AppendLine($"     协同: {string.Join("；", s.SynergyNotes.Take(3))}");
                string desc = GameState.SafeDesc(p.Card, 70);
                if (!string.IsNullOrEmpty(desc)) sb.AppendLine($"     卡面: {desc}");
            }
        }
        catch (Exception e) { Pilot.Warn("选卡描述失败: " + e.Message); }
        return sb.ToString();
    }

    private static string NumText(CardKnowledge.CardProfile p)
    {
        var parts = new List<string>();
        if (p.TotalDamage > 0) parts.Add(p.Hits > 1 ? $"伤害{p.Damage}×{p.Hits}" : $"伤害{p.TotalDamage}");
        if (p.Block > 0) parts.Add($"格挡{p.Block}");
        if (p.Draw > 0) parts.Add($"抽{p.Draw}");
        if (p.EnergyGain > 0) parts.Add($"充能{p.EnergyGain}");
        if (p.Poison > 0) parts.Add($"毒{p.Poison}");
        if (p.StrengthGain > 0) parts.Add($"力量+{p.StrengthGain}");
        if (p.DexterityGain > 0) parts.Add($"敏捷+{p.DexterityGain}");
        if (p.VulnerableStacks > 0) parts.Add($"易伤{p.VulnerableStacks}");
        if (p.WeakStacks > 0) parts.Add($"虚弱{p.WeakStacks}");
        return parts.Count == 0 ? "—" : string.Join(" ", parts);
    }
}
