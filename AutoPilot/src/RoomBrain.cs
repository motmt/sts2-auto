using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.RestSite;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;

namespace AutoPilot;

/// <summary>
/// 非战斗房间的决策大脑：事件房选项、商店买卖、篝火休息/升级。
///
/// 设计原则与 <see cref="Brain"/> 一致：
///   · 人工偏好（配置里的倾向）优先级最高；
///   · 其次问大模型（带精确的量化信息：战损、牌组评估、金币）；
///   · 大模型失败/超时一律回退本地启发式规则 —— 保证 AI 永不停摆。
///
/// 与 <see cref="ScreenRouter"/> 的关系：本类只负责“决定选哪个”（返回索引/对象），
/// 由 ScreenRouter 负责找到对应按钮并点击，职责分离便于测试与复用。
/// </summary>
public static class RoomBrain
{
    // =====================================================================
    //  一、篝火：休息回血 vs 打铁升级
    // =====================================================================

    /// <summary>篝火决策结果。</summary>
    public struct RestChoice
    {
        public RestSiteOption Option;
        public string Why;
    }

    /// <summary>
    /// 在篝火选项里挑一个。综合血量、牌组健康度、可升级卡数量。
    /// 优先级：残血必休息 &gt; LLM 判断 &gt; 本地打分。
    /// </summary>
    public static RestChoice ChooseRestOption(IReadOnlyList<RestSiteOption> options, out string why)
    {
        why = "";
        var result = new RestChoice();
        if (options == null || options.Count == 0) return result;

        Player me = Pilot.Me;
        var enabled = options.Where(o => SafeEnabled(o)).ToList();
        if (enabled.Count == 0) enabled = options.ToList();

        // 唯一选项直接选
        if (enabled.Count == 1)
        {
            result.Option = enabled[0];
            why = "唯一可选";
            return result;
        }

        // ① 残血强制休息（保命优先，凌驾于一切之上）
        try
        {
            int hp = me?.Creature?.CurrentHp ?? 0;
            int maxHp = me?.Creature?.MaxHp ?? 1;
            double ratio = maxHp > 0 ? (double)hp / maxHp : 1;
            if (ratio * 100 < PilotConfig.Current.LowHpRestPercent)
            {
                var heal = enabled.FirstOrDefault(o => IsHeal(o));
                if (heal != null)
                {
                    result.Option = heal;
                    why = $"残血({hp}/{maxHp}={ratio * 100:F0}%)，优先休息保命";
                    return result;
                }
            }
        }
        catch { }

        // ② 大模型决策
        if (Brain.UseLlmPublic)
        {
            var llm = LlmChooseRest(enabled, out string llmWhy);
            if (llm != null) { result.Option = llm; why = llmWhy; return result; }
            if (!Brain.AllowFallbackPublic) { result.Option = enabled[0]; why = "LLM 无响应，取首个"; return result; }
        }

        // ③ 本地打分
        result.Option = ScoreRestLocal(enabled, me, out why);
        return result;
    }

    private static RestSiteOption ScoreRestLocal(IReadOnlyList<RestSiteOption> options, Player me, out string why)
    {
        int hp = me?.Creature?.CurrentHp ?? 0;
        int maxHp = me?.Creature?.MaxHp ?? 1;
        double missing = maxHp > 0 ? (double)(maxHp - hp) / maxHp : 0;
        int health = Evaluator.DeckHealth(me);
        var report = Evaluator.AnalyzeDeck(me);

        double best = double.MinValue;
        RestSiteOption pick = options[0];
        var reasons = new List<string>();

        foreach (var o in options)
        {
            double s = 0;
            string id = SafeId(o);
            string note = "";

            if (IsHeal(o))
            {
                // 缺血越多越该休息；牌组越强越敢休息（活着才能输出）
                s = missing * 120;
                if (missing > 0.4) { s += 40; note = "血量吃紧"; }
                note = note.Length > 0 ? note : "回血稳";
            }
            else if (IsSmithUpgrade(o))
            {
                // 牌组越弱/升级收益越大越该打铁；血多时更倾向升级
                s = (1 - missing) * 70 + (100 - health) * 0.4;
                if (report.Upgraded < report.Size * 0.5) s += 20;
                if (report.Size > 25) s -= 25; // 牌组太杂，升级收益被稀释
                // v2.4.0：用"真实升级收益"修正——牌组里存在高收益可升级卡时才值得打铁
                double bestUp = BestUpgradeGain(me);
                s += Math.Min(45, bestUp);       // 升级收益越高越值
                if (bestUp < 3) s -= 30;         // 几乎无可升级价值 → 降权
                note = bestUp >= 20 ? "打铁提升显著" : "打铁提升战力";
            }
            else
            {
                s = 30; // 其它特殊选项（如铲子挖掘）给中等分
                note = "特殊选项";
            }

            reasons.Add($"{SafeTitle(o)}={s:F0}({note})");
            if (s > best) { best = s; pick = o; }
        }

        why = string.Join("，", reasons);
        return pick;
    }

    /// <summary>问大模型篝火该怎么选。</summary>
    private static RestSiteOption LlmChooseRest(IReadOnlyList<RestSiteOption> options, out string why)
    {
        why = "";
        try
        {
            Player me = Pilot.Me;
            int hp = me?.Creature?.CurrentHp ?? 0;
            int maxHp = me?.Creature?.MaxHp ?? 1;

            string system =
                "你是《杀戮尖塔2》的顶尖决策AI。玩家正在篝火（休息点）做选择。\n" +
                StsKnowledge.Rest +
                "【决策原则】\n" +
                "1) 血量危急（<40%）优先休息回血保命——死了什么都没了；\n" +
                "2) 血量健康且牌组有升级价值时，优先打铁（升级关键卡）；\n" +
                "3) 牌组臃肿/基础卡多时，升级收益低；核心卡少时升级收益高；\n" +
                "4) 若即将面对首领，血线安全比升级更重要。\n" +
                "只输出 JSON：{\"pick\":选项序号(1开始),\"reason\":\"不超过20字理由\"}";

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"【生命】{hp}/{maxHp}（{((maxHp > 0 ? (double)hp / maxHp : 1) * 100):F0}%）");
            sb.AppendLine($"【金币】{me?.Gold}");
            sb.AppendLine("【牌组评估】");
            sb.Append(Evaluator.DescribeDeckReport(me));
            // v2.4.0：给出"最值得升级的卡"真实收益排序，供 LLM 判断打铁价值
            sb.AppendLine("【最值得升级的卡（升级收益排序）】");
            sb.Append(DescribeUpgradeCandidates(me, 4));
            sb.AppendLine("【篝火选项】");
            for (int i = 0; i < options.Count; i++)
            {
                var o = options[i];
                string desc = "";
                try { desc = o.Description?.GetRawText() ?? ""; } catch { }
                sb.AppendLine($"  {i + 1}. {SafeTitle(o)}（id={SafeId(o)}）{(string.IsNullOrEmpty(desc) ? "" : "  " + desc)}");
            }
            sb.AppendLine("请选择，只输出 JSON。");

            string answer = LlmClient.AskAsync(system, sb.ToString()).GetAwaiter().GetResult();
            if (string.IsNullOrEmpty(answer)) return null;
            var dict = LlmClient.ParseJsonObject(answer);
            if (dict == null) return null;
            int? pick = LlmClient.GetInt(dict, "pick");
            string reason = LlmClient.GetStr(dict, "reason") ?? "";
            if (pick == null || pick < 1 || pick > options.Count) return null;

            var chosen = options[pick.Value - 1];
            why = $"LLM：{SafeTitle(chosen)}（{reason}）";
            return chosen;
        }
        catch (Exception e)
        {
            Pilot.Warn("LLM 篝火决策异常: " + e.Message);
            return null;
        }
    }

    private static bool IsHeal(RestSiteOption o) => SafeId(o).Contains("Heal", StringComparison.OrdinalIgnoreCase);
    private static bool IsSmithUpgrade(RestSiteOption o) => SafeId(o).Contains("Smith", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// v2.4.0：牌组里"最值得升级的那张卡"的升级收益（0~∞，裁剪到易读量级）。
    /// 走 ScreenRouter 的真实克隆差分（本体重算数值），与篝火选牌逻辑同源。
    /// </summary>
    public static double BestUpgradeGain(Player me)
    {
        try
        {
            var deck = me?.Deck?.Cards?.Where(c => c != null).ToList();
            if (deck == null || deck.Count == 0) return 0;
            double best = 0;
            foreach (var card in deck)
            {
                try
                {
                    if (!card.IsUpgradable) continue;
                    double g = ScreenRouter.UpgradeGainOf(card);
                    if (g > best) best = g;
                }
                catch { }
            }
            return best;
        }
        catch { return 0; }
    }

    /// <summary>
    /// v2.4.0：列出牌组里"最值得升级的卡"（用于打铁界面/日志/LLM 提示）。
    /// 返回 (卡, 升级收益) 降序。
    /// </summary>
    public static List<(CardModel Card, double Gain)> RankUpgradeCandidates(Player me, int top = 5)
    {
        var list = new List<(CardModel, double)>();
        try
        {
            var deck = me?.Deck?.Cards?.Where(c => c != null).ToList() ?? new List<CardModel>();
            foreach (var c in deck)
            {
                if (!c.IsUpgradable) continue;
                double g = ScreenRouter.UpgradeGainOf(c);
                if (g > 0) list.Add((c, g));
            }
            list.Sort((a, b) => b.Item2.CompareTo(a.Item2));
        }
        catch { }
        return list.Take(top).ToList();
    }

    /// <summary>升级推荐文本（供 LLM / 日志）。</summary>
    public static string DescribeUpgradeCandidates(Player me, int top = 4)
    {
        var ranked = RankUpgradeCandidates(me, top);
        if (ranked.Count == 0) return "  （当前牌组没有可升级的牌）";
        var sb = new System.Text.StringBuilder();
        foreach (var (c, g) in ranked)
        {
            var b = CardKnowledge.Profile(c);
            var a = ScreenRouter.UpgradedProfile(c);
            sb.AppendLine($"  · {Brain.SafeTitle(c)}：升级收益 {g:F0}" +
                          (b != null && a != null
                              ? $"（伤害{b.TotalDamage}→{a.TotalDamage} 格挡{b.Block}→{a.Block} 费{b.Cost}→{a.Cost}）"
                              : ""));
        }
        return sb.ToString();
    }

    // =====================================================================
    //  二、商店：买什么、删不删牌
    // =====================================================================

    /// <summary>商店购买决策结果。</summary>
    public struct ShopDecision
    {
        public int BuyIndex;        // 要买的条目索引（对应传入列表），-1 表示不再买
        public string Why;
    }

    /// <summary>
    /// 决定要不要删牌（商人删牌服务）。牌组臃肿/有诅咒时强烈建议删。
    /// v2.4.0：用 <see cref="ScreenRouter.RankRemovalCandidates"/> 的真实"最该删"排序，
    /// 而不是只看诅咒/基础卡计数。
    /// </summary>
    public static bool ShouldRemoveCard(Player me, int removalCost, out string why)
    {
        why = "";
        try
        {
            int gold = me?.Gold ?? 0;
            if (gold < removalCost) { why = $"金币不足（{gold}<{removalCost}）"; return false; }
            var r = Evaluator.AnalyzeDeck(me);
            // 有诅咒/状态 → 必删
            if (r.Curses > 0) { why = $"有 {r.Curses} 张诅咒/状态，优先移除"; return true; }

            // v2.4.0：真实"最该删"的价值越低 → 越值得花钱删
            double worst = ScreenRouter.WorstKeepValue(me, out string worstName);
            if (worst < 42)
            {
                why = $"最该删的是「{worstName}」（保留价值仅 {worst:F0}），值得花钱移除";
                return true;
            }

            if (r.StrikeLike >= 3 || r.DefendLike >= 3)
            {
                why = $"基础卡偏多（打击{r.StrikeLike}/防御{r.DefendLike}），值得删";
                return true;
            }
            if (r.Size >= 20) { why = $"牌组臃肿({r.Size}张)，精简优先"; return true; }
            why = $"牌组精炼（{r.Size}张），暂无删除必要";
            return false;
        }
        catch { return false; }
    }

    /// <summary>
    /// 在可买列表里挑一个买。返回索引；-1 = 不买。
    /// </summary>
    public static ShopDecision ChooseShopPurchase(Player me, IReadOnlyList<ShopItem> items, out string why)
    {
        why = "";
        var d = new ShopDecision { BuyIndex = -1 };
        if (items == null || items.Count == 0) { why = "没有可买的"; return d; }

        var affordable = new List<int>();
        for (int i = 0; i < items.Count; i++)
            if (items[i].Cost <= (me?.Gold ?? 0) && items[i].Stocked) affordable.Add(i);
        if (affordable.Count == 0) { why = "金币不足，买不起任何东西"; return d; }

        if (Brain.UseLlmPublic)
        {
            int llm = LlmChooseShopPurchase(me, items, affordable, out string llmWhy);
            if (llm >= 0 || llm == -1) { if (!string.IsNullOrEmpty(llmWhy)) { d.BuyIndex = llm; why = llmWhy; return d; } }
            if (!Brain.AllowFallbackPublic) return d;
        }

        // 本地兜底：按性价比打分
        return ScoreShopLocal(me, items, affordable, out why);
    }

    private static ShopDecision ScoreShopLocal(Player me, IReadOnlyList<ShopItem> items, List<int> affordable, out string why)
    {
        var d = new ShopDecision { BuyIndex = -1 };
        var r = Evaluator.AnalyzeDeck(me);
        double best = double.MinValue; int bestIdx = -1; string bestWhy = "";

        foreach (int i in affordable)
        {
            var it = items[i];
            double s = it.Kind switch
            {
                ShopKind.Relic => 70,      // 遗物通常最值
                ShopKind.Card => ScoreShopCard(it.Card, r),
                ShopKind.Potion => 35,
                ShopKind.Removal => 60,    // 删牌长期价值高
                _ => 20,
            };
            // 价格惩罚（越贵越谨慎）
            s -= it.Cost * 0.18;
            if (s > best) { best = s; bestIdx = i; bestWhy = $"{it.Name}（评分{s:F0}，价{it.Cost}）"; }
        }

        // 低于阈值就不买了（攒钱）
        if (best < 30) { why = $"最优仅 {best:F0} 分，不值得买，攒钱"; return d; }
        d.BuyIndex = bestIdx;
        why = "本地：" + bestWhy;
        return d;
    }

    private static double ScoreShopCard(CardModel c, Evaluator.DeckReport r)
    {
        if (c == null) return 0;
        double s = 30;
        try
        {
            switch (c.Rarity)
            {
                case CardRarity.Rare: s += 35; break;
                case CardRarity.Uncommon: s += 22; break;
                case CardRarity.Common: s += 12; break;
                case CardRarity.Ancient: s += 45; break;
            }
            // 补短板加成
            if (c.Type == CardType.Attack && r.Attacks < 6) s += 18;
            if (c.Type == CardType.Skill && r.Skills < 5) s += 15;
            if (c.Type == CardType.Power) s += 20;
            if (c.Type == CardType.Curse || c.Type == CardType.Status) s -= 200;

            // v2.4.0：并入 Selector 选牌分（流派协同 / 数值密度 / 牌组稀释），取加权平均
            try
            {
                var sc = Selector.ScoreOptions(new List<CardModel> { c }, Pilot.Me, false);
                double sel = sc?.FirstOrDefault()?.Score ?? 50;
                s = s * 0.5 + sel * 0.5;
            }
            catch { }
        }
        catch { }
        return s;
    }

    private static int LlmChooseShopPurchase(Player me, IReadOnlyList<ShopItem> items, List<int> affordable, out string why)
    {
        why = "";
        try
        {
            string system =
                "你是《杀戮尖塔2》的顶尖决策AI。玩家正在商店，决定要不要买东西。\n" +
                StsKnowledge.Shop +
                "【决策原则】\n" +
                "1) 遗物通常性价比最高，能买优先买；\n" +
                "2) 删牌服务（移除卡）长期价值极高，牌组臃肿或有诅咒时必买；\n" +
                "3) 补牌组短板的卡值得买（缺输出买攻击、缺防御买格挡）；\n" +
                "4) 药水按需购买，不必强求；\n" +
                "5) 金币要留给后面的商店/删牌，不要为买而买；若都不划算就选择不买(0)。\n" +
                "只输出 JSON：{\"buy\":要买的序号(1开始，0表示不买),\"reason\":\"不超过20字理由\"}";

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"【生命】{me?.Creature?.CurrentHp}/{me?.Creature?.MaxHp}  【金币】{me?.Gold}");
            sb.AppendLine("【牌组评估】");
            sb.Append(Evaluator.DescribeDeckReport(me));
            sb.AppendLine("【现有牌组】" + GameState.DescribeDeck(me));
            // v2.4.0：给出"最该删的卡"排序，帮助 LLM 判断删牌服务价值
            var removal = ScreenRouter.RankRemovalCandidates(me, 3);
            if (removal.Count > 0)
            {
                sb.AppendLine("【最该删的卡（保留价值升序）】");
                foreach (var (c, keep) in removal)
                {
                    var p = CardKnowledge.Profile(c);
                    sb.AppendLine($"  · {Brain.SafeTitle(c)}（保留价值 {keep:F0}{(p != null && p.Has(CardKnowledge.Tag.Curse) ? "，诅咒" : "")}）");
                }
            }
            sb.AppendLine("【商店可买】");
            for (int i = 0; i < items.Count; i++)
            {
                var it = items[i];
                string affordableTag = it.Cost <= (me?.Gold ?? 0) ? "" : "（买不起）";
                sb.AppendLine($"  {i + 1}. [{it.KindCn}] {it.Name}  价格{it.Cost}{affordableTag}" +
                              (string.IsNullOrEmpty(it.Detail) ? "" : $"  {it.Detail}"));
            }
            sb.AppendLine("请决定买哪个（或输入 0 不买），只输出 JSON。");

            string answer = LlmClient.AskAsync(system, sb.ToString()).GetAwaiter().GetResult();
            if (string.IsNullOrEmpty(answer)) return -2;
            var dict = LlmClient.ParseJsonObject(answer);
            if (dict == null) return -2;
            int? buy = LlmClient.GetInt(dict, "buy");
            string reason = LlmClient.GetStr(dict, "reason") ?? "";
            if (buy == null) return -2;

            if (buy.Value == 0) { why = $"LLM：不买（{reason}）"; return -1; }
            int idx = buy.Value - 1;
            if (idx < 0 || idx >= items.Count) return -2;
            if (items[idx].Cost > (me?.Gold ?? 0)) { why = $"LLM 选了买不起的，跳过"; return -1; }
            why = $"LLM：买 {items[idx].Name}（{reason}）";
            return idx;
        }
        catch (Exception e)
        {
            Pilot.Warn("LLM 商店决策异常: " + e.Message);
            return -2;
        }
    }

    /// <summary>商店条目的抽象（由 ScreenRouter 从 NMerchantSlot 转换而来）。</summary>
    public sealed class ShopItem
    {
        public ShopKind Kind;
        public string Name = "?";
        public int Cost;
        public bool Stocked = true;
        public CardModel Card;      // Kind=Card 时有值
        public string Detail = "";  // 额外描述

        public string KindCn => Kind switch
        {
            ShopKind.Card => "卡牌",
            ShopKind.Relic => "遗物",
            ShopKind.Potion => "药水",
            ShopKind.Removal => "删牌服务",
            _ => "其它",
        };
    }

    public enum ShopKind { Card, Relic, Potion, Removal, Other }

    // =====================================================================
    //  三、事件房：选哪个选项
    // =====================================================================

    /// <summary>事件选项的抽象（由 ScreenRouter 从 NEventOptionButton 转换而来）。</summary>
    public sealed class EventChoice
    {
        public string Title = "?";
        public string Description = "";
        public bool IsLocked;
        public bool IsProceed;
        public bool WillKillPlayer;
        /// <summary>事件自身的 id（如 <c>ABYSSAL_BATHS</c>），用于查 spire-codex 权威数据。</summary>
        public string EventId;
        /// <summary>该选项自身的 id（如 <c>IMMERSE</c>），用于与官方选项一一对应。</summary>
        public string OptionId;
    }

    /// <summary>从选项列表里反推事件 id（各选项由同一事件生成，取首个非空值即可）。</summary>
    private static string EventKeyOf(IReadOnlyList<EventChoice> options)
    {
        try
        {
            foreach (var o in options)
                if (o != null && !string.IsNullOrEmpty(o.EventId)) return o.EventId;
        }
        catch { }
        return null;
    }

    /// <summary>
    /// 在事件选项里挑一个。返回索引（对应传入列表）。
    /// 优先级：保命（不选致死项）&gt; 人工偏好（事件倾向）&gt; LLM &gt; 本地启发式。
    /// </summary>
    public static int ChooseEventOption(Player me, IReadOnlyList<EventChoice> options, out string why)
    {
        why = "";
        if (options == null || options.Count == 0) return -1;

        // 只考虑可用（未锁定）的选项
        var usable = new List<int>();
        for (int i = 0; i < options.Count; i++)
        {
            var o = options[i];
            if (o.IsLocked) continue;
            if (o.WillKillPlayer) { continue; }  // 绝不自杀
            usable.Add(i);
        }
        if (usable.Count == 0)
        {
            // 兜底：所有选项都被过滤，就只能选第一个未锁定的（哪怕危险）
            for (int i = 0; i < options.Count; i++) if (!options[i].IsLocked) { why = "无安全选项，被迫选择"; return i; }
            return -1;
        }
        if (usable.Count == 1) { why = "唯一安全选项"; return usable[0]; }

        // 大模型
        if (Brain.UseLlmPublic)
        {
            int llm = LlmChooseEventOption(me, options, usable, out string llmWhy);
            if (llm >= 0) { why = llmWhy; return llm; }
            if (!Brain.AllowFallbackPublic) { why = "LLM 无响应，取首个安全项"; return usable[0]; }
        }

        // 本地兜底：按关键词给收益打分
        return ScoreEventLocal(me, options, usable, out why);
    }

    private static int ScoreEventLocal(Player me, IReadOnlyList<EventChoice> options, List<int> usable, out string why)
    {
        var reasons = new List<string>();
        double best = double.MinValue; int bestIdx = usable[0];
        int hp = me?.Creature?.CurrentHp ?? 0;
        int maxHp = me?.Creature?.MaxHp ?? 1;
        double hpRatio = maxHp > 0 ? (double)hp / maxHp : 1;

        foreach (int i in usable)
        {
            var o = options[i];
            string text = (o.Title + " " + o.Description).ToLowerInvariant();
            double s = 0;

            // 关键词收益
            if (Contains(text, "遗物", "relic")) s += 45;
            if (Contains(text, "稀有", "rare")) s += 35;
            if (Contains(text, "金币", "gold")) s += 15;
            if (Contains(text, "最大生命", "max hp", "maxhp")) s += 30;
            if (Contains(text, "升级", "upgrade")) s += 20;
            if (Contains(text, "移除", "remove")) s += 22;
            if (Contains(text, "治疗", "回血", "heal")) s += 18;
            if (Contains(text, "诅咒", "curse")) s -= 35;
            if (Contains(text, "失去", "伤害", "damage", "lose")) s -= 30;
            if (Contains(text, "生命值", "hp")) s -= 5;
            if (Contains(text, "拒绝", "离开", "leave", "proceed")) s += 5; // 保守退路

            // 残血时回避任何伤害类选项
            if (hpRatio < 0.5 && (Contains(text, "伤害", "damage", "失去") || Contains(text, "生命值"))) s -= 45;

            s += 10; // 基准
            reasons.Add($"{o.Title}={s:F0}");
            if (s > best) { best = s; bestIdx = i; }
        }
        why = "本地：" + string.Join("，", reasons);
        return bestIdx;
    }

    private static int LlmChooseEventOption(Player me, IReadOnlyList<EventChoice> options, List<int> usable, out string why)
    {
        why = "";
        try
        {
            string system =
                "你是《杀戮尖塔2》的顶尖决策AI。玩家遇到了一个事件，需要从若干选项中选一个。\n" +
                StsKnowledge.Event +
                "【决策原则】\n" +
                "1) 生存第一：绝不做会导致死亡或濒死的选择（尤其血量低时）；\n" +
                "2) 优先拿遗物、稀有卡、最大生命提升等长期收益；\n" +
                "3) 诅咒/污染牌组要谨慎（除非代价换来的收益极大）；\n" +
                "4) 血量健康时可冒险博高收益，血量低时保守；\n" +
                "5) 结合当前牌组短板判断（缺输出就拿攻击，缺防御就拿格挡）。\n" +
                "只输出 JSON：{\"pick\":选项序号(1开始),\"reason\":\"不超过25字理由\"}";

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"【生命】{me?.Creature?.CurrentHp}/{me?.Creature?.MaxHp}  【金币】{me?.Gold}");
            sb.AppendLine("【牌组评估】");
            sb.Append(Evaluator.DescribeDeckReport(me));
            sb.AppendLine("【现有牌组】" + GameState.DescribeDeck(me));
            // 权威事件数据（spire-codex）：给出该事件所有选项的官方文本，锚定模型不要凭记忆猜
            string codexEv = "";
            try
            {
                string evId = null;
                try { evId = EventKeyOf(options); } catch { }
                if (!string.IsNullOrEmpty(evId)) codexEv = Codex.EventOptionsText(evId);
            }
            catch { }
            if (!string.IsNullOrEmpty(codexEv))
                sb.AppendLine("【该事件官方选项数据（权威）】\n" + codexEv);
            sb.AppendLine("【事件选项】");
            for (int i = 0; i < options.Count; i++)
            {
                var o = options[i];
                string tag = o.IsLocked ? "（不可选）" : (o.WillKillPlayer ? "（会致死）" : "");
                sb.AppendLine($"  {i + 1}. {o.Title}{tag}");
                if (!string.IsNullOrEmpty(o.Description)) sb.AppendLine($"     说明: {o.Description}");
            }
            sb.AppendLine("请选择最优选项，只输出 JSON。");

            string answer = LlmClient.AskAsync(system, sb.ToString()).GetAwaiter().GetResult();
            if (string.IsNullOrEmpty(answer)) return -1;
            var dict = LlmClient.ParseJsonObject(answer);
            if (dict == null) return -1;
            int? pick = LlmClient.GetInt(dict, "pick");
            string reason = LlmClient.GetStr(dict, "reason") ?? "";
            if (pick == null || pick < 1 || pick > options.Count) return -1;

            int idx = pick.Value - 1;
            // 安全检查：LLM 若选了致死/锁定项，覆盖掉
            if (options[idx].IsLocked || options[idx].WillKillPlayer)
            {
                Pilot.Warn("LLM 事件选择不安全，改为首个安全项");
                why = "LLM 选项不安全 → 改选安全项";
                return usable[0];
            }
            why = $"LLM：{options[idx].Title}（{reason}）";
            return idx;
        }
        catch (Exception e)
        {
            Pilot.Warn("LLM 事件决策异常: " + e.Message);
            return -1;
        }
    }

    // =====================================================================
    //  辅助
    // =====================================================================
    private static bool Contains(string text, params string[] keys)
    {
        if (string.IsNullOrEmpty(text)) return false;
        foreach (string k in keys) if (text.Contains(k)) return true;
        return false;
    }

    private static bool SafeEnabled(RestSiteOption o)
    {
        try { return o.IsEnabled; } catch { return true; }
    }

    private static string SafeId(RestSiteOption o)
    {
        try { return o.OptionId ?? ""; } catch { return ""; }
    }

    public static string SafeTitle(RestSiteOption o)
    {
        try { return o.Title?.GetRawText() ?? SafeId(o); } catch { return SafeId(o); }
    }
}
