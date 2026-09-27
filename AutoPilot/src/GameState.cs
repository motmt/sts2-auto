using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;

namespace AutoPilot;

/// <summary>
/// 局面序列化器：把游戏状态压成一段紧凑、给大模型看的文本。
///
/// 关键原则（v2.1 修正）：
///   · 只给**决策必需的字段**：手牌（真实费用/类型/伤害/格挡）、能量、血量/格挡、敌人（含意图伤害）、
///     遗物、药水、金币，以及可选项（卡牌奖励/地图候选）。
///   · **费用与数值必须是当前局面的真实值**（含力量/敏捷/虚弱/易伤等修正）——这是 v2.0 的致命缺陷：
///     当时用反射读 BaseDamage/Damage，读不到就回落成常量（攻击 6 / 技能 0），导致模型看不到费用与数值，
///     等于蒙眼打牌（「乱打、不考虑费用与排序」）。
///   · 绝不调用会抛异常的 <c>LocString.GetFormattedText()</c>（卡面含 {Damage:diff()} 等运行期变量时会炸）。
///     一律用 <see cref="CardModel.GetDescriptionForPile"/> 取已求值的描述，并包 try/catch。
/// </summary>
public static class GameState
{
    // =====================================================================
    //  战斗局面
    // =====================================================================
    /// <summary>生成战斗局面的紧凑文本（不含任何作弊信息，全部是玩家可见状态）。</summary>
    public static string DescribeCombat(Player me, IReadOnlyList<CardModel> hand)
    {
        var sb = new StringBuilder();
        try
        {
            int hp = me.Creature?.CurrentHp ?? 0;
            int maxHp = me.Creature?.MaxHp ?? 0;
            int block = me.Creature?.Block ?? 0;
            int energy = me.PlayerCombatState?.Energy ?? 0;
            int turn = me.PlayerCombatState?.TurnNumber ?? 0;

            var enemies = HittableEnemies(me);
            int incoming = enemies.Sum(IntentDamage);

            sb.AppendLine("【我方】");
            sb.AppendLine($"  角色: {CharName(me)}  回合: {turn}  能量: {energy}");
            sb.AppendLine($"  生命: {hp}/{maxHp}  格挡: {block}  金币: {me.Gold}");
            sb.AppendLine($"  本回合预计承伤: {incoming}  → 需额外格挡: {Math.Max(0, incoming - block)}");
            string powers = DescribePowers(me.Creature);
            if (!string.IsNullOrEmpty(powers)) sb.AppendLine($"  状态: {powers}");

            var relics = SafeRelics(me);
            if (relics.Count > 0)
                sb.AppendLine($"  遗物({relics.Count}): " + string.Join("、", relics.Take(20)));

            var potions = SafePotions(me);
            sb.AppendLine("  药水: " + (potions.Count == 0 ? "(无)" : string.Join("、", potions)));

            sb.AppendLine("【敌人】");
            if (enemies.Count == 0) sb.AppendLine("  (无)");
            for (int i = 0; i < enemies.Count; i++)
            {
                Creature e = enemies[i];
                string intent = DescribeIntent(e);
                string ep = DescribePowers(e);
                sb.AppendLine($"  #{i + 1} {e.Name}  HP {e.CurrentHp}/{e.MaxHp}  格挡 {e.Block}" +
                              (string.IsNullOrEmpty(intent) ? "" : $"  意图: {intent}") +
                              (string.IsNullOrEmpty(ep) ? "" : $"  [{ep}]"));
            }
            // 权威怪物情报（spire-codex）：给出本场敌人的完整招式表与进阶血量，
            // 供大模型判断"它接下来可能出什么"以及精确的伤害上限，锚定意图 API 读不到的信息。
            try
            {
                var mIds = enemies.Select(e => { try { return e.Monster?.Id?.Entry; } catch { return null; } })
                                  .Where(s => !string.IsNullOrEmpty(s)).ToList();
                string codexEnemies = Codex.EnemiesPrompt(mIds);
                if (!string.IsNullOrEmpty(codexEnemies)) sb.Append(codexEnemies);
            }
            catch { }

            sb.AppendLine("【手牌】");
            if (hand == null || hand.Count == 0) sb.AppendLine("  (无)");
            else
            {
                for (int i = 0; i < hand.Count; i++)
                {
                    CardModel c = hand[i];
                    int cost = SafeCost(c);
                    bool playable = SafeCanPlay(c);
                    sb.AppendLine($"  {i + 1}. {Brain.SafeTitle(c)} [费用{cost}] {CardStats(c, me)}" +
                                  (playable ? "" : " （当前不可打出）") +
                                  $"  <{c.Id.Entry}>");
                }
            }

            sb.AppendLine("【牌堆】");
            sb.AppendLine($"  抽牌堆: {PileCount(me, PileType.Draw)}  弃牌堆: {PileCount(me, PileType.Discard)}  消耗堆: {PileCount(me, PileType.Exhaust)}");
        }
        catch (Exception e)
        {
            Pilot.Warn("序列化战斗局面失败: " + e.Message);
        }
        return sb.ToString();
    }

    /// <summary>牌组全貌（供选卡决策用）。</summary>
    public static string DescribeDeck(Player me, int max = 60)
    {
        try
        {
            var deck = me?.Deck?.Cards;
            if (deck == null || deck.Count == 0) return "(空)";
            var grouped = deck
                .GroupBy(c => Brain.SafeTitle(c))
                .OrderByDescending(g => g.Count())
                .Select(g => g.Count() > 1 ? $"{g.Key}×{g.Count()}" : g.Key);
            return string.Join("、", grouped.Take(max));
        }
        catch { return "(读取失败)"; }
    }

    // =====================================================================
    //  可选项序列化
    // =====================================================================
    /// <summary>卡牌奖励三选一（含真实数值与描述）。</summary>
    public static string DescribeCardOptions(IReadOnlyList<CardModel> options)
    {
        var sb = new StringBuilder();
        try
        {
            Player me = Pilot.Me;
            for (int i = 0; i < options.Count; i++)
            {
                CardModel c = options[i];
                int cost = SafeCost(c);
                sb.AppendLine($"  {i + 1}. {Brain.SafeTitle(c)} [费用{cost}] {TypeCn(c.Type)}·{RarityCn(c.Rarity)} " +
                              $"{CardStats(c, me)}  <{c.Id.Entry}>");
                string desc = SafeDesc(c);
                if (!string.IsNullOrEmpty(desc)) sb.AppendLine($"     描述: {desc}");
            }
        }
        catch (Exception e)
        {
            Pilot.Warn("序列化卡牌选项失败（已忽略）: " + e.Message);
        }
        return sb.ToString();
    }

    /// <summary>
    /// 地图候选节点。v2.3.6：不再只列类型名，而是带上该节点的**战略价值判断上下文**
    /// （类型 + 威胁/收益定位 + 与当前血量金币的关系），让 LLM 能做真正的路线规划，
    /// 而不是只看一个类型名瞎猜。
    /// </summary>
    public static string DescribeMapOptions(IReadOnlyList<MegaCrit.Sts2.Core.Map.MapPoint> candidates, int floor, int act)
    {
        var sb = new StringBuilder();
        try
        {
            Player me = Pilot.Me;
            int hp = me?.Creature?.CurrentHp ?? 0;
            int maxHp = me?.Creature?.MaxHp ?? 1;
            double hpRatio = maxHp > 0 ? (double)hp / maxHp : 1;
            int gold = me?.Gold ?? 0;
            int deckSize = 0;
            try { deckSize = me?.Deck?.Cards?.Count ?? 0; } catch { }
            var rep = Evaluator.AnalyzeDeck(me);

            sb.AppendLine($"  当前: 第 {floor} 层 · 第 {act} 幕   " +
                          $"生命 {hp}/{maxHp}({hpRatio * 100:F0}%)   金币 {gold}   牌组 {deckSize} 张" +
                          $"（诅咒{rep.Curses}/打击类{rep.StrikeLike}/防御类{rep.DefendLike}）");
            for (int i = 0; i < candidates.Count; i++)
            {
                var p = candidates[i];
                sb.AppendLine($"  {i + 1}. {PointCn(p.PointType)} —— {RouteHint(p.PointType, hpRatio, gold, floor, act)}");
            }
        }        catch (Exception e)
        {
            Pilot.Warn("序列化地图选项失败（已忽略）: " + e.Message);
        }
        return sb.ToString();
    }

    /// <summary>给单个节点类型生成"结合当前状态"的一句话定位，供 LLM 理解取舍。</summary>
    private static string RouteHint(MegaCrit.Sts2.Core.Map.MapPointType t, double hpRatio, int gold, int floor, int act)
    {
        bool low = hpRatio * 100 < 55;
        bool veryLow = hpRatio * 100 < 35;
        return t switch
        {
            MegaCrit.Sts2.Core.Map.MapPointType.Elite =>
                veryLow ? "精英（危险，残血强烈建议避开）"
                        : low ? "精英（有风险，会掉不少血，但给遗物）"
                              : "精英（状态尚可，可博遗物）",
            MegaCrit.Sts2.Core.Map.MapPointType.Monster =>
                floor < 8 ? "普通战斗（前期，拿卡成长性价比高）"
                          : "普通战斗（收益一般，注意战损）",
            MegaCrit.Sts2.Core.Map.MapPointType.Boss => "首领（本幕终点，必有）",
            MegaCrit.Sts2.Core.Map.MapPointType.RestSite =>
                low ? "休息点（残血，强烈建议去回血）"
                    : "休息点（可回血或升级）",
            MegaCrit.Sts2.Core.Map.MapPointType.Shop =>
                gold < 120 ? $"商店（金币{gold}偏少，价值有限）"
                           : $"商店（金币{gold}充足，可买遗物/删牌）",
            MegaCrit.Sts2.Core.Map.MapPointType.Treasure => "宝箱（免费遗物，稳赚）",
            MegaCrit.Sts2.Core.Map.MapPointType.Unknown => "事件/未知（随机：可能回血/得卡/得钱，也可能掉血）",
            MegaCrit.Sts2.Core.Map.MapPointType.Ancient => "远古（强力遗物，通常伴随代价/挑战）",
            _ => t.ToString(),
        };
    }

    // =====================================================================
    //  辅助：数值读取（关键——必须读到真实值）
    // =====================================================================
    public static List<Creature> HittableEnemies(Player me)
    {
        try
        {
            ICombatState cs = me?.Creature?.CombatState;
            if (cs == null) return new List<Creature>();
            return cs.Enemies.Where(e => e.IsAlive).ToList();
        }
        catch { return new List<Creature>(); }
    }

    /// <summary>敌人意图的可读描述：用 GetTotalDamage(targets, owner) 精确计算（近战+多段）。</summary>
    /// <summary>
    /// v2.5.5：**逐回合威胁时间线** —— 把「我方血量 + 我方格挡 + 每个敌人本回合意图 + 每个敌人
    /// 接下来的可能招式与最大伤害」整合成一张表，并把**致死判定**写成显式结论。
    ///
    /// 用户反馈「不看血量 11 层暴毙」「没能控制血量，不能只看眼前状态，要看好敌人每一回合状态」
    /// —— 根因：旧提示词里 `预计承伤` 只是**当回合**的一个数字，且"会不会死"要靠模型自己算
    /// （规则 E 是一句软性散文，实测被模型直接忽略：血量 10、承伤 20、明明会死，仍打了
    /// `完美打击 → 打击` 两把输出牌）。本方法把结论直接算好摆到台面上。
    ///
    /// 输出包含：
    ///   ① 我方血量/格挡/有效生命（血+格挡）
    ///   ② 每回合的**致死标记**：本回合承伤 >= 有效生命 → 🩸 会死
    ///   ③ 每个敌人这一回合要干什么 + 精确伤害
    ///   ④ 每个敌人的**后续招式池**（最疼的那一招有多痛）→ 让模型知道"下回合会不会更痛"
    ///   ⑤ 明确的行动结论：必须先挡还是可以全力输出
    /// </summary>
    public static string DescribeThreatTimeline(Player me)
    {
        try
        {
            Creature pc = me?.Creature;
            if (pc == null) return "";
            int hp = pc.CurrentHp;
            int maxHp = Math.Max(1, pc.MaxHp);
            int block = pc.Block;
            var enemies = HittableEnemies(me);
            if (enemies.Count == 0) return "";

            var sb = new StringBuilder();
            int incoming = enemies.Sum(IntentDamage);
            int effectiveHp = hp + block;
            bool lethalThisTurn = incoming >= effectiveHp;
            bool lethalWithoutNewBlock = incoming >= hp;      // 完全不挡必死
            bool comfortable = incoming <= block;             // 现有格挡已够

            sb.AppendLine("【逐回合威胁时间线（这是本回合决策的第一依据）】");
            sb.AppendLine($"  我方：生命 {hp}/{maxHp}（{hp * 100 / maxHp}%） 格挡 {block} " +
                          $"→ 本回合有效生命 {effectiveHp}");
            sb.AppendLine($"  本回合总承伤：{incoming}（{enemies.Count} 个敌人意图之和）");
            sb.AppendLine($"  不额外格挡的剩余生命：{hp - incoming}   " +
                          $"加上现有格挡后的剩余：{effectiveHp - incoming}");

            if (lethalThisTurn && !comfortable)
                sb.AppendLine($"  🩸🩸 **致死预警**：本回合承伤 {incoming} >= 有效生命 {effectiveHp}，**现在不全力防御就会当场死亡**！");
            else if (lethalWithoutNewBlock)
                sb.AppendLine($"  🩸 **高风险**：本回合承伤 {incoming} >= 当前生命 {hp}，**必须打出格挡牌**，否则掉血极其危险。");
            else if (comfortable)
                sb.AppendLine($"  ✅ 现有格挡 {block} 已能完全吸收 {incoming} 点承伤，**本回合不需要再打任何格挡牌，应全力输出**。");
            else
                sb.AppendLine($"  ⚠ 本回合会掉约 {incoming - block} 点血，可接受，但若凑不出斩杀请注意留血。");

            sb.AppendLine("  逐敌明细（本回合意图 + 后续可能招式）：");
            for (int i = 0; i < enemies.Count; i++)
            {
                Creature e = enemies[i];
                int d = IntentDamage(e);
                string intent = DescribeIntent(e);
                string future = "";
                string mechShort = "";
                try
                {
                    string mid = e.Monster?.Id?.Entry;
                    var m = Codex.Monster(mid);
                    if (m != null && m.Moves.Count > 0)
                    {
                        // 找出该怪**最疼的一招**（按 damage 字段解析出的最大单次总伤）
                        int worst = 0; string worstName = "";
                        foreach (var mv in m.Moves)
                        {
                            int v = ParseMoveDamage(mv.Damage);
                            if (v > worst) { worst = v; worstName = mv.Name; }
                        }
                        if (worst > 0)
                            future = $"  后续最疼一招：{worstName} 约 {worst} 伤（{m.Moves.Count} 招轮转）";
                    }
                    // v2.6.1：把该怪机制**挂在它自己这一行**，让"序号↔机制"一一对应，
                    // 避免 LLM 读了机制段落却记不住"#3 才是会休眠/会给我挂脆弱的那个"。
                    mechShort = MonsterMechanics.ShortLine(mid);
                }
                catch { }
                sb.AppendLine($"    #{i + 1} {e.Name}  HP {e.CurrentHp}/{e.MaxHp}  本回合意图: " +
                              (string.IsNullOrEmpty(intent) ? "（无攻击）" : intent) +
                              $"，将造成 {d} 伤" +
                              (string.IsNullOrEmpty(mechShort) ? "" : $"  ◀ {mechShort}") +
                              (string.IsNullOrEmpty(future) ? "" : "\n      " + future));
            }

            // v2.6.0：注入**怪物特殊机制**（spire-codex 只有招式数值、没有机制叙事）。
            // 让 LLM 在当下这场战斗就懂"打死一只其他休眠""叠了虚弱格挡打折"等联动规则。
            try
            {
                var ids = enemies.Select(e => { try { return e.Monster?.Id?.Entry; } catch { return null; } })
                                 .Where(s => !string.IsNullOrEmpty(s)).ToList();
                string mech = MonsterMechanics.Prompt(ids);
                if (!string.IsNullOrEmpty(mech)) sb.AppendLine(mech);
            }
            catch { }

            // ④ 行动结论（把该不该防的话说死，不给模型自由发挥）
            sb.AppendLine("  ▶ **行动结论**：" +
                (lethalThisTurn && !comfortable
                    ? "必须**先出格挡/减伤牌**，把格挡叠到至少 " + Math.Max(0, incoming - block) +
                      " 点再考虑输出；打不死敌人时**绝不要为了输出放弃防御**。"
                    : lethalWithoutNewBlock
                        ? "至少打出 1 张格挡牌（建议叠 " + Math.Max(0, incoming - block) + " 点格挡），剩余能量再输出。"
                        : comfortable
                            ? "不用打格挡牌，把全部能量用于输出/铺垫。"
                            : "优先考虑：若能本回合斩杀敌人则优先斩杀（少一个敌人就少一份承伤），否则打 1 张格挡牌。"));
            return sb.ToString();
        }
        catch (Exception e)
        {
            try { Pilot.Warn("威胁时间线生成失败: " + e.Message); } catch { }
            return "";
        }
    }

    /// <summary>把 Codex 招式里的伤害字符串（"22" / "22/26" / "11x2"）解析成最大总伤害。</summary>
    private static int ParseMoveDamage(string s)
    {
        if (string.IsNullOrEmpty(s)) return 0;
        try
        {
            int mult = 1;
            string body = s;
            int xi = s.IndexOf('x');
            if (xi < 0) xi = s.IndexOf('X');
            if (xi > 0)
            {
                body = s.Substring(0, xi);
                int.TryParse(s.Substring(xi + 1), out mult);
                if (mult <= 0) mult = 1;
            }
            // 取 "/" 分隔的最大值（如 "22/26" = 普通/进阶）
            int best = 0;
            foreach (string part in body.Split('/'))
            {
                if (int.TryParse(part.Trim(), out int v) && v > best) best = v;
            }
            return best * mult;
        }
        catch { return 0; }
    }

    public static string DescribeIntent(Creature e)
    {        try
        {
            MonsterModel mon = e.Monster;
            if (mon == null || mon.NextMove == null) return "";
            var intents = mon.NextMove.Intents;
            if (intents == null || intents.Count == 0) return "（无）";

            var parts = new List<string>();
            var targets = new List<Creature> { e };
            foreach (var intent in intents)
            {
                if (intent is MegaCrit.Sts2.Core.MonsterMoves.Intents.AttackIntent atk)
                {
                    int dmg = 0;
                    try { dmg = atk.GetTotalDamage(targets, e); } catch { dmg = 0; }
                    int repeats = 1;
                    try { repeats = Math.Max(1, atk.Repeats); } catch { }
                    parts.Add(repeats > 1 ? $"攻击{dmg}(多段×{repeats})" : $"攻击{dmg}");
                }
                else if (intent != null)
                {
                    parts.Add(IntentCn(intent.GetType().Name.Replace("Intent", "")));
                }
            }
            return parts.Count == 0 ? "" : string.Join("+", parts);
        }
        catch
        {
            try { if (e.Monster != null && e.Monster.IntendsToAttack) return "攻击?"; } catch { }
            return "";
        }
    }

    /// <summary>敌人本回合意图总伤害（数值）。</summary>
    public static int IntentDamage(Creature e)
    {
        try
        {
            MonsterModel mon = e.Monster;
            if (mon?.NextMove?.Intents == null) return 0;
            var targets = new List<Creature> { e };
            int total = 0;
            foreach (var intent in mon.NextMove.Intents)
            {
                if (intent is MegaCrit.Sts2.Core.MonsterMoves.Intents.AttackIntent atk)
                {
                    try { total += atk.GetTotalDamage(targets, e); } catch { }
                }
            }
            return Math.Max(0, total);
        }
        catch { return 0; }
    }

    /// <summary>
    /// 卡牌的“实际作用”文本：伤害 / 格挡 / 抽牌 等。
    /// 优先用原版 GetDescriptionForPile（已求值、含升级与关键词），失败再退到反射估算。
    /// </summary>
    public static string CardStats(CardModel c, Player me)
    {
        var parts = new List<string>();
        try
        {
            // ① 已求值的卡面文本（最可靠；含实际数值）
            string desc = SafeDesc(c, 70);
            if (!string.IsNullOrEmpty(desc)) parts.Add(desc);

            // ② 结构化数值（供模型做算术，不必从文本里抠数字）
            int dmg = Brain.EstimateCardDamagePublic(c, me);
            int blk = Brain.EstimateCardBlockPublic(c, me);
            var nums = new List<string>();
            if (dmg > 0) nums.Add($"伤害≈{dmg}");
            if (blk > 0) nums.Add($"格挡≈{blk}");
            if (SafeDraw(c) > 0) nums.Add($"抽{SafeDraw(c)}");
            if (nums.Count > 0) parts.Add("[" + string.Join(" ", nums) + "]");
        }
        catch { }
        return string.Join("  ", parts);
    }

    /// <summary>安全获取已求值的卡面描述。</summary>
    public static string SafeDesc(CardModel c, int maxLen = 120)
    {
        if (c == null) return "";
        // GetDescriptionForPile 会调用 AddExtraArgsToDescription，故含真实数值
        foreach (var attempt in new Func<string>[]
        {
            () => c.GetDescriptionForPile(PileType.None),
            () => c.GetDescriptionForUpgradePreview(),
            () => c.Description?.GetRawText(),
        })
        {
            try
            {
                string d = attempt();
                if (!string.IsNullOrWhiteSpace(d))
                {
                    d = d.Replace("\r", " ").Replace("\n", " ").Trim();
                    // 折叠连续空白
                    while (d.Contains("  ")) d = d.Replace("  ", " ");
                    return d.Length > maxLen ? d[..maxLen] + "…" : d;
                }
            }
            catch { /* 换下一种方式 */ }
        }
        return "";
    }

    private static string DescribePowers(Creature c)
    {
        try
        {
            var powers = c?.Powers;
            if (powers == null || powers.Count == 0) return "";
            return string.Join("、", powers
                .Where(p => p != null && p.Amount != 0)
                .Select(p => $"{PowerCn(p.GetType().Name)}×{p.Amount}")
                .Take(12));
        }
        catch { return ""; }
    }

    private static List<string> SafeRelics(Player me)
    {
        var list = new List<string>();
        try
        {
            var relics = me?.Relics;
            if (relics == null) return list;
            foreach (var r in relics)
            {
                try { list.Add(r.Id.Entry); } catch { }
            }
        }
        catch { }
        return list;
    }

    private static List<string> SafePotions(Player me)
    {
        var list = new List<string>();
        try
        {
            var potions = me?.Potions;
            if (potions == null) return list;
            foreach (var p in potions)
            {
                try { list.Add(p.Id.Entry); } catch { }
            }
        }
        catch { }
        return list;
    }

    /// <summary>取当前局面的真实费用（含各种修正）。用 Name 定位，避免编译期签名不一致。</summary>
    public static int SafeCost(CardModel c)
    {
        try
        {
            object costObj = c.EnergyCost;
            if (costObj == null) return 0;
            var t = costObj.GetType();
            // CostModifiers 枚举在 MegaCrit.Sts2.Core.Entities.Cards 命名空间
            object local = null;
            try { local = Enum.Parse(t.Assembly.GetType("MegaCrit.Sts2.Core.Entities.Cards.CostModifiers") ?? typeof(int), "Local"); }
            catch { local = null; }

            if (local != null)
            {
                var m = t.GetMethod("GetWithModifiers");
                if (m != null)
                {
                    var ps = m.GetParameters();
                    if (ps.Length == 1) return Convert.ToInt32(m.Invoke(costObj, new[] { local }));
                }
            }
            // 退路：直接读字段
            foreach (string name in new[] { "Value", "Cost", "BaseCost", "_value" })
            {
                var p = t.GetProperty(name);
                if (p != null) return Convert.ToInt32(p.GetValue(costObj));
                var f = t.GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
                if (f != null) return Convert.ToInt32(f.GetValue(costObj));
            }
        }
        catch { }
        return 0;
    }

    /// <summary>当前是否能打出（CanPlay 的真实结果）。</summary>
    public static bool SafeCanPlay(CardModel c)
    {
        try { return c.CanPlay(out _, out _); }
        catch { return true; }
    }

    private static int SafeDraw(CardModel c)
    {
        try
        {
            string id = c.Id.Entry ?? "";
            if (id.Contains("DRAW") || id.Contains("CYCLE") || id.Contains("OFFERING") ||
                id.Contains("SEEK") || id.Contains("SKIM") || id.Contains("ACROBATICS"))
                return 1;
        }
        catch { }
        return 0;
    }

    private static int PileCount(Player me, PileType t)
    {
        try { return t.GetPile(me)?.Cards?.Count ?? 0; }
        catch { return 0; }
    }

    private static string CharName(Player me)
    {
        try { return me?.Character?.Id.Entry ?? "?"; }
        catch { return "?"; }
    }

    public static string TypeCn(CardType t) => t switch
    {
        CardType.Attack => "攻击",
        CardType.Skill => "技能",
        CardType.Power => "能力",
        CardType.Status => "状态",
        CardType.Curse => "诅咒",
        _ => t.ToString(),
    };

    public static string RarityCn(CardRarity r) => r switch
    {
        CardRarity.Common => "普通",
        CardRarity.Uncommon => "罕见",
        CardRarity.Rare => "稀有",
        CardRarity.Ancient => "远古",
        CardRarity.Basic => "初始",
        _ => r.ToString(),
    };

    public static string PointCn(MegaCrit.Sts2.Core.Map.MapPointType t) => t switch
    {
        MegaCrit.Sts2.Core.Map.MapPointType.Monster => "普通战斗",
        MegaCrit.Sts2.Core.Map.MapPointType.Elite => "精英战斗",
        MegaCrit.Sts2.Core.Map.MapPointType.Boss => "首领",
        MegaCrit.Sts2.Core.Map.MapPointType.Unknown => "事件/未知",
        MegaCrit.Sts2.Core.Map.MapPointType.Shop => "商店",
        MegaCrit.Sts2.Core.Map.MapPointType.Treasure => "宝箱",
        MegaCrit.Sts2.Core.Map.MapPointType.RestSite => "休息点",
        MegaCrit.Sts2.Core.Map.MapPointType.Ancient => "远古",
        _ => t.ToString(),
    };

    /// <summary>把意图对象转成中文短语（供 Evaluator 复用）。</summary>
    public static string IntentName(object intent)
    {
        try
        {
            if (intent == null) return "";
            return IntentCn(intent.GetType().Name.Replace("Intent", ""));
        }
        catch { return ""; }
    }

    private static string IntentCn(string raw) => raw switch
    {
        "SingleAttack" or "MultiAttack" => "攻击",
        "Defend" or "Block" => "防御",
        "Buff" => "增益",
        "Debuff" or "DebuffStrong" => "减益",
        "Heal" => "治疗",
        "Summon" => "召唤",
        "Stun" => "眩晕",
        "Sleep" => "睡眠",
        "Escape" => "逃跑",
        "Status" or "CardDebuff" => "塞牌",
        "DeathBlow" => "致死打击",
        "Hidden" => "未知",
        _ => raw,
    };

    private static string PowerCn(string typeName) => typeName switch
    {
        "StrengthPower" => "力量",
        "DexterityPower" => "敏捷",
        "VulnerablePower" => "易伤",
        "WeakPower" => "虚弱",
        "FrailPower" => "脆弱",
        "PoisonPower" => "中毒",
        "RegenPower" => "再生",
        "ThornsPower" => "荆棘",
        "PlatedArmorPower" => "板甲",
        "MetallicizePower" => "金属化",
        "ArtifactPower" => "神器",
        "IntangiblePower" => "无形",
        "BarricadePower" => "壁垒",
        "DemonFormPower" => "恶魔形态",
        "RitualPower" => "仪式",
        "EnragePower" => "激怒",
        "AngerPower" => "愤怒",
        _ => typeName.Replace("Power", ""),
    };
}
