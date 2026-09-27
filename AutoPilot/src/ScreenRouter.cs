using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.AutoSlay;
using MegaCrit.Sts2.Core.AutoSlay.Helpers;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Events;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Nodes.Screens.TreasureRoomRelic;
using MegaCrit.Sts2.Core.Nodes.Screens.Shops;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Rooms;

namespace AutoPilot;

/// <summary>
/// 界面路由：把当前打开的浮层界面交给 Brain 决策，然后模拟点击。
///
/// 复用了官方 <see cref="UiHelper"/>（ForceClick 绕过 hover/焦点检查，稳定可靠），
/// 但决策内容全部替换为 AutoPilot 自己的判断，且每个界面都有兜底，
/// 遇到 AI 不认识的新界面会安全交还玩家（而不是像官方那样抛异常终止整局）。
/// </summary>
public static class ScreenRouter
{
    // =====================================================================
    //  防重入：同一浮层实例在"未被换下栈顶"时，最多被 AI 点击 2 次
    //
    //  血泪教训：drain 循环 + 主循环都会调 HandleTopAsync。若点击后界面因动画
    //  尚未真正出栈（WaitScreenClosedAsync 100ms 轮询早退），下一轮 peek 到的
    //  还是同一界面 → 再次 EmitSignal(Pressed) → 游戏侧 NCardRewardSelectionScreen
    //  .OnAlternateRewardSelected 二次触发 → InvalidOperationException（任务已完成）。
    //
    //  ⚠ 不能用"永久拉黑集合"：奖励列表这类界面需要"点开卡牌 → 选完 → 回到列表
    //     再点继续"的多次交互，永久拉黑会卡死。正确做法 = 只统计"连续停留在同一
    //     实例上的点击次数"；一旦栈顶换成别的实例（说明真推进了）立即归零。
    // =====================================================================
    private static ulong _lastTopId;
    private static int _sameTopClicks;   // 同一实例连续点击次数

    /// <summary>处理浮层栈顶界面。返回 true 表示已处理（可以继续 drain），false 表示交给玩家。</summary>
    /// <summary>
    /// 重置"栈顶防重入"计数。战斗中由 <see cref="Brain"/> 的浮层守卫在**每次打算出牌前**调用：
    /// 战斗浮层处理要求"能反复尝试"，而 HandleTopAsync 的 `_sameTopClicks>=2` 是为了防主循环
    /// 空转死循环，不该限制战斗内的重试。故出牌前显式清零。
    /// </summary>
    public static void ResetTopClickGuard() => _sameTopClicks = 0;

    public static async Task<bool> HandleTopAsync(IOverlayScreen top, CancellationToken ct)
    {
        await Pilot.WaitIfPausedAsync(ct);
        if (ct.IsCancellationRequested) return false;

        Node node = top as Node;
        if (node == null || !GodotObject.IsInstanceValid(node)) return false;

        ulong id = node.GetInstanceId();
        if (id != _lastTopId) { _lastTopId = id; _sameTopClicks = 0; }
        // 同一实例连续点 2 次仍没换下去 → 不再点，交给 drain 空转 / 玩家
        if (_sameTopClicks >= 2) return true;
        _sameTopClicks++;

        Type t = node.GetType();
        try
        {
            // ---------- 卡牌奖励（三选一） ----------
            if (top is NCardRewardSelectionScreen cardReward) return await HandleCardRewardAsync(cardReward, ct);

            // ---------- 通用“选一张卡”界面 ----------
            if (top is NChooseACardSelectionScreen choose) return await HandleChooseCardsAsync(choose, ct);

            // ---------- 选一组牌（如宝箱/弃牌） ----------
            if (top is NSimpleCardSelectScreen simple) return await HandleSimpleSelectAsync(simple, ct);

            // ---------- 牌组操作：升级 / 移除 / 变化 / 附魔 ----------
            if (top is NDeckUpgradeSelectScreen up) return await HandleGridPickAsync(up, "升级", ct);
            if (top is NDeckTransformSelectScreen tf) return await HandleGridPickAsync(tf, "变化", ct);
            // ⚠ v2.5.5 血泪修复：`NDeckCardSelectScreen` 是**通用牌组选牌屏**，
            //   商店删牌（MerchantCardRemovalEntry → CardSelectCmd.FromDeckForRemoval）、
            //   事件删牌、以及其它"从牌组里选一张"全都复用它 —— 但**动作语义完全不同**！
            //   旧实现硬编码成 "选择"（= PickAction.Choose = 挑**最高分**的牌），
            //   于是**商店删牌时 AI 挑了自己评分最高的卡去删**（用户实测：刚选的
            //   预备打击 SETUP_STRIKE 评分 95 最高 → 被删掉）。日志铁证：
            //     商店 → 买「?」价100（优先删诅咒）
            //     选择 → SETUP_STRIKE（拿 分95｜机制: 伤害/力量/打击类）
            //     Player 1 chose cards [SETUP_STRIKE]
            //   修法：从屏幕自身的 `_prefs.Prompt` 读出真实意图（LocString.LocEntryKey ==
            //   TO_REMOVE / TO_UPGRADE / TO_TRANSFORM / TO_ENCHANT / TO_DISCARD），
            //   映射到正确的动作语义；读不到再退回 "选择"。
            if (top is NDeckCardSelectScreen dc)
                return await HandleGridPickAsync(dc, DeckSelectActionOf(dc), ct);
            if (top is NDeckEnchantSelectScreen en) return await HandleGridPickAsync(en, "附魔", ct);

            // ---------- 战斗内选牌堆（弃牌/消耗堆里挑牌） ----------
            if (top is NCombatPileCardSelectScreen cp) return await HandleGridPickAsync(cp, "选牌堆", ct);

            // ---------- 选一包牌（"选卡包"，如卷轴箱/宝箱怪等遗物） ----------
            // ⚠ v2.5.6 血泪修复：NChooseABundleSelectionScreen 是**两段式**界面
            //   （选 NCardBundle → 弹出预览 %BundlePreviewContainer → 点 %Confirm 才提交），
            //   它**没有 NGridCardHolder**（牌在 NCardBundle 里，不是网格），旧代码把它丢给
            //   HandleGridPickAsync → 找不到任何 holder → 直接 return false →
            //   日志「界面 NChooseABundleSelectionScreen 无 AI 处理器」，AI 卡住不动。
            //   正确流程：给每个 bundle 打分 → 点最优 bundle 的 Hitbox（触发 OnBundleClicked
            //   → 开预览 + Enable %Confirm/%Cancel）→ 等 %Confirm 可用 → 点它。
            if (top is NChooseABundleSelectionScreen bundle) return await HandleBundlePickAsync(bundle, ct);

            // ---------- 选遗物（首领奖励等） ----------
            if (top is NChooseARelicSelection relic) return await HandleRelicPickAsync(relic, ct);

            // ---------- 战后奖励列表 ----------
            if (top is MegaCrit.Sts2.Core.Nodes.Screens.NRewardsScreen rewards) return await HandleRewardsAsync(rewards, ct);

            // ---------- 水晶球（占卜事件） ----------
            if (top is MegaCrit.Sts2.Core.Nodes.Events.Custom.CrystalSphere.NCrystalSphereScreen sphere)
                return await HandleProceedOnlyAsync(sphere, "%ProceedButton", ct);

            // ---------- 游戏结束 ----------
            if (top is MegaCrit.Sts2.Core.Nodes.Screens.GameOverScreen.NGameOverScreen over)
                return await HandleGameOverAsync(over, ct);

            // 注意：篝火(NRestSiteRoom)/事件(NEventRoom) 实现的是 IScreenContext 而非
            // IOverlayScreen，不会出现在这里；它们由 Brain.HandleRoomAsync 房间级驱动
            // （HandleRestSiteRoomAsync / HandleEventRoomAsync）。此处不再分发，避免死代码。

            Pilot.Log($"遇到未适配界面 {t.Name}，交还玩家");
            return false;
        }
        catch (Exception e)
        {
            Pilot.Warn($"界面 {t.Name} 处理异常: {e.Message}");
            return false;
        }
    }

    // =====================================================================
    //  卡牌奖励：让 Brain 打分选牌
    // =====================================================================
    private static async Task<bool> HandleCardRewardAsync(NCardRewardSelectionScreen screen, CancellationToken ct)
    {
        await MainThreadRunner.DelayAsync(400, ct);
        List<NCardHolder> holders = UiHelper.FindAll<NCardHolder>(screen);
        if (holders.Count == 0) { Pilot.Warn("卡牌奖励界面没有卡"); return false; }

        // 取出候选卡（与 holder 一一对应）
        var options = new List<CardModel>();
        var map = new List<(NCardHolder holder, CardModel card)>();
        foreach (NCardHolder h in holders)
        {
            CardModel c = TryGetCardFromHolder(h);
            if (c != null) map.Add((h, c));
            options.Add(c);
        }

        CardModel pick = Brain.ChooseCardReward(map.Select(m => m.card).Where(c => c != null).ToList(), out string why);

        if (pick == null)
        {
            if (!PilotConfig.Current.AllowCardSkip) { Pilot.Log("AI 未选出卡但禁用了跳过，随便选一张"); pick = map.FirstOrDefault().card; }
            else
            {
                Pilot.Log("AI 判断本次奖励不值得选，跳过（点跳过按钮）");
                Pilot.CardsSkipped++;
                // 卡牌奖励界面的跳过按钮：直接找按钮点击，避免依赖具体静态方法名
                NProceedButton skip = UiHelper.FindFirst<NProceedButton>(screen);
                if (skip != null) await UiHelper.Click(skip);
                else { holders[0].EmitSignal(NCardHolder.SignalName.Pressed, holders[0]); }
                await MainThreadRunner.DelayAsync(400, ct);
                return true;
            }
        }

        var target = map.FirstOrDefault(m => ReferenceEquals(m.card, pick));
        if (target.holder == null) { TargetFallback(map, out target); }
        if (target.holder == null) return false;

        Pilot.Log($"选卡 → {Brain.SafeTitle(pick)}（{why}）");
        Pilot.CardsPicked++;
        target.holder.EmitSignal(NCardHolder.SignalName.Pressed, target.holder);
        await WaitScreenClosedAsync(screen, ct);
        return true;
    }

    private static void TargetFallback(List<(NCardHolder holder, CardModel card)> map, out (NCardHolder holder, CardModel card) t)
    {
        t = map.FirstOrDefault();
    }

    /// <summary>尽力从 CardHolder 上取出 CardModel（反射兜底，兼容不同版本字段名）。</summary>
    public static CardModel TryGetCardFromHolder(Node holder)
    {
        try
        {
            if (holder == null) return null;
            foreach (string name in new[] { "Card", "CardModel", "_card", "Model" })
            {
                var p = holder.GetType().GetProperty(name);
                if (p != null && typeof(CardModel).IsAssignableFrom(p.PropertyType))
                {
                    return p.GetValue(holder) as CardModel;
                }
                var f = holder.GetType().GetField(name,
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                if (f != null && typeof(CardModel).IsAssignableFrom(f.FieldType))
                {
                    return f.GetValue(holder) as CardModel;
                }
            }
        }
        catch { }
        return null;
    }

    private static PickAction ActionOf(string action) => action switch
    {
        "升级" => PickAction.Upgrade,
        "移除" => PickAction.Remove,
        "变化" => PickAction.Transform,
        "附魔" => PickAction.Enchant,
        _ => PickAction.Choose,
    };

    /// <summary>
    /// v2.5.5：判定 <see cref="NDeckCardSelectScreen"/> 的**真实动作语义**。
    ///
    /// 这个屏是通用容器，商店删牌 / 事件删牌 / 事件变化 都复用它；屏幕把意图存在私有
    /// 字段 `_prefs`（<c>CardSelectorPrefs</c>）里，而 `_prefs.Prompt` 是 <c>LocString</c>
    /// （公开属性 <c>LocEntryKey</c> / <c>LocTable</c>）。
    ///
    /// `CardSelectorPrefs` 里预置的 key：
    ///   TO_REMOVE → 移除   TO_UPGRADE → 升级   TO_TRANSFORM → 变化
    ///   TO_ENCHANT → 附魔  TO_DISCARD → 弃牌   TO_EXHAUST → 消耗
    ///
    /// 读不到 key 时再退一步读 <c>GetRawText()</c> 做中文关键词匹配；
    /// 仍判不出才退回 "选择"（保持旧行为，不会更差）。
    /// </summary>
    private static string DeckSelectActionOf(NDeckCardSelectScreen screen)
    {
        try
        {
            object prefs = null;
            for (Type t = screen.GetType(); t != null && prefs == null; t = t.BaseType)
            {
                var f = t.GetField("_prefs",
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Public);
                if (f != null) prefs = f.GetValue(screen);
            }
            if (prefs == null) return "选择";

            object prompt = prefs.GetType().GetProperty("Prompt")?.GetValue(prefs);
            if (prompt == null) return "选择";

            string key = prompt.GetType().GetProperty("LocEntryKey")?.GetValue(prompt) as string ?? "";
            string act = key switch
            {
                "TO_REMOVE" => "移除",
                "TO_UPGRADE" => "升级",
                "TO_TRANSFORM" => "变化",
                "TO_ENCHANT" => "附魔",
                "TO_DISCARD" => "移除",     // 弃牌 = 丢掉，朝"最该丢"的方向选
                "TO_EXHAUST" => "移除",     // 消耗 = 烧掉，同上
                _ => null,
            };
            if (act != null)
            {
                Pilot.Log($"[删牌意图] 由 CardSelectorPrefs 判定：{key} → 动作「{act}」");
                return act;
            }

            // 兜底：读原始文本做中文关键词匹配（不同语言/版本可能 key 名不同）
            string raw = "";
            try { raw = prompt.GetType().GetMethod("GetRawText")?.Invoke(prompt, null) as string ?? ""; }
            catch { }
            if (!string.IsNullOrEmpty(raw))
            {
                if (raw.Contains("移除") || raw.Contains("删除") || raw.Contains("remove", StringComparison.OrdinalIgnoreCase)) return "移除";
                if (raw.Contains("升级") || raw.Contains("upgrade", StringComparison.OrdinalIgnoreCase)) return "升级";
                if (raw.Contains("变化") || raw.Contains("transform", StringComparison.OrdinalIgnoreCase)) return "变化";
                if (raw.Contains("附魔") || raw.Contains("enchant", StringComparison.OrdinalIgnoreCase)) return "附魔";
            }
            Pilot.Warn($"[删牌意图] 未能判出动作语义（key='{key}'，原文='{raw}'）→ 退回「选择」");
        }
        catch (Exception e) { Pilot.Warn("[删牌意图] 判定失败: " + e.Message); }
        return "选择";
    }

    private static string DescribePickReason(List<CardModel> cards, int idx, string action)
    {
        try
        {
            if (idx < 0 || idx >= cards.Count) return action;
            if (action == "升级")
            {
                var b = CardKnowledge.Profile(cards[idx]);
                var a = UpgradedProfile(cards[idx]);
                if (b != null && a != null)
                    return $"升级收益 伤害{b.TotalDamage}→{a.TotalDamage} 格挡{b.Block}→{a.Block} 费{b.Cost}→{a.Cost}";
            }
            var sc = Selector.ScoreOptions(new List<CardModel> { cards[idx] }, Pilot.Me);
            var s0 = sc?.FirstOrDefault();
            if (s0 != null)
                return $"{s0.VerdictCn} 分{s0.Score:F0}" +
                       (s0.Reasons.Count > 0 ? "｜" + string.Join("；", s0.Reasons.Take(2)) : "");
        }
        catch { }
        return action;
    }

    // =====================================================================
    //  通用选卡（事件/商店要求选牌）
    // =====================================================================
    private static async Task<bool> HandleChooseCardsAsync(NChooseACardSelectionScreen screen, CancellationToken ct)
    {
        // v2.4.3：NChooseACardSelectionScreen.SelectHolder 里有 `Time.GetTicksMsec() - _openedTicks > 350`
        // 的门槛 —— 打开后 350ms 内点击**完全无效**（会被静默忽略），所以必须先等过去。
        await MainThreadRunner.DelayAsync(600, ct);
        List<NCardHolder> holders = UiHelper.FindAll<NCardHolder>(screen);
        if (holders.Count == 0) return false;

        var cards = holders.Select(h => TryGetCardFromHolder(h)).ToList();
        int pickIdx = BestIndexForAction(cards, PickAction.Choose);
        if (pickIdx < 0) pickIdx = 0;

        // v2.4.0：先试大模型裁定（带本地预评分），失败回退本地
        try
        {
            var llmPick = Brain.ChooseCardForAction(cards, "选卡", Pilot.Me);
            if (llmPick != null)
            {
                int li = cards.FindIndex(c => ReferenceEquals(c, llmPick));
                if (li >= 0) pickIdx = li;
            }
        }
        catch { }

        Pilot.Log($"选卡（战斗中/药水）→ {cards[pickIdx]?.Id.Entry ?? "?"}（{DescribePickReason(cards, pickIdx, "选卡")}）");

        // 多轮尝试：350ms 门槛虽已等待，但界面淡入动画期间点击仍可能不生效 → 最多试 4 轮。
        for (int attempt = 0; attempt < 4; attempt++)
        {
            if (ct.IsCancellationRequested) return true;
            if (!IsScreenStillOpen(screen)) return true;
            try
            {
                if (pickIdx >= 0 && pickIdx < holders.Count)
                    holders[pickIdx].EmitSignal(NCardHolder.SignalName.Pressed, holders[pickIdx]);
            }
            catch (Exception e) { Pilot.Warn("点击选卡失败: " + e.Message); }
            await MainThreadRunner.DelayAsync(400, ct);
        }

        // 兜底：选不掉就点 Skip（技能药水 canSkip=true；不选也比把战斗卡死强）
        if (IsScreenStillOpen(screen))
        {
            try
            {
                var skip = FindByNameRecursive(screen, "SkipButton") as NClickableControl;
                if (skip != null && GodotObject.IsInstanceValid(skip) && IsEffectivelyVisible(skip))
                {
                    Pilot.Log("选卡界面未能选中 → 兜底点 Skip");
                    await UiHelper.Click(skip);
                    await MainThreadRunner.DelayAsync(350, ct);
                }
            }
            catch (Exception e) { Pilot.Warn("点 Skip 失败: " + e.Message); }
        }

        await WaitScreenClosedAsync(screen, ct);
        return true;
    }

    private static async Task<bool> HandleSimpleSelectAsync(NSimpleCardSelectScreen screen, CancellationToken ct)
    {
        await MainThreadRunner.DelayAsync(300, ct);
        List<NCardHolder> holders = UiHelper.FindAll<NCardHolder>(screen);
        if (holders.Count == 0) return false;

        var cards = holders.Select(h => TryGetCardFromHolder(h)).ToList();
        int pickIdx = BestIndexForAction(cards, PickAction.Choose);
        if (pickIdx < 0) pickIdx = 0;

        // NCardHolder 有 Pressed 信号
        holders[pickIdx].EmitSignal(NCardHolder.SignalName.Pressed, holders[pickIdx]);
        await MainThreadRunner.DelayAsync(300, ct);

        // 有些界面需要再点确认（NConfirmButton）→ 走两段式
        await ClickConfirmAsync(screen, ct);
        await WaitScreenClosedAsync(screen, ct);
        return true;
    }

    /// <summary>
    /// 从一组卡里挑最值得动的一张（用于升级/移除/变化）。
    ///
    /// v2.4.0：接入 <see cref="Selector"/> 打分引擎（战力/成长/一致性/生存/遗物/风险/稀释
    /// + 动态权重），并支持"动作语义"——升级要挑**最值得升级**的、移除要挑**最该删**的。
    /// 见 <see cref="BestIndexForAction"/>。
    /// </summary>
    private static int BestIndexOf(List<CardModel> cards)
        => BestIndexForAction(cards, PickAction.Choose);

    /// <summary>选牌动作语义（决定打分取向）。</summary>
    public enum PickAction
    {
        Choose,     // 通用"选一张"
        Upgrade,    // 升级 → 要"升级收益最大"的
        Remove,     // 移除/删除 → 要"最该滚蛋"的
        Enchant,    // 附魔 → 要"底子最好、收益最大"的
        Transform,  // 变化 → 要"最弱、最该变的"
    }

    private static int BestIndexForAction(List<CardModel> cards, PickAction action)
    {
        if (cards == null || cards.Count == 0) return -1;
        var me = Pilot.Me;

        // ---------- 通用/附魔/变化：直接取 Selector 最高分 ----------
        if (action == PickAction.Choose || action == PickAction.Enchant)
            return ArgMax(Selector.ScoreOptions(cards, me));

        // ---------- 升级：真实"升级前后数值差"排序（军师思路） ----------
        if (action == PickAction.Upgrade)
        {
            int bi = -1; double bs = double.MinValue;
            for (int i = 0; i < cards.Count; i++)
            {
                CardModel c = cards[i];
                if (c == null) continue;
                double s = UpgradeValue(cards, i);
                if (s > bs) { bs = s; bi = i; }
            }
            return bi >= 0 ? bi : ArgMax(Selector.ScoreOptions(cards, me));
        }

        // ---------- 移除/变化：最该滚蛋的（诅咒/状态 → 最弱牌） ----------
        if (action == PickAction.Remove || action == PickAction.Transform)
        {
            // ① 优先诅咒 / 状态（污染牌组，删掉净赚）
            for (int pass = 0; pass < 2; pass++)
            {
                int bi = -1; double bs = double.MinValue;
                for (int i = 0; i < cards.Count; i++)
                {
                    var p = CardKnowledge.Profile(cards[i]);
                    if (p == null) continue;
                    double s;
                    if (pass == 0)
                    {
                        if (!p.Has(CardKnowledge.Tag.Curse) && !p.Has(CardKnowledge.Tag.Status)) continue;
                        s = (p.Has(CardKnowledge.Tag.Curse) ? 1000 : 800) - DeckKeepValue(cards, i);
                    }
                    else
                    {
                        // ② 没有诅咒/状态 → 挑"在牌组里最不值钱"的（Selector 最低分）
                        s = -DeckKeepValue(cards, i);
                    }
                    if (s > bs) { bs = s; bi = i; }
                }
                if (bi >= 0) return bi;
            }
            var sc = Selector.ScoreOptions(cards, me);
            int wi = 0; double ws = double.MaxValue;
            for (int i = 0; i < sc.Count; i++) if (sc[i].Score < ws) { ws = sc[i].Score; wi = i; }
            return wi;
        }

        return ArgMax(Selector.ScoreOptions(cards, me));
    }

    private static int ArgMax(List<Selector.CandidateScore> sc)
    {
        if (sc == null || sc.Count == 0) return -1;
        int bi = -1; double bs = double.MinValue;
        for (int i = 0; i < sc.Count; i++)
            if (sc[i] != null && sc[i].Score > bs) { bs = sc[i].Score; bi = i; }
        return bi;
    }

    /// <summary>
    /// 取某张卡"升级后"的真实画像：临时克隆 + UpgradeInternal，读 DynamicVars。
    /// 对原卡零副作用（OnUpgrade 只改克隆自身数值）。失败返回 null。
    ///
    /// ⚠ v2.6.0 血泪修复：旧代码用 <c>card.CreateCloneForPlayer(...)</c>，而
    ///   <c>CardModel.CreateClone()</c> 里有一条硬门禁：
    ///     <c>if (Pile != null &amp;&amp; !Pile.Type.IsCombatPile()) throw ...</c>
    ///   —— **非战斗牌堆（牌组/抽牌堆/弃牌堆）里的卡一律不能 CreateClone**。
    ///   于是**篝火/商店/事件**里（非战斗环境）对任何牌调用都抛
    ///   `Cannot create a clone of a card that is not in a combat pile.`
    ///   → `BestUpgradeGain` 恒为 0 → 提示词里"最值得升级的卡"全空 →
    ///   LLM 判定"无可升级牌" → **永远休息、从不敢打铁（敲牌）**。日志铁证：
    ///     `升级画像估算失败（STRIKE_IRONCLAD）：Cannot create a clone ...` ×10+ / 次篝火
    ///     `篝火 → 休息（LLM：休息（无可升级牌，血量82%仍宜休息稳血线））`
    ///   正解（官方注释指明）：跑局级克隆要走 <see cref="ICardScope.CloneCard"/>，
    ///   即 <c>card.CardScope.CloneCard(card)</c> —— 它**没有战斗牌堆门禁**。
    ///   优先用它；再退回 CreateCloneForPlayer（战斗内仍可用）；最后退回"只升克隆本体"。
    /// </summary>
    public static CardKnowledge.CardProfile UpgradedProfile(CardModel card)
    {
        if (card == null) return null;
        try
        {
            CardModel clone = CloneForUpgradeProbe(card);
            if (clone == null) return null;
            clone.UpgradeInternal();
            return CardKnowledge.Profile(clone);
        }
        catch (Exception e)
        {
            Pilot.Warn($"升级画像估算失败（{card.Id.Entry}）：{e.Message}");
            return null;
        }
    }

    /// <summary>
    /// v2.6.0：为"升级估值"取一张临时克隆卡，**在非战斗环境同样可用**。
    /// 三条路径依次尝试（第一条成功即返回）：
    ///   ① <c>card.CardScope.CloneCard(card)</c> —— 官方跑局级克隆，无战斗牌堆门禁（正解）
    ///   ② <c>card.CreateCloneForPlayer(owner)</c> —— 战斗内路径（旧行为）
    ///   ③ 反射兜底：若上面都失败，尝试用 <c>ModelDb.GetById(id).ToMutable()</c> 造一张干净可变卡
    /// </summary>
    private static CardModel CloneForUpgradeProbe(CardModel card)
    {
        // ① 跑局级克隆（覆盖篝火/商店/事件等非战斗场景）
        try
        {
            var scope = card.CardScope;
            if (scope != null)
            {
                CardModel c = scope.CloneCard(card);
                if (c != null) return c;
            }
        }
        catch { /* 落到 ② */ }

        // ② 战斗内克隆
        try
        {
            MegaCrit.Sts2.Core.Entities.Players.Player owner = card.Owner ?? Pilot.Me;
            CardModel c = card.CreateCloneForPlayer(owner);
            if (c != null) return c;
        }
        catch { /* 落到 ③ */ }

        // ③ 兜底：从规范卡造一张可变副本（读 DynamicVars 不需要 owner）
        try
        {
            CardModel c = card.ToMutable();
            if (c != null) return c;
        }
        catch { }

        return null;
    }

    /// <summary>
    /// 单卡"升级收益"（供 RoomBrain 打铁决策复用）。返回可比较的相对值。
    /// </summary>
    public static double UpgradeGainOf(CardModel card)
    {
        if (card == null) return 0;
        try
        {
            var before = CardKnowledge.Profile(card);
            if (before == null) return 0;
            var after = UpgradedProfile(card);
            double gain = 0;
            if (after != null)
            {
                gain += (after.TotalDamage - before.TotalDamage) * 1.8;
                gain += (after.Block - before.Block) * 1.4;
                gain += (after.Draw - before.Draw) * 12;
                gain += (after.EnergyGain - before.EnergyGain) * 14;
                gain += (after.Poison - before.Poison) * 3.0;
                gain += (after.StrengthGain - before.StrengthGain) * 12;
                gain += (after.DexterityGain - before.DexterityGain) * 10;
                gain += (after.VulnerableStacks - before.VulnerableStacks) * 4;
                gain += (after.WeakStacks - before.WeakStacks) * 4;
                gain += (after.HealAmount - before.HealAmount) * 2;
                gain += (before.Cost - after.Cost) * 15;
                foreach (var t in after.Tags) if (!before.Has(t)) gain += 2;
            }
            if (gain == 0)
                gain += ParseNumericDelta(card.GetDescriptionForUpgradePreview(), card.GetDescriptionForPile(PileType.None));
            return gain;
        }
        catch { return 0; }
    }

    /// <summary>
    /// 升级价值 = Σ**真实数值差**（升级后 − 升级前），仅作为相对排序键。
    ///
    /// 实现方式（v2.4.0）：取一张**临时克隆**（<see cref="CardModel.CreateClone"/>，
    /// 内部是完整成员克隆，保留 DynamicVars / 升级等级），在克隆上调用
    /// <see cref="CardModel.UpgradeInternal"/>，再读 DynamicVars 对比。
    /// OnUpgrade() 只改卡自身的数值/费用、同步执行、不触游戏状态，故对原卡零副作用；
    /// 数值取自游戏本体求值结果，对任意 mod 卡同样成立。
    /// </summary>
    private static double UpgradeValue(List<CardModel> cards, int idx)
    {
        CardModel card = cards[idx];
        double gain = UpgradeGainOf(card);
        // 底子越好越值得升级；纯 0 增益的牌降权
        double baseScore = Selector.ScoreOptions(new List<CardModel> { card }, Pilot.Me)
                                 ?.FirstOrDefault()?.Score ?? 50;
        return gain * 2.0 + baseScore * 0.5;
    }

    /// <summary>从"升级预览文本 vs 普通文本"中解析数值增量（末位兜底）。</summary>
    private static double ParseNumericDelta(string previewText, string normalText)
    {
        try
        {
            var nums = System.Text.RegularExpressions.Regex.Matches(previewText ?? "", @"\d+");
            var baseNums = System.Text.RegularExpressions.Regex.Matches(normalText ?? "", @"\d+");
            double sum = 0;
            int n = Math.Min(nums.Count, baseNums.Count);
            for (int i = 0; i < n; i++)
            {
                if (int.TryParse(nums[i].Value, out int a) && int.TryParse(baseNums[i].Value, out int b) && a > b)
                    sum += (a - b);
            }
            return sum;
        }
        catch { return 0; }
    }

    /// <summary>"保留在牌组里的价值"（越高越不该删）。用于移除/变化排序。</summary>
    private static double DeckKeepValue(List<CardModel> cards, int idx)
    {
        var sc = Selector.ScoreOptions(new List<CardModel> { cards[idx] }, Pilot.Me);
        return sc?.FirstOrDefault()?.Score ?? 50;
    }

    /// <summary>
    /// 牌组里"最该删"的卡排序（升序 = 最该删在前）。诅咒/状态强制置顶。
    /// 返回 (卡, 保留价值)。
    /// </summary>
    public static List<(CardModel Card, double KeepValue)> RankRemovalCandidates(Player me, int bottom = 5)
    {
        var list = new List<(CardModel, double)>();
        try
        {
            var deck = me?.Deck?.Cards?.Where(c => c != null).ToList() ?? new List<CardModel>();
            foreach (var c in deck)
            {
                var p = CardKnowledge.Profile(c);
                double keep = DeckKeepValue(deck, deck.IndexOf(c));
                if (p != null && p.Has(CardKnowledge.Tag.Curse)) keep = -100;   // 诅咒：最先删
                else if (p != null && p.Has(CardKnowledge.Tag.Status)) keep = -80;
                list.Add((c, keep));
            }
            list.Sort((a, b) => a.Item2.CompareTo(b.Item2));
        }
        catch { }
        return list.Take(bottom).ToList();
    }

    /// <summary>牌组里最该删的那张的"保留价值"（越低越该删）。</summary>
    public static double WorstKeepValue(Player me, out string cardName)
    {
        cardName = "";
        try
        {
            var ranked = RankRemovalCandidates(me, 1);
            if (ranked.Count == 0) return 100;
            cardName = Brain.SafeTitle(ranked[0].Card);
            return ranked[0].KeepValue;
        }
        catch { return 100; }
    }

    // =====================================================================
    //  牌组格子选择（升级/变化/移除/附魔）
    //
    //  v2.4.0：选牌后必须走**两段式确认**（主确认 → 预览确认），
    //  且 NDeckEnchantSelectScreen / NDeckUpgradeSelectScreen 的预览容器是
    //  根据"选了几张"在点击后才动态显示的 → 因此点完卡片要多等一会。
    // =====================================================================
    private static async Task<bool> HandleGridPickAsync(Node screen, string action, CancellationToken ct)
    {
        await MainThreadRunner.DelayAsync(400, ct);
        List<NGridCardHolder> grid = new();
        for (int i = 0; i < 30; i++)
        {
            grid = UiHelper.FindAll<NGridCardHolder>(screen);
            if (grid.Count > 0) break;
            await MainThreadRunner.DelayAsync(150, ct);
        }
        if (grid.Count == 0) return false;

        var cards = grid.Select(g => TryGetCardFromHolder(g)).ToList();

        // ---------- v2.5.6：多选支持 ----------
        // ⚠ 血泪：事件/遗物里的"移除 2 张/升级 2 张"（Amalgamator / Trial / LuminousChoir /
        //   MorphicGrove / BiiigHug / EmptyCage …）用的都是同一个 NDeckCardSelectScreen，但
        //   CardSelectorPrefs 的 MinSelect/MaxSelect > 1。旧代码**只点一张就点确认** →
        //   CheckIfSelectionComplete 因 count < MinSelect 直接 return（不完成）→ AI 卡死。
        //   实测语义（NDeckCardSelectScreen.OnCardClicked）：
        //     · 每点一张 → _selectedCards 加入该牌；当 count == MaxSelect 时**自动**进预览；
        //     · count 在 [MinSelect, MaxSelect) 时 %Confirm 可用 → 点它进预览（可提前确认）。
        //   故正确流程：按"动作语义"排好优先级 → 依次点 MaxSelect 张（或选到 MinSelect 就够，
        //   择优点满 MinSelect）→ 再走既有的 预览/确认 流程。
        int maxSel = 1, minSel = 1;
        try { ReadSelectRange(screen, out minSel, out maxSel); } catch { }
        int want = Math.Max(1, minSel <= 0 ? 1 : Math.Max(minSel, 1));
        if (maxSel > 1) want = Math.Max(want, Math.Min(minSel > 0 ? minSel : maxSel, maxSel));

        // 排序：得分从高到低（按动作语义），取前 want 张
        var order = RankIndicesForAction(cards, ActionOf(action));

        // 让 LLM 也能参与（若开启大模型）：把本地预评分并入提示词，LLM 最终裁定**首选**
        int primary = order.Count > 0 ? order[0] : 0;
        try
        {
            var me = Pilot.Me;
            var llmPick = Brain.ChooseCardForAction(cards, action, me);
            if (llmPick != null)
            {
                int li = cards.FindIndex(c => ReferenceEquals(c, llmPick));
                if (li >= 0)
                {
                    // LLM 选中的牌置顶，其余保持本地排序
                    order.Remove(li);
                    order.Insert(0, li);
                    primary = li;
                }
            }
        }
        catch (Exception e) { Pilot.Warn($"{action} 大模型裁定失败，用本地评分: {e.Message}"); }

        if (maxSel <= 1)
        {
            // ---------- 单选：老路径（保持不变） ----------
            Pilot.Log($"{action} → {cards[primary]?.Id.Entry ?? "?"}（{DescribePickReason(cards, primary, action)}）");
            grid[primary].EmitSignal(NCardHolder.SignalName.Pressed, grid[primary]);
        }
        else
        {
            // ---------- 多选：依次点 want 张 ----------
            Pilot.Log($"{action} 多选 → 需要 {minSel}~{maxSel} 张，选取 {want} 张");
            int clicked = 0;
            foreach (int ci in order)
            {
                if (clicked >= want) break;
                if (ci < 0 || ci >= grid.Count) continue;
                if (ct.IsCancellationRequested) return true;
                // 已进预览（选满 MaxSelect 会自动进）→ 停止点击
                if (FindActiveConfirm(screen) != null) break;
                Pilot.Log($"  [{clicked + 1}/{want}] {action} → {cards[ci]?.Id.Entry ?? "?"}（{DescribePickReason(cards, ci, action)}）");
                try { grid[ci].EmitSignal(NCardHolder.SignalName.Pressed, grid[ci]); } catch { }
                clicked++;
                // 等选中动画 / 可能的自动进预览
                await MainThreadRunner.DelayAsync(220, ct);
                if (FindActiveConfirm(screen) != null) break;
            }
            if (clicked == 0)
            {
                // 兜底：至少点一张，避免完全不动
                try { grid[primary].EmitSignal(NCardHolder.SignalName.Pressed, grid[primary]); } catch { }
            }
        }

        // 选中后要等：① 高亮动画 ② 主确认键 Enable ③ 两段式预览容器显示
        // 实测这是"没点确定"的关键等待窗口，给到 ~2.4s
        for (int i = 0; i < 16; i++)
        {
            await MainThreadRunner.DelayAsync(150, ct);
            if (FindActiveConfirm(screen) != null) break;
        }

        await ClickConfirmAsync(screen, ct);
        await WaitScreenClosedAsync(screen, ct);
        return true;
    }

    /// <summary>
    /// v2.5.6：从网格选牌屏（<c>NCardGridSelectionScreen</c> 系）的私有字段 <c>_prefs</c>
    /// （<c>CardSelectorPrefs</c>）读出 MinSelect/MaxSelect。读不到时给 (1,1)（退化为单选）。
    /// </summary>
    private static void ReadSelectRange(Node screen, out int minSel, out int maxSel)
    {
        minSel = 1; maxSel = 1;
        try
        {
            object prefs = null;
            for (Type t = screen.GetType(); t != null && prefs == null; t = t.BaseType)
            {
                var f = t.GetField("_prefs",
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Public);
                if (f != null) prefs = f.GetValue(screen);
            }
            if (prefs == null) return;
            var pMin = prefs.GetType().GetProperty("MinSelect");
            var pMax = prefs.GetType().GetProperty("MaxSelect");
            int? mn = pMin?.GetValue(prefs) as int?;
            int? mx = pMax?.GetValue(prefs) as int?;
            if (mn.HasValue && mn.Value > 0) minSel = mn.Value;
            if (mx.HasValue && mx.Value > 0) maxSel = mx.Value;
        }
        catch { }
    }

    /// <summary>
    /// v2.5.6：按动作语义给出"点击优先级"索引序列（越靠前越该被选中）。
    /// 单选时取首位；多选时依次取前 N 位。升级要"收益最大"，移除/变化要"最该滚蛋"。
    /// </summary>
    private static List<int> RankIndicesForAction(List<CardModel> cards, PickAction action)
    {
        var result = new List<int>();
        if (cards == null || cards.Count == 0) return result;
        var me = Pilot.Me;

        try
        {
            if (action == PickAction.Upgrade)
            {
                var pairs = Enumerable.Range(0, cards.Count)
                    .Select(i => (i, v: UpgradeValue(cards, i)))
                    .OrderByDescending(x => x.v).Select(x => x.i);
                result.AddRange(pairs);
            }
            else if (action == PickAction.Remove || action == PickAction.Transform)
            {
                // 诅咒/状态最优先；同档内"最不值钱"（DeckKeepValue 最低）优先
                var pairs = Enumerable.Range(0, cards.Count)
                    .Select(i =>
                    {
                        var p = CardKnowledge.Profile(cards[i]);
                        double curseBonus = 0;
                        if (p != null)
                        {
                            if (p.Has(CardKnowledge.Tag.Curse)) curseBonus = 1000;
                            else if (p.Has(CardKnowledge.Tag.Status)) curseBonus = 800;
                        }
                        return (i, v: curseBonus - DeckKeepValue(cards, i));
                    })
                    .OrderByDescending(x => x.v).Select(x => x.i);
                result.AddRange(pairs);
            }
            else
            {
                // Choose / Enchant：Selector 得分从高到低
                var sc = Selector.ScoreOptions(cards, me);
                result.AddRange(Enumerable.Range(0, sc.Count).OrderByDescending(i => sc[i].Score));
            }
        }
        catch (Exception e)
        {
            Pilot.Warn($"{action} 排序失败，退化按原序: {e.Message}");
        }

        if (result.Count == 0)
            for (int i = 0; i < cards.Count; i++) result.Add(i);
        return result;
    }

    // =====================================================================
    //  选一包牌（"选卡包"）：NChooseABundleSelectionScreen —— 两段式
    //
    //  反编译核对（NChooseABundleSelectionScreen.cs / NCardBundle.cs）：
    //    · 屏幕把多包牌放在 %BundleRow（每包一个 NCardBundle）；
    //    · 点 bundle 的 Hitbox（NClickableControl）→ NCardBundle.OnClicked →
    //        EmitSignal(Clicked, this) → 屏幕 OnBundleClicked：
    //        隐藏 %BundleRow、显示 %BundlePreviewContainer、Enable %Confirm/%Cancel；
    //    · 点 %Confirm（NConfirmButton）→ ConfirmSelection → _completionSource.SetResult(bundle) → 出栈。
    //  故流程：给每包打分 → 点最优包的 Hitbox → 等 %Confirm 可用 → 点它。
    //
    //  评分：一包牌用 Selector 逐张打分后取"均分 + 最高分"的加权（既看整体质量也看核心牌）。
    // =====================================================================
    private static async Task<bool> HandleBundlePickAsync(NChooseABundleSelectionScreen screen, CancellationToken ct)
    {
        await MainThreadRunner.DelayAsync(400, ct);

        // 等 NCardBundle 就位（_Ready 里 AddChildSafely 后还要 Initialize 装牌）
        List<NCardBundle> bundles = new();
        for (int i = 0; i < 30; i++)
        {
            bundles = UiHelper.FindAll<NCardBundle>(screen);
            if (bundles.Count > 0 && bundles.Any(b => b != null && b.Bundle != null && b.Bundle.Count > 0)) break;
            await MainThreadRunner.DelayAsync(150, ct);
        }
        if (bundles.Count == 0) return false;

        // 给每包打分
        var me = Pilot.Me;
        int best = 0;
        double bestScore = double.MinValue;
        var lines = new List<string>();
        for (int bi = 0; bi < bundles.Count; bi++)
        {
            try
            {
                var bundle = bundles[bi];
                if (bundle == null || !GodotObject.IsInstanceValid(bundle)) continue;
                IReadOnlyList<CardModel> cards = bundle.Bundle;
                if (cards == null || cards.Count == 0) continue;
                var sc = Selector.ScoreOptions(cards, me);
                double avg = sc.Count > 0 ? sc.Average(s => s.Score) : 0;
                double top = sc.Count > 0 ? sc.Max(s => s.Score) : 0;
                double s2 = avg * 0.6 + top * 0.4;   // 整体质量 6 : 核心牌 4
                string names = string.Join("/", cards.Select(c => c?.Id.Entry ?? "?"));
                lines.Add($"  包{bi + 1}: 均分{avg:F0} 最高{top:F0} 综合{s2:F1} [{names}]");
                if (s2 > bestScore) { bestScore = s2; best = bi; }
            }
            catch (Exception e) { Pilot.Warn($"卡包 {bi + 1} 评分失败: {e.Message}"); }
        }

        Pilot.Log($"选卡包 → 第 {best + 1} 包（综合 {bestScore:F1}）");
        foreach (var l in lines) Pilot.Log(l);

        // 点最优包的 Hitbox（触发 Clicked → 开预览 + Enable %Confirm）
        try
        {
            var hit = bundles[best].Hitbox;
            if (hit != null && GodotObject.IsInstanceValid(hit))
            {
                await UiHelper.Click(hit);
            }
            else
            {
                Pilot.Warn("选卡包：最优包 Hitbox 无效，退回直接 EmitSignal(Clicked)");
                bundles[best].EmitSignal(NCardBundle.SignalName.Clicked, bundles[best]);
            }
        }
        catch (Exception e)
        {
            Pilot.Warn("选卡包：点击失败，尝试直接发 Clicked 信号: " + e.Message);
            try { bundles[best].EmitSignal(NCardBundle.SignalName.Clicked, bundles[best]); } catch { }
        }

        // 等 %Confirm 可用（OnBundleClicked 里 Enable，且预览卡有 0.5s 飞入动画）
        for (int i = 0; i < 20; i++)
        {
            await MainThreadRunner.DelayAsync(150, ct);
            if (FindActiveConfirm(screen) != null) break;
        }

        // 点确认 → ConfirmSelection → 出栈
        await ClickConfirmAsync(screen, ct);
        await WaitScreenClosedAsync(screen, ct);
        return true;
    }

    // =====================================================================
    //  选遗物
    // =====================================================================
    private static async Task<bool> HandleRelicPickAsync(NChooseARelicSelection screen, CancellationToken ct)
    {
        await MainThreadRunner.DelayAsync(300, ct);
        List<NClickableControl> ctrl = UiHelper.FindAll<NClickableControl>(screen);
        if (ctrl.Count == 0) return false;
        Pilot.Log("选择遗物（默认第一个）");
        await UiHelper.Click(ctrl[0]);
        await WaitScreenClosedAsync(screen, ct);
        return true;
    }

    // =====================================================================
    //  战后奖励列表：拿金币/卡牌/药水，然后继续
    // =====================================================================
    private static async Task<bool> HandleRewardsAsync(MegaCrit.Sts2.Core.Nodes.Screens.NRewardsScreen screen, CancellationToken ct)
    {
        var attempted = new HashSet<MegaCrit.Sts2.Core.Nodes.Rewards.NRewardButton>();
        for (int i = 0; i < 12; i++)
        {
            await Pilot.WaitIfPausedAsync(ct);
            var buttons = UiHelper.FindAll<MegaCrit.Sts2.Core.Nodes.Rewards.NRewardButton>(screen);
            var next = buttons.FirstOrDefault(b => b.IsEnabled && !attempted.Contains(b));
            if (next == null) break;
            attempted.Add(next);

            if (PilotConfig.Current.Verbose)
                Pilot.Log("领取奖励: " + (next.Reward?.GetType().Name ?? "?"));

            await UiHelper.Click(next);
            await MainThreadRunner.DelayAsync(500, ct);

            // 点开卡牌奖励会弹出子界面 → 结束本轮，交给 drain 循环处理
            IOverlayScreen top2 = NOverlayStack.Instance?.Peek();
            if (top2 != null && top2 != screen) return true;
        }

        // ⚠ Bug #3 修复：NRewardsScreen 的前进键是 NProceedButton，**没有 NConfirmButton**
        //  （官方 RewardsScreenHandler.cs:55 核准）。旧代码走 ClickConfirmAsync → 会先在
        //  FindActiveConfirm 上白等满 25×120ms = 3s 才兜底，这就是"战斗后前进卡很久"的主因。
        //  这里改为：先直接点前进键（含"奖励未领完但已无可点奖励"的立即离开），再兜底。
        for (int probe = 0; probe < 20; probe++)
        {
            if (ct.IsCancellationRequested) return true;
            if (!GodotObject.IsInstanceValid(screen) || !IsScreenStillOpen(screen)) return true;

            if (await TryClickProceedAsync(screen, ct))
            {
                await WaitScreenClosedAsync(screen, ct);
                return true;
            }
            // 前进键可能因奖励动画暂未 Enable → 短轮询（每次 150ms，共 ~3s 上限）
            await MainThreadRunner.DelayAsync(150, ct);
        }

        // 最后兜底（极端情况：有确认键的变体）
        await ClickConfirmAsync(screen, ct);
        await WaitScreenClosedAsync(screen, ct);
        return true;
    }

    // =====================================================================
    //  宝箱房：开箱 → 点遗物 → 继续（参照官方 TreasureRoomHandler）
    //
    //  ⚠ NTreasureRoom : Control, IScreenContext, IRoomWithProceedButton —— 同样
    //     不是 IOverlayScreen，不进 NOverlayStack → 必须房间级驱动。
    //  ⚠ v2.5.0 前完全没实现这段，导致"宝箱房只会点继续、从不开箱"。
    //
    //  开箱调用链（反编译核对 NTreasureRoom.cs:225/275/291）：
    //    %Chest (NTreasureButton) --Released--> OnChestButtonReleased → OpenChest()
    //    OpenChest 内部：发金 → DoExtraRewards → InitializeRelics → AnimIn →
    //      （单人）2.5s 后把 Proceed 文案改为"跳过"并 EnableSkipAfterDelay，
    //      之后 Proceed 先被 Disable，等 RelicPickingFinished 才重新 Enable。
    //  遗物选择：单人是 %SingleplayerRelicHolder（NTreasureRoomRelicHolder）的
    //    Released → PickRelic；多人是多个 holder。官方直接找所有 holder 逐个点。
    // =====================================================================
    private static bool _treasureChestOpened;   // 本宝箱房是否已开箱（防重入）

    public static async Task<bool> HandleTreasureRoomAsync(MegaCrit.Sts2.Core.Nodes.Rooms.NTreasureRoom room, CancellationToken ct)
    {
        if (room == null || !GodotObject.IsInstanceValid(room)) return false;

        // ---- ① 开箱：点 %Chest（NTreasureButton）----
        if (!_treasureChestOpened)
        {
            MegaCrit.Sts2.Core.Nodes.TreasureRooms.NTreasureButton chest =
                room.GetNodeOrNull<MegaCrit.Sts2.Core.Nodes.TreasureRooms.NTreasureButton>("%Chest");
            if (chest == null) chest = UiHelper.FindFirst<MegaCrit.Sts2.Core.Nodes.TreasureRooms.NTreasureButton>(room);
            if (chest == null)
            {
                Pilot.Warn("宝箱房：找不到 %Chest 节点，无法开箱");
                return false;
            }
            // 开箱按钮就位判定：NTreasureButton.Setup 会加载骨骼动画，稍等一拍更稳
            for (int i = 0; i < 10 && !GodotObject.IsInstanceValid(chest); i++)
                await MainThreadRunner.DelayAsync(150, ct);

            Pilot.Log("宝箱房：点击开启宝箱");
            await UiHelper.Click(chest, 150);
            _treasureChestOpened = true;

            // 等遗物出现（AnimIn 里 0.2s 延迟 + 0.6s 位移；给足 3s）
            for (int i = 0; i < 20; i++)
            {
                await MainThreadRunner.DelayAsync(150, ct);
                if (FindTreasureHolders(room).Count > 0) break;
            }
        }

        // ---- ② 选遗物：逐个点可见的 NTreasureRoomRelicHolder ----
        // ⚠ 单人也有一个 holder（%SingleplayerRelicHolder），其 Released → PickRelic。
        // ⚠ 空宝箱（currentRelics 为空）时 holder 全部 Visible=false，此处自然跳过。
        var holders = FindTreasureHolders(room);
        foreach (var h in holders)
        {
            if (ct.IsCancellationRequested) return true;
            try
            {
                if (h == null || !GodotObject.IsInstanceValid(h)) continue;
                if (!h.Visible || !h.IsEnabled) continue;
                // 等待 AnimIn 完成（holder.MouseFilter 由 Ignore 变 Stop 才是真正可点）
                for (int i = 0; i < 12; i++)
                {
                    if (h.MouseFilter != Control.MouseFilterEnum.Ignore) break;
                    await MainThreadRunner.DelayAsync(150, ct);
                }
                string relicName = "?";
                try { relicName = h.Relic?.Model?.Id?.Entry ?? "?"; } catch { }
                Pilot.Log($"宝箱房：拾取遗物 {relicName}");
                await UiHelper.Click(h, 200);
                break;   // 单人只需点一次；多人环境下点第一个可见遗物即可推进
            }
            catch (Exception e) { Pilot.Warn("宝箱房：拾取遗物失败: " + e.Message); }
        }

        // ---- ③ 等遗物选择结算完毕（Proceed 重新 Enable）----
        MegaCrit.Sts2.Core.Nodes.CommonUi.NProceedButton pb = null;
        try { pb = room.ProceedButton; } catch { }
        if (pb == null) pb = UiHelper.FindFirst<MegaCrit.Sts2.Core.Nodes.CommonUi.NProceedButton>(room);

        if (pb != null)
        {
            // 单人开箱后有 2.5s 的"跳过"窗口，且 Proceed 会被短暂 Disable；
            // 等它重新可用（最多 ~6s），再点"继续"离开。
            for (int i = 0; i < 40; i++)
            {
                if (ct.IsCancellationRequested) return true;
                try { if (pb.IsEnabled && pb.Visible) break; } catch { break; }
                await MainThreadRunner.DelayAsync(150, ct);
            }
        }
        return true;
    }

    private static List<MegaCrit.Sts2.Core.Nodes.Screens.TreasureRoomRelic.NTreasureRoomRelicHolder> FindTreasureHolders(Node room)
    {
        try { return UiHelper.FindAll<MegaCrit.Sts2.Core.Nodes.Screens.TreasureRoomRelic.NTreasureRoomRelicHolder>(room); }
        catch { return new List<MegaCrit.Sts2.Core.Nodes.Screens.TreasureRoomRelic.NTreasureRoomRelicHolder>(); }
    }

    // =====================================================================
    //  篝火：休息 / 打铁（RoomBrain 决策 → 点击对应 NRestSiteButton）
    //
    //  ⚠ NRestSiteRoom : Control, IScreenContext —— 不是 IOverlayScreen，不进
    //     NOverlayStack，故由 Brain.HandleRoomAsync 房间级调用（本方法 public）。
    // =====================================================================
    private static bool _restHandled;   // 本房间篝火选项是否已点过（防重入）

    public static async Task<bool> HandleRestSiteRoomAsync(MegaCrit.Sts2.Core.Nodes.Rooms.NRestSiteRoom room, CancellationToken ct)
    {
        if (room == null || !GodotObject.IsInstanceValid(room)) return false;

        // 等选项按钮就位。血泪：NRestSiteButton._Ready 里 `Modulate = transparentBlack`，
        // AnimateIn 用 0.5s 才把 modulate 拉回白色，并把 MouseFilter 从 Ignore 改成 Stop。
        // 因此**光看"按钮存在"不够**，必须等到它真正可点（MouseFilter != Ignore）才动手。
        List<MegaCrit.Sts2.Core.Nodes.RestSite.NRestSiteButton> buttons = new();
        for (int i = 0; i < 40; i++)
        {
            buttons = UiHelper.FindAll<MegaCrit.Sts2.Core.Nodes.RestSite.NRestSiteButton>(room);
            if (buttons.Count > 0 && buttons.Any(IsRestButtonClickable)) break;
            await MainThreadRunner.DelayAsync(150, ct);
        }
        if (buttons.Count == 0)
        {
            _restHandled = false;   // 选项消失 = 已选完，准备让位于"继续"
            // 没有可点选项（例如已选完、只剩"继续"）→ 尝试推进
            return await TryProceedRoomAsync(room, ct);
        }

        // 防重：本房间的选项已经点过一次（按钮因动画还在），不再重复点
        if (_restHandled) return false;
        _restHandled = true;

        // 按钮顺序即 Options 顺序（NRestSiteButton.SelectOption 用 Options.IndexOf 定位）
        IReadOnlyList<MegaCrit.Sts2.Core.Entities.RestSite.RestSiteOption> options = null;
        try { options = room.Options; } catch { }
        if (options == null || options.Count == 0)
        {
            options = buttons.Select(b => { try { return b.Option; } catch { return null; } }).Where(o => o != null).ToList();
        }

        var choice = RoomBrain.ChooseRestOption(options, out string why);
        if (choice.Option == null) return await TryProceedRoomAsync(room, ct);

        // 优先按"选项对象 → 按钮"反查（最稳，不依赖顺序），失败再退回索引对齐
        MegaCrit.Sts2.Core.Nodes.RestSite.NRestSiteButton btn = null;
        try { btn = room.GetButtonForOption(choice.Option); } catch { }
        if (btn == null)
        {
            int idx = -1;
            for (int i = 0; i < options.Count; i++) if (ReferenceEquals(options[i], choice.Option)) { idx = i; break; }
            if (idx < 0)
            {
                // 引用不匹配时按 OptionId 兜底匹配
                try
                {
                    string want = choice.Option.OptionId ?? "";
                    idx = options.ToList().FindIndex(o => o != null && (o.OptionId ?? "") == want);
                }
                catch { }
            }
            if (idx < 0) idx = 0;
            btn = idx < buttons.Count ? buttons[idx] : buttons[0];
        }

        Pilot.Log($"篝火 → {RoomBrain.SafeTitle(choice.Option)}（{why}）");

        // 点击前最后确认可点，避免点到仍在淡入、MouseFilter=Ignore 的按钮
        for (int i = 0; i < 12 && !IsRestButtonClickable(btn); i++)
            await MainThreadRunner.DelayAsync(150, ct);

        await UiHelper.Click(btn);

        // 等待选择执行完成：选项列表变化或出现继续按钮
        await WaitRestResolvedAsync(room, ct);
        return true;
    }

    /// <summary>篝火按钮是否真正可点：已启用 + 鼠标可拦 + 淡入到位（modulate 不透明）。</summary>
    private static bool IsRestButtonClickable(MegaCrit.Sts2.Core.Nodes.RestSite.NRestSiteButton b)
    {
        try
        {
            if (b == null || !GodotObject.IsInstanceValid(b)) return false;
            if (!b.Visible || !b.IsEnabled) return false;
            // AnimateIn 期间 MouseFilter=Ignore；变 Stop 说明可点（ForceClick 虽可绕过，但等它结束更稳）
            if (b.MouseFilter == Control.MouseFilterEnum.Ignore) return false;
            return true;
        }
        catch { return false; }
    }

    private static async Task WaitRestResolvedAsync(Node room, CancellationToken ct)
    {
        for (int i = 0; i < 60; i++)
        {
            if (ct.IsCancellationRequested) return;
            if (!GodotObject.IsInstanceValid(room)) return;
            try
            {
                var pb = UiHelper.FindFirst<NProceedButton>(room);
                var btns = UiHelper.FindAll<MegaCrit.Sts2.Core.Nodes.RestSite.NRestSiteButton>(room);
                if ((pb != null && pb.IsEnabled && pb.Visible) || btns.Count == 0) return;
            }
            catch { return; }
            await MainThreadRunner.DelayAsync(150, ct);
        }
    }

    // =====================================================================
    //  商店：买 / 删牌（RoomBrain 决策 → 点击 NMerchantSlot）
    //
    //  注意：NMerchantInventory 实现的是 IScreenContext 而非 IOverlayScreen，
    //  它不会进入 NOverlayStack，所以这里由房间级调用（HandleShopRoomAsync），
    //  不走 HandleTopAsync 的浮层分发。
    //
    //  ⚠ 关键：商店房间必须"买完 → 关库存 → 点 %ProceedButton 离开房间"一次走完，
    //     否则下一轮主循环仍停在 Shop 房间 → 反复开/关商店 → 与地图旅行竞争 →
    //     触发 NCombatRoom.OnCombatSetUp 空引用（黑屏）。
    // =====================================================================
    private static string _handledRoomKey = "";      // 已处理完的商店房间指纹
    private static int _shopBuyRounds;               // 当前商店已购买轮数
    private static RoomType _lastRoomType = RoomType.Unassigned;

    /// <summary>房间类型变化时清空"已处理"状态（由 Brain.HandleRoomAsync 每轮调用）。</summary>
    public static void NotifyRoomChanged(RoomType roomType)
    {
        if (roomType == _lastRoomType) return;
        _lastRoomType = roomType;
        _handledRoomKey = "";
        _shopBuyRounds = 0;
        _lastEventPageKey = "";   // 进入新房间 → 事件页指纹重置
        _restHandled = false;     // 进入新房间 → 篝火防重重置
        _treasureChestOpened = false;  // 进入新房间 → 宝箱开箱防重重置
    }

    public static async Task<bool> HandleShopRoomAsync(MegaCrit.Sts2.Core.Nodes.Rooms.NMerchantRoom room, CancellationToken ct)
    {
        var shop = room?.Inventory;
        if (shop == null) return false;

        // 已处理过这个商店 → 只负责离开，不再重开
        string key = ShopRoomKey(shop);
        bool alreadyHandled = key == _handledRoomKey;

        // 库存未打开 → 先打开（点商人的等价操作）
        if (!shop.IsOpen)
        {
            if (alreadyHandled) return false;   // 已买过且库存已关，交给房间推进兜底
            try
            {
                shop.Open();
                await MainThreadRunner.DelayAsync(600, ct);
            }
            catch (Exception e) { Pilot.Warn("打开商店失败: " + e.Message); return false; }
        }

        if (alreadyHandled && _shopBuyRounds >= 6)
        {
            // 已达到购买轮数上限，确保离开
            await CloseShopAsync(shop, ct);
            await TryProceedRoomAsync(room, ct);
            return true;
        }

        var slots = UiHelper.FindAll<MegaCrit.Sts2.Core.Nodes.Screens.Shops.NMerchantSlot>(shop);
        if (slots.Count == 0) return false;

        // 收集可见/可买条目（排除未上架、已买）
        var items = new List<RoomBrain.ShopItem>();
        var slotOf = new List<MegaCrit.Sts2.Core.Nodes.Screens.Shops.NMerchantSlot>();
        foreach (var s in slots)
        {
            var it = TryBuildShopItem(s, out object _);
            if (it == null) continue;
            items.Add(it); slotOf.Add(s);
        }
        if (items.Count == 0) return false;

        Player me = Pilot.Me;
        bool anyAffordable = false;
        for (int i = 0; i < items.Count; i++)
            if (items[i].Stocked && items[i].Cost <= (me?.Gold ?? 0)) { anyAffordable = true; break; }

        // 没有负担得起的 → 关库存 + 离开房间（一次走完，避免下一轮重进）
        if (!anyAffordable)
        {
            Pilot.Log($"商店：买不起任何东西（金币 {me?.Gold}），离开商店");
            _handledRoomKey = key;
            await CloseShopAsync(shop, ct);
            await TryProceedRoomAsync(room, ct);
            return true;
        }

        var decision = RoomBrain.ChooseShopPurchase(me, items, out string why);
        if (decision.BuyIndex < 0 || decision.BuyIndex >= items.Count)
        {
            Pilot.Log($"商店：买完/不买（{why}），离开商店");
            _handledRoomKey = key;
            await CloseShopAsync(shop, ct);
            await TryProceedRoomAsync(room, ct);
            return true;
        }

        var target = items[decision.BuyIndex];
        Pilot.Log($"商店 → 买「{target.Name}」价{target.Cost}（{why}）");
        _handledRoomKey = key;
        _shopBuyRounds++;

        // ⚠ 不 await 购买（删牌位会弹出选牌浮层，await 会死锁主循环）→ 交给主循环去 drain
        int goldBefore = me?.Gold ?? 0;
        bool fired = ShopPurchaseFire(slotOf[decision.BuyIndex], out string detail);
        if (!fired)
        {
            Pilot.Warn($"商店购买入口调用失败（{detail}），跳过该商品");
            return true;
        }

        // 给购买流程一点时间：若它弹出了选牌浮层，本轮的 DrainScreensAsync 会负责点掉。
        await MainThreadRunner.DelayAsync(500, ct);

        try
        {
            int goldAfter = Pilot.Me?.Gold ?? goldBefore;
            bool overlay = NOverlayStack.Instance?.ScreenCount > 0;
            if (overlay)
                Pilot.Log($"商店：已发起购买（{detail}），出现浮层（选牌等）→ 交主循环处理");
            else if (goldAfter != goldBefore)
                Pilot.Log($"商店：购买成功（{detail}），金币 {goldBefore}→{goldAfter}");
            else
                Pilot.Warn($"商店：购买无金币变化也未弹浮层（{detail}），可能未成交");
        }
        catch { }
        return true;   // 下一轮会再进来买下一件（alreadyHandled=true，不会重开）
    }

    /// <summary>商店房间指纹（用玩家+金币+库存对象构建，用于"本房间已处理"判重）。</summary>
    private static string ShopRoomKey(Node shop)
    {
        try { return shop.GetInstanceId() + ":" + (Pilot.Me?.Gold ?? 0); }
        catch { return shop?.GetInstanceId().ToString() ?? ""; }
    }

    /// <summary>点一次购买。</summary>
    /// <remarks>
    /// ⚠⚠ v2.5.2 血泪（用户反馈「商店购买点击未生效」×6）：
    /// 旧实现反射调 <c>NMerchantSlot.OnSelected()</c> 并 **await 它到底** —— 这是**致命的**。
    /// `OnSelected` → `OnTryPurchase` → 各槽子类 → `MerchantEntry.OnTryPurchaseWrapper(inventory)`，
    /// 而**删牌位**（`MerchantCardRemovalEntry`）内部走
    /// `OneOffSynchronizer.DoLocalMerchantCardRemoval` → `CardSelectCmd.FromDeckForRemoval(player, prefs)`，
    /// 这个 Task **只有在玩家选完一张牌并确认后才会完成**。于是主循环被永久阻塞在 await 上，
    /// `DrainScreensAsync` 永远没机会去处理那个选牌浮层 → 死锁，直到看门狗重启主循环。
    /// 日志证据：`商店购买点击未生效，结束本轮` 连续 6 次（`_shopBuyRounds` 触顶）。
    ///
    /// ✅ 正确做法：**发起购买但不等待其完成**（fire-and-forget），把控制权立刻还给主循环，
    /// 由 `DrainScreensAsync` 去点掉随之弹出的选牌浮层；购买结果通过"金币变化 / 库存变化"事后核验。
    /// </remarks>
    private static bool ShopPurchaseFire(NMerchantSlot slot, out string detail)
    {
        detail = "";
        try
        {
            // 优先用公开的 Entry（MerchantEntry）→ OnTryPurchaseWrapper，这是官方的购买入口，
            // 参数就是库存本身；比反射私有 OnSelected 稳定得多（不依赖方法名/可见性）。
            var entry = slot.Entry;
            if (entry != null)
            {
                var m = entry.GetType().GetMethod("OnTryPurchaseWrapper",
                    System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.NonPublic,
                    null,
                    new[] { typeof(MerchantInventory), typeof(bool) },
                    null);
                if (m == null)
                {
                    // 删牌位的重载多一个 cancelable 参数，用宽松匹配
                    foreach (var cand in entry.GetType().GetMethods(
                        System.Reflection.BindingFlags.Instance |
                        System.Reflection.BindingFlags.Public |
                        System.Reflection.BindingFlags.NonPublic))
                    {
                        if (cand.Name != "OnTryPurchaseWrapper") continue;
                        m = cand; break;
                    }
                }
                if (m != null)
                {
                    var ps = m.GetParameters();
                    object[] args = new object[ps.Length];
                    for (int i = 0; i < ps.Length; i++)
                    {
                        if (ps[i].ParameterType == typeof(MerchantInventory)) args[i] = _shopInventoryOf(slot);
                        else if (ps[i].ParameterType == typeof(bool)) args[i] = (object)(i == 0);   // ignoreCost=false / cancelable=true
                        else args[i] = ps[i].HasDefaultValue ? ps[i].DefaultValue : null;
                    }
                    m.Invoke(entry, args);
                    detail = $"entry.{m.Name}({ps.Length}参)";
                    return true;
                }
                detail = "未找到 OnTryPurchaseWrapper";
            }

            // 兜底：仍走 UI 语义 —— 点 Hitbox（等价鼠标左键释放）。
            // ⚠ 必须让 _isHovered 为真，故先手动触发 OnFocus 语义：这里用 EmitSignal 更可靠地
            //   模拟"先悬停再松开"，否则 OnMouseReleased 里的 `_isHovered` 判定会直接吞掉点击。
            var hit = slot.Hitbox;
            if (hit != null)
            {
                hit.EmitSignal(Control.SignalName.MouseEntered);
                hit.EmitSignal(NClickableControl.SignalName.MouseReleased, new InputEventMouseButton
                {
                    ButtonIndex = MouseButton.Left,
                    Pressed = false,
                });
                detail = "hitbox 模拟鼠标释放";
                return true;
            }

            // 最后兜底：反射私有 OnSelected（不 await）
            var mSel = slot.GetType().GetMethod("OnSelected",
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Public);
            if (mSel != null) { mSel.Invoke(slot, null); detail = "OnSelected(不等待)"; return true; }

            detail = "无可用购买入口";
            return false;
        }
        catch (Exception e)
        {
            detail = e.InnerException?.Message ?? e.Message;
            return false;
        }
    }

    /// <summary>从槽节点取它所属的库存（反射读私有 `_merchantRug`）。</summary>
    private static MerchantInventory _shopInventoryOf(NMerchantSlot slot)
    {
        try
        {
            var f = typeof(NMerchantSlot).GetField("_merchantRug",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var rug = f?.GetValue(slot) as MegaCrit.Sts2.Core.Nodes.Screens.Shops.NMerchantInventory;
            return rug?.Inventory;
        }
        catch { return null; }
    }

    /// <summary>离开商店：点 %BackButton（Close 是私有的，只能走 UI）。</summary>
    private static async Task CloseShopAsync(Node shop, CancellationToken ct)
    {
        try
        {
            var back = shop.GetNodeOrNull<MegaCrit.Sts2.Core.Nodes.CommonUi.NBackButton>("%BackButton");
            if (back != null) { await UiHelper.Click(back); await MainThreadRunner.DelayAsync(600, ct); return; }
        }
        catch (Exception e) { Pilot.Warn("关闭商店失败: " + e.Message); }
    }

    /// <summary>把 NMerchantSlot 转成 RoomBrain.ShopItem（反射兜底，兼容不同字段名）。</summary>
    private static RoomBrain.ShopItem TryBuildShopItem(Node slot, out object entry)
    {
        entry = null;
        try
        {
            // 条目：优先走抽象属性 Entry
            var pEntry = slot.GetType().GetProperty("Entry");
            entry = pEntry?.GetValue(slot);
            if (entry == null) return null;

            var item = new RoomBrain.ShopItem();
            item.Name = "?";
            item.Stocked = true;

            // 成本 / 上架状态
            var pCost = entry.GetType().GetProperty("Cost");
            if (pCost != null) item.Cost = Convert.ToInt32(pCost.GetValue(entry) ?? 0);
            var pStock = entry.GetType().GetProperty("IsStocked");
            if (pStock != null) item.Stocked = (bool)(pStock.GetValue(entry) ?? true);

            // 名称
            foreach (string n in new[] { "Card", "Relic", "Potion", "Name", "Title" })
            {
                var p = entry.GetType().GetProperty(n);
                if (p == null) continue;
                object v = p.GetValue(entry);
                if (v is CardModel cm) { item.Card = cm; item.Name = Brain.SafeTitle(cm); }
                else if (v != null)
                {
                    // LocString / string
                    var raw = v.GetType().GetMethod("GetRawText");
                    item.Name = raw != null ? (raw.Invoke(v, null) as string ?? v.ToString()) : v.ToString();
                }
                if (!string.IsNullOrEmpty(item.Name) && item.Name != "?") break;
            }

            // 条目类型推断
            string tn = slot.GetType().Name;
            if (tn.Contains("CardRemoval")) item.Kind = RoomBrain.ShopKind.Removal;
            else if (tn.Contains("Relic")) item.Kind = RoomBrain.ShopKind.Relic;
            else if (tn.Contains("Potion")) item.Kind = RoomBrain.ShopKind.Potion;
            else if (tn.Contains("Card")) item.Kind = RoomBrain.ShopKind.Card;
            else item.Kind = RoomBrain.ShopKind.Other;

            if (item.Cost <= 0 && item.Stocked) item.Stocked = false; // 无价=不可买
            return item;
        }
        catch { return null; }
    }

    // =====================================================================
    //  事件房：选一个选项
    //
    //  ⚠ NEventRoom : Control, IScreenContext —— 不是 IOverlayScreen，不进
    //     NOverlayStack，故由 Brain.HandleRoomAsync 房间级调用（本方法 public）。
    //
    //  事件常常有多页（选完一页 → 出新一页）。主循环每轮都会进这里，
    //  所以用"页面指纹"防重：同一组选项文字只点一次，等页面变了再点下一次。
    // =====================================================================
    private static string _lastEventPageKey = "";

    public static async Task<bool> HandleEventRoomAsync(MegaCrit.Sts2.Core.Nodes.Rooms.NEventRoom room, CancellationToken ct)
    {
        if (room == null || !GodotObject.IsInstanceValid(room)) return false;
        await MainThreadRunner.DelayAsync(300, ct);
        List<NEventOptionButton> opts = new();
        for (int i = 0; i < 20; i++)
        {
            opts = UiHelper.FindAll<NEventOptionButton>(room);
            if (opts.Count > 0) break;
            await MainThreadRunner.DelayAsync(150, ct);
        }
        if (opts.Count == 0) return false;   // 布局选项由 FightAsync / Layout 处理

        // 页面指纹 = 各选项标题拼接（同一页只处理一次）
        string pageKey;
        try
        {
            pageKey = string.Join("|", opts.Select(b =>
            {
                try { return b.Option?.Title?.GetFormattedText() ?? "?"; } catch { return "?"; }
            }));
        }
        catch { pageKey = opts.Count.ToString(); }
        if (pageKey == _lastEventPageKey) return false;   // 本页已处理，等它翻页
        _lastEventPageKey = pageKey;

        return await PickEventOptionAsync(room, opts, ct);
    }

    /// <summary>房间切换时重置事件页面指纹。</summary>
    public static void ResetEventPage() => _lastEventPageKey = "";

    /// <summary>在事件选项按钮里决策并点击。公开给 Brain.HandleRoomAsync 复用。</summary>
    public static async Task<bool> PickEventOptionAsync(Node room, List<NEventOptionButton> buttons, CancellationToken ct)
    {
        var choices = new List<RoomBrain.EventChoice>();
        foreach (var b in buttons)
        {
            var c = new RoomBrain.EventChoice();
            try
            {
                var opt = b.Option;
                c.Title = opt.Title?.GetFormattedText() ?? "";
                c.Description = opt.Description?.GetFormattedText() ?? "";
                c.IsLocked = opt.IsLocked;
                c.IsProceed = opt.IsProceed;
                c.WillKillPlayer = opt.WillKillPlayer?.Invoke(null) ?? false;
                // spire-codex 权威数据锚点：事件 id（EventModel.Id.Entry）+ 选项 id（EventOption.TextKey）
                try { c.EventId = b.Event?.Id?.Entry; } catch { }
                try { c.OptionId = opt.TextKey; } catch { }
            }
            catch { c.Title = "?"; }
            choices.Add(c);
        }

        int idx = RoomBrain.ChooseEventOption(Pilot.Me, choices, out string why);
        if (idx < 0 || idx >= buttons.Count) return false;

        Pilot.Log($"事件 → {choices[idx].Title}（{why}）");
        await UiHelper.Click(buttons[idx]);
        await MainThreadRunner.DelayAsync(400, ct);
        return true;
    }

    // =====================================================================
    //  通用工具
    // =====================================================================
    private static async Task<bool> TryProceedRoomAsync(Node room, CancellationToken ct)
    {
        try
        {
            NProceedButton pb = UiHelper.FindFirst<NProceedButton>(room);
            if (pb != null && pb.IsEnabled && pb.Visible)
            {
                await UiHelper.Click(pb);
                await MainThreadRunner.DelayAsync(400, ct);
                return true;
            }
        }
        catch { }
        return false;
    }

    private static async Task<bool> HandleProceedOnlyAsync(Node screen, string path, CancellationToken ct)
    {
        await MainThreadRunner.DelayAsync(1000, ct);
        NProceedButton btn = screen.GetNodeOrNull<NProceedButton>(path);
        if (btn?.IsEnabled ?? false)
        {
            await UiHelper.Click(btn);
            return true;
        }
        return false;
    }

    private static async Task<bool> HandleGameOverAsync(MegaCrit.Sts2.Core.Nodes.Screens.GameOverScreen.NGameOverScreen screen, CancellationToken ct)
    {
        Pilot.Log("游戏结束界面，停止 AI");
        Pilot.Stop();
        await MainThreadRunner.DelayAsync(200, ct);
        return false; // 交还玩家自行处理
    }

    private static async Task ClickIfPresentAsync<T>(Node parent, CancellationToken ct) where T : NClickableControl
    {
        try
        {
            T btn = UiHelper.FindFirst<T>(parent);
            if (btn != null && btn.IsEnabled && btn.Visible)
            {
                await UiHelper.Click(btn);
                await MainThreadRunner.DelayAsync(250, ct);
            }
        }
        catch { }
    }

    // =====================================================================
    //  「确定」按钮：选牌界面的确认流程（v2.4.0 全量核对反编译后的正确实现）
    //
    //  ⚠ 三层血泪教训：
    //   1) NConfirmButton 与 NProceedButton 都是 NButton 的**直接子类，互不继承**
    //      （核对 NConfirmButton.cs:20 / NProceedButton.cs:18）。选牌确认键是
    //      NConfirmButton，旧代码找 NProceedButton 永远匹配不到。
    //   2) 大多数选牌屏是**两段式**：主「确认」只是打开**预览浮层**，真正的完成键
    //      在预览层里。只点主确认 → 停在预览页 = 用户看到的"选完了没确定"。
    //   3) 各屏的节点命名**不统一**（已逐个核对 _Ready）：
    //
    //   屏                                     主确认键位置                预览确认键位置
    //   ─────────────────────────────────────  ─────────────────────────  ────────────────────────
    //   NDeckUpgradeSelectScreen               "Confirm"                   "%UpgradeSinglePreviewContainer"/%UpgradeMultiPreviewContainer → "Confirm"
    //   NDeckEnchantSelectScreen               "Confirm"                   同上（Enchant* 容器）
    //   NDeckCardSelectScreen                  "%Confirm"(→Preview)         "%PreviewConfirm"(→完成)
    //   NDeckTransformSelectScreen             "Confirm"(→Preview)          "%PreviewContainer"/"Confirm"(→完成)
    //   NSimpleCardSelectScreen                "%Confirm"(直接完成)          无
    //   NCombatPileCardSelectScreen            "%Confirm"(直接完成)          无
    //   NChooseABundleSelectionScreen          "%Confirm"                  "%BundlePreviewContainer" → "Confirm"
    //
    //  策略（名字优先 + 全屏兜底，对单段/两段都安全）：
    //   a. 全屏找 `NConfirmButton`，**排除位于未显示容器内的**，取"当前真正露在外面"的
    //      最上层一个来点 —— 这样两段式第一段点主键、进入预览后第二段自然点到预览键，
    //      无需按屏写死路径；
    //   b. 点完若界面仍在，重复一轮（覆盖两段式）；
    //   c. 若本屏**没有** NConfirmButton（如 NRewardsScreen 的前进键是 NProceedButton），
    //      或反射完成方法也不适用 → 兜底点 `NProceedButton`（"继续/前进"键）。⚠ v2.4.1 回归：
    //      官方 RewardsScreenHandler.cs:55 确认奖励屏前进用的是 NProceedButton，v2.4.0 把它
    //      错换成只找 NConfirmButton → 找不到 → 战斗结束卡在奖励屏无法前进。
    //   d. 仍在则兜底直接调 ConfirmSelection / CompleteSelection 反射终点方法。
    // =====================================================================
    private static async Task ClickConfirmAsync(Node parent, CancellationToken ct)
    {
        for (int round = 0; round < 4; round++)
        {
            if (ct.IsCancellationRequested) return;
            if (!GodotObject.IsInstanceValid(parent) || !IsScreenStillOpen(parent)) return;

            // 等"有按钮可用"最多 ~3s（两段式预览层是延迟建出来的）
            NConfirmButton target = null;
            for (int i = 0; i < 25; i++)
            {
                if (ct.IsCancellationRequested) return;
                if (!GodotObject.IsInstanceValid(parent)) return;
                target = FindActiveConfirm(parent);
                if (target != null) break;
                if (!IsScreenStillOpen(parent)) return;
                await MainThreadRunner.DelayAsync(120, ct);
            }

            if (target == null)
            {
                // 没有可用确认键 → 先试"前进/继续"键（奖励屏等），再试反射完成方法
                if (await TryClickProceedAsync(parent, ct)) continue;
                if (TryInvokeCompletion(parent)) return;
                break;
            }

            await UiHelper.Click(target);
            await MainThreadRunner.DelayAsync(350, ct);
        }

        // 界面还在 → 最后兜底：前进键 / 反射完成方法
        if (GodotObject.IsInstanceValid(parent) && IsScreenStillOpen(parent))
        {
            if (!await TryClickProceedAsync(parent, ct))
                TryInvokeCompletion(parent);
        }
    }

    /// <summary>
    /// 兜底点"前进/继续"键（`NProceedButton`）。用于没有 NConfirmButton 的屏
    /// （典型：NRewardsScreen 战斗奖励屏 —— 官方 RewardsScreenHandler 就是点它）。
    /// 同样做祖先可见性判断，避免点到隐藏的。返回是否点到了。
    /// </summary>
    private static async Task<bool> TryClickProceedAsync(Node parent, CancellationToken ct)
    {
        try
        {
            NProceedButton pb = null;
            foreach (var p in UiHelper.FindAll<NProceedButton>(parent))
            {
                if (p == null || !GodotObject.IsInstanceValid(p)) continue;
                if (!p.Visible || !p.IsEnabled) continue;
                if (!IsEffectivelyVisible(p)) continue;
                pb = p; break;
            }
            if (pb == null) return false;
            await UiHelper.Click(pb);
            await MainThreadRunner.DelayAsync(400, ct);
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// 找出"当前真正可用"的确认键。同名节点在未显示的预览容器里也存在，
    /// 故必须做**祖先可见性**判断，取最先命中的那个（深度优先 = 外层优先）。
    /// </summary>
    private static NConfirmButton FindActiveConfirm(Node parent)
    {
        try
        {
            foreach (var c in UiHelper.FindAll<NConfirmButton>(parent))
            {
                if (c == null || !GodotObject.IsInstanceValid(c)) continue;
                if (!c.Visible || !c.IsEnabled) continue;
                if (!IsEffectivelyVisible(c)) continue;   // 排除藏在未显示预览容器里的
                return c;
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// 兜底：直接反射调用该屏的完成方法（CompleteSelection / ConfirmSelection / CheckIfSelectionComplete）。
    /// 这些方法内部会 `_completionSource.SetResult(...)` + 出栈，等价于玩家点了确认键。
    /// </summary>
    private static bool TryInvokeCompletion(Node parent)
    {
        foreach (Node n in EnumerateSelfAndDescendants(parent))
        {
            if (n == null) continue;
            Type t = n.GetType();
            foreach (string m in new[] { "ConfirmSelection", "CompleteSelection", "CheckIfSelectionComplete" })
            {
                try
                {
                    var mi = t.GetMethod(m,
                        System.Reflection.BindingFlags.Instance |
                        System.Reflection.BindingFlags.NonPublic |
                        System.Reflection.BindingFlags.Public);
                    if (mi == null) continue;
                    var ps = mi.GetParameters();
                    // 只调无参或单参(NButton)版本
                    if (ps.Length == 0) { mi.Invoke(n, null); return true; }
                    if (ps.Length == 1 && typeof(NButton).IsAssignableFrom(ps[0].ParameterType))
                    { mi.Invoke(n, new object[] { null }); return true; }
                }
                catch { /* 继续试下一个 */ }
            }
        }
        return false;
    }

    private static IEnumerable<Node> EnumerateSelfAndDescendants(Node node)
    {
        if (node == null) yield break;
        yield return node;
        List<Node> children;
        try { children = node.GetChildren().ToList(); }
        catch { yield break; }
        foreach (Node c in children)
            foreach (Node x in EnumerateSelfAndDescendants(c))
                yield return x;
    }

    /// <summary>按绝对/相对/唯一名路径查找节点。</summary>
    private static Node FindByPath(Node root, string path)
    {
        try
        {
            if (root == null) return null;
            var n = root.GetNodeOrNull(path);
            if (n != null) return n;
            return FindByNameRecursive(root, path.TrimStart('%'));
        }
        catch { return null; }
    }

    private static Node FindByNameRecursive(Node node, string name)
    {
        try
        {
            if (node == null) return null;
            if (node.Name == name) return node;
            foreach (Node child in node.GetChildren())
            {
                var found = FindByNameRecursive(child, name);
                if (found != null) return found;
            }
        }
        catch { }
        return null;
    }

    /// <summary>节点自身及其所有祖先都可见，才算"真正显示"。</summary>
    private static bool IsEffectivelyVisible(Node n)
    {
        try
        {
            if (n == null) return false;
            if (n is CanvasItem ci && !ci.Visible) return false;
            Node p = n.GetParent();
            int guard = 0;
            while (p != null && guard++ < 40)
            {
                if (p is CanvasItem pci && !pci.Visible) return false;
                p = p.GetParent();
            }
            return true;
        }
        catch { return false; }
    }

    /// <summary>界面是否仍开着（不再位于浮层栈顶）。</summary>
    private static bool IsScreenStillOpen(Node screen)
    {
        try
        {
            if (!GodotObject.IsInstanceValid(screen)) return false;
            if (screen is IOverlayScreen)
            {
                IOverlayScreen top = NOverlayStack.Instance?.Peek();
                return top != null && ReferenceEquals(top, screen);
            }
            return screen.IsInsideTree();
        }
        catch { return false; }
    }

    private static async Task WaitScreenClosedAsync(Node screen, CancellationToken ct)
    {
        for (int i = 0; i < 100; i++)
        {
            if (ct.IsCancellationRequested) return;
            if (!GodotObject.IsInstanceValid(screen)) return;
            IOverlayScreen top = NOverlayStack.Instance?.Peek();
            if (top == null || !ReferenceEquals(top, screen)) return;
            await MainThreadRunner.DelayAsync(100, ct);
        }
    }

    // =====================================================================
    //  地图：在浮层之外单独处理（地图不是 overlay）
    // =====================================================================
    /// <summary>如果地图界面开着，让 Brain 选一个节点并点击。返回 true 表示已走了一步。</summary>
    public static async Task<bool> TryAdvanceMapAsync(CancellationToken ct)
    {
        try
        {
            NMapScreen map = NMapScreen.Instance;
            if (map == null || !map.IsOpen) return false;

            List<NMapPoint> pts = UiHelper.FindAll<NMapPoint>(map);
            if (pts.Count == 0) return false;

            // 只考虑可点击的节点
            List<NMapPoint> enabled = pts.Where(p => p.IsEnabled).ToList();
            if (enabled.Count == 0) return false;

            // 解析“下一层可达”的候选
            RunState rs = Pilot.RunStateNow;
            List<MapPoint> candidates = new();
            if (rs != null && rs.VisitedMapCoords != null && rs.VisitedMapCoords.Count > 0)
            {
                MapCoord last = rs.VisitedMapCoords[^1];
                NMapPoint cur = pts.FirstOrDefault(p => p.Point.coord.Equals(last));
                if (cur?.Point.Children != null)
                {
                    foreach (MapPoint ch in cur.Point.Children) candidates.Add(ch);
                }
            }

            // 兜底：用所有可点节点
            List<NMapPoint> enabledCandidates = candidates.Count > 0
                ? enabled.Where(p => candidates.Any(c => c.coord.Equals(p.Point.coord))).ToList()
                : enabled;
            if (enabledCandidates.Count == 0) enabledCandidates = enabled;

            MapPoint chosen;
            string why;
            if (candidates.Count > 0 && enabledCandidates.Count == candidates.Count)
            {
                chosen = Brain.ChooseMapPoint(candidates, out why);
            }
            else
            {
                chosen = enabledCandidates[0].Point;
                why = "唯一可达";
            }

            NMapPoint targetNode = enabledCandidates.FirstOrDefault(p => p.Point.coord.Equals(chosen.coord))
                                   ?? enabledCandidates[0];
            Pilot.Log($"选路 → {targetNode.Point.PointType}（{why}）");
            await UiHelper.Click(targetNode);
            await MainThreadRunner.DelayAsync(600, ct);
            return true;
        }
        catch (Exception e)
        {
            Pilot.Warn("地图处理失败: " + e.Message);
            return false;
        }
    }
}
