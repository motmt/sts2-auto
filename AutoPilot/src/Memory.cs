using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using MegaCrit.Sts2.Core.Runs;

namespace AutoPilot;

/// <summary>
/// AI 记忆总结：把本局/历史对局提炼成可读结论，并支持“人工给 AI 选牌建议”。
///
/// 这是玩家与 AI 之间的反馈闭环：
///   玩家在控制台说「这张卡好」→ 写进配置的偏好列表 → 之后所有选卡都优先它。
///   玩家说「别再选这卡」→ 写进禁选列表 → AI 永不选它。
///   AI 每局结束后留一份记忆文件 → 玩家可以回看它为什么这么决策。
/// </summary>
public static class Memory
{
    /// <summary>人工添加选卡偏好。</summary>
    public static string AddPreference(string cardNameOrId)
    {
        if (string.IsNullOrWhiteSpace(cardNameOrId)) return "请给出卡名，例：ap like 献祭";
        string v = cardNameOrId.Trim();
        var list = PilotConfig.Current.PreferredCards;
        if (list.Any(x => x.Equals(v, StringComparison.OrdinalIgnoreCase))) return $"偏好里已经有「{v}」了";
        list.Add(v);
        PilotConfig.Save();
        return $"✅ 已记住：优先选「{v}」（之后所有卡牌奖励都会优先考虑它）";
    }

    public static string RemovePreference(string cardNameOrId)
    {
        string v = (cardNameOrId ?? "").Trim();
        var list = PilotConfig.Current.PreferredCards;
        int removed = list.RemoveAll(x => x.Equals(v, StringComparison.OrdinalIgnoreCase));
        PilotConfig.Save();
        return removed > 0 ? $"已移除偏好「{v}」" : $"偏好里没有「{v}」";
    }

    /// <summary>人工添加禁选卡。</summary>
    public static string AddBan(string cardNameOrId)
    {
        if (string.IsNullOrWhiteSpace(cardNameOrId)) return "请给出卡名，例：ap ban 苦痛";
        string v = cardNameOrId.Trim();
        var list = PilotConfig.Current.BannedCards;
        if (list.Any(x => x.Equals(v, StringComparison.OrdinalIgnoreCase))) return $"禁选里已经有「{v}」了";
        list.Add(v);
        PilotConfig.Save();
        return $"🚫 已记住：永不选「{v}」";
    }

    public static string RemoveBan(string cardNameOrId)
    {
        string v = (cardNameOrId ?? "").Trim();
        var list = PilotConfig.Current.BannedCards;
        int removed = list.RemoveAll(x => x.Equals(v, StringComparison.OrdinalIgnoreCase));
        PilotConfig.Save();
        return removed > 0 ? $"已移除禁选「{v}」" : $"禁选里没有「{v}」";
    }

    /// <summary>列出当前的人工偏好与禁选。</summary>
    public static string ListRules()
    {
        var c = PilotConfig.Current;
        var sb = new StringBuilder();
        sb.AppendLine("AI 记忆 - 人工规则");
        sb.AppendLine("  优先选卡 like: " + (c.PreferredCards.Count == 0 ? "(无)" : string.Join("、", c.PreferredCards)));
        sb.AppendLine("  禁选卡 ban  : " + (c.BannedCards.Count == 0 ? "(无)" : string.Join("、", c.BannedCards)));
        sb.AppendLine("  （用 ap like <卡名> / ap ban <卡名> 添加；ap unlike / ap unban 移除）");
        return sb.ToString();
    }

    /// <summary>
    /// 生成本局的复盘摘要（AI 用自然语言“总结”这一局发生了什么、学到了什么）。
    /// </summary>
    public static string SummarizeCurrentRun()
    {
        var sb = new StringBuilder();
        RunState rs = Pilot.RunStateNow;
        sb.AppendLine("═══ 本局回忆 ═══");
        if (rs == null) { sb.AppendLine("（当前没有进行中的对局）"); return sb.ToString(); }

        sb.AppendLine($"进度：第 {rs.TotalFloor} 层 / 第 {rs.CurrentActIndex + 1} 幕");
        var me = Pilot.Me;
        if (me != null)
        {
            int hp = me.Creature?.CurrentHp ?? 0, maxHp = me.Creature?.MaxHp ?? 1;
            double pct = maxHp > 0 ? hp * 100.0 / maxHp : 0;
            sb.AppendLine($"状态：{hp}/{maxHp} 血（{pct:F0}%）、{me.Gold} 金币、牌组 {me.Deck?.Cards?.Count ?? 0} 张");
            sb.AppendLine("健康度：" + (pct > 70 ? "状态很好，可以更激进（打精英/少休息）"
                                        : pct > 40 ? "状态一般，稳健推进"
                                        : "⚠ 残血，优先休息点、避开精英"));
        }
        sb.AppendLine($"战绩：战斗 {Pilot.CombatsWon} 胜、精英 {Pilot.ElitesWon} 胜、选卡 {Pilot.CardsPicked}、跳过 {Pilot.CardsSkipped}");
        sb.AppendLine();
        sb.AppendLine("最近 12 条决策依据：");
        foreach (string t in Brain.Trace.TakeLast(12)) sb.AppendLine("  " + t);
        return sb.ToString();
    }
}
