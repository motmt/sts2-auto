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
/// 战斗前瞻 —— 「移植评估函数 + 当前回合穷举」。
///
/// 设计来源（用户指定参考模组 <c>Torch1230/CombatSolver</c>）：
///   · 评估权重直接移植它的 <c>src/Search/SolverWeights.cs</c>（见 <see cref="W"/>）；
///   · 状态评分公式移植它的 <c>CombatBeamSolver.StateEvaluation.Snapshot()</c>
///     —— 核心是「生存优先」：<c>projectedHp * 30000</c> 远大于敌方掉血的
///     <c>enemyHp * (-10000)</c>，且死亡罚分 <c>-1e12</c> 一票否决。
///
/// 与它的**本质区别**（也是本模组刻意保持的克制）：
///   · **不做**完整战斗模拟（那套方案要克隆整套 RNG、镜像每张牌的效果，
///     实测单次搜索 ~120 秒 / 16GB 内存）；
///   · **只穷举当前回合**：手牌可打组合 + 每个候选的目标分配 + 出牌顺序，
///     用本模组已有的近似数值做**确定性推演**，全程不写任何游戏状态、不克隆 RNG、不跨回合。
///
/// v2.7.1 关键修正 —— **铺垫牌与增益的链式建模**（血泪）：
///   v2.7.0 的穷举只看每张牌的**裸伤害**，完全没建模「先挂易伤 → 后续攻击 ×1.5」这类
///   **跨步骤状态传递**。于是把 `痛击(8伤+挂易伤)` 排在攻击牌后面也不扣分，
///   算出的"最优"是错的，喂给 LLM 的候选也跟着错 —— 实测表现为
///   「没先打易伤再打伤害牌，最后刚好差 3 点血打不死」。
///   现在补上：维护**每个敌人的易伤层数 / 虚弱层数**，攻击结算时按
///   <c>VulnerablePower.ModifyDamageMultiplicative</c>（×1.5）与
///   <c>WeakPower.ModifyDamageMultiplicative</c>（×0.75）真实折算。
///
/// 安全性：所有入口都包 try/catch，任何异常 → 返回 null，调用方静默继续，
/// 绝不影响正常出牌流程。
/// </summary>
public static class CombatLookahead
{
    // =====================================================================
    //  一、移植的评估权重（对齐 CombatSolver/src/Search/SolverWeights.cs）
    // =====================================================================

    /// <summary>CombatSolver 的精确权重常量。数值来源：参考模组源码，未做改动。</summary>
    private static class W
    {
        public const double DeathPenalty = -1e12;              // 死亡（一票否决）
        public const double VictoryBonus = 1e10;               // 胜利
        public const double Hp = 30000;                        // 每点剩余生命
        public const double EnemyHp = -10000;                  // 每点敌方剩余血（负 = 越少越好）
        public const double RiskPenalty = -2000;               // 未挡住的承伤
        public const double ActionPenalty = -1;                // 每多一步
        public const int CurrentEnergyBeamCap = 6;             // 能量保留上限
        public const int VulnerableAttackWindowCap = 24;       // 易伤窗口上限
        public const double VulnerableAttackMultiplierBeamValue = 5000;
        public const int PersistentBuffDeltaBeamCap = 32;      // 持续增益上限
        public const double PersistentBuffDeltaBeamValue = 50000;
        public const double SoldHpPenalty = -20000;            // 卖血
        public const double LongTermResourceBeamValue = 25000;
        public const int LongTermResourceCap = 25000;
        public const double LiveDeckClutterPenalty = -8000;    // 牌组杂质

        /// <summary>
        /// v2.7.2 新增：**溢出（无效）伤害**的罚分 —— 打在已死/血不够的敌人身上的那部分伤害。
        /// 数值取 <see cref="EnemyHp"/> 的绝对值（10000/点）：一点溢出 = 一点白送的输出机会。
        /// </summary>
        public const double WastedDamagePenalty = -10000;

        /// <summary>
        /// v2.7.3 新增：**本次实际掉血**的附加小罚（轻微"惜血"倾向）。
        ///
        /// 注意它**不是**格挡的收益来源 —— 格挡的功劳已经通过
        /// <c>projectedHp</c>（掉血少 → 剩余血多 → 分高）完整体现。
        /// 若在这里按"未挡住承伤"大刀阔斧计价（旧 v2.7.3 草案用 −30000/点），
        /// 会变成**变相奖励堆格挡**：多挡出来的无效格挡也按 −30000/点"赚分"，
        /// 实测导致「三防御」与「三打击」打平甚至胜出 = 用户抱怨的"一直防御"复发。
        ///
        /// 故幅度取 <see cref="EnemyHp"/> 的 1/2（5000/点）：
        /// 保证"少掉 1 血"略优于"多打 1 点敌血"，但绝不压倒输出。
        /// </summary>
        public const double HpLostPenalty = -5000;

        /// <summary>
        /// v2.7.3 修正：剩余能量的价值必须 **远小于** 一点敌血的价值。
        /// （详见原注释：60000 → 500，本回合省下的能量回合结束即作废）
        /// </summary>
        public const double CurrentEnergyBeamValue = 500;
        public const int CurrentEnergyValueCap = 3;
    }

    private const int MaxSequenceLength = 6;      // 最多 6 步（与 LLM 提示词上限一致）
    private const int NodeBudget = 3000;          // 状态节点总预算（超出即剪枝）
    private const int MaxTargetAssignments = 4;   // 每个候选最多枚举几种目标分配

    private const double VulnerableMul = 1.5;     // VulnerablePower.DamageIncrease 默认 1.5
    private const double WeakMul = 0.75;          // WeakPower.DamageDecrease 默认 0.75
    private const double FrailMul = 0.75;         // FrailPower 默认 0.75

    // =====================================================================
    //  二、对外入口
    // =====================================================================

    public sealed class LookaheadResult
    {
        public Bundle Best;
        public List<Bundle> Top = new();
        public int Nodes;
        public bool Truncated;
        public double Baseline;
    }

    public sealed class Bundle
    {
        public List<Step> Steps = new();
        public double Score;
        public int ProjectedHp;
        public int ProjectedEnemyHp;
        public bool Lethal;
        public int EnergySpent;
        /// <summary>方案对敌方造成的总伤害（含易伤折算，用于展示）。</summary>
        public int TotalDamageDealt;

        public bool ClearsRoom => ProjectedEnemyHp <= 0;
        public string Sign => ClearsRoom ? "斩杀清场" : (Lethal ? "会死" : $"剩血{ProjectedHp}");
    }

    public sealed class Step
    {
        public CardModel Card;
        public int? TargetIndex;   // 1-based 敌人序号（与提示词 # 编号一致）
        public string Note;
        public int Cost;
    }

    public static bool WorthComputing(Player me, IReadOnlyList<CardModel> hand)
    {
        try
        {
            if (me?.Creature == null || hand == null) return false;
            if (!CombatManager.Instance.IsInProgress) return false;
            if (hand.Count < 2) return false;
            return true;
        }
        catch { return false; }
    }

    public static LookaheadResult Compute(Player me, IReadOnlyList<CardModel> hand)
    {
        try
        {
            if (!WorthComputing(me, hand)) return null;

            int energy = Brain.CurrentEnergyPublic(me);
            int myHp = me.Creature?.CurrentHp ?? 0;
            int myMaxHp = Math.Max(1, me.Creature?.MaxHp ?? 1);
            int myBlock = Brain.GetMyBlockPublic(me);
            bool frail = Brain.HasFrailPublic(me);

            List<Creature> enemies = HittableAlive(me);
            if (enemies.Count == 0) return null;

            var snap = new Snapshot
            {
                Count = enemies.Count,
                Hp = enemies.Select(e => Math.Max(0, e.CurrentHp)).ToArray(),
                Block = enemies.Select(e => Math.Max(0, e.Block)).ToArray(),
                // v2.7.1：初值 = 敌人身上**已有**的易伤/虚弱层数（跨回合残留的铺垫要继承）
                Vulnerable = enemies.Select(e => PowerAmount(e, "Vulnerable")).ToArray(),
                Weak = enemies.Select(e => PowerAmount(e, "Weak")).ToArray(),
                Incoming = enemies.Select(e => Brain.EstimateIncomingFromPublic(e)).ToArray(),
            };
            snap.IncomingEffective = new int[snap.Count];
            RecomputeIncoming(snap);

            int incomingTotal = snap.AliveIncoming();
            bool lethalByDefault = incomingTotal >= myHp + myBlock;

            var res = new LookaheadResult
            {
                Baseline = ScoreState(myHp, myMaxHp, myBlock, snap, incomingTotal,
                                      won: false, lethal: lethalByDefault, energyLeft: energy, me),
            };

            var pool = new List<CardModel>();
            foreach (CardModel c in hand)
            {
                if (c == null) continue;
                try
                {
                    if (Brain.IsBannedPublic(c)) continue;
                    if (c.Type == CardType.Status || c.Type == CardType.Curse) continue;
                    if (!c.CanPlay(out _, out _)) continue;
                    if (GameState.SafeCost(c) > energy) continue;
                    pool.Add(c);
                }
                catch { }
            }
            if (pool.Count == 0) return res;

            var found = new List<Bundle>();
            int nodes = 0;
            bool truncated = false;

            Search(0, energy, myBlock, myHp, snap,
                   new List<Step>(), found, ref nodes, ref truncated,
                   pool, myMaxHp, frail, me);

            res.Nodes = nodes;
            res.Truncated = truncated;
            res.Top = found
                .OrderByDescending(b => b.ClearsRoom)
                .ThenByDescending(b => b.Score)
                .ThenBy(b => b.Steps.Count)
                .Take(5)
                .ToList();
            res.Best = res.Top.FirstOrDefault();

            // v2.7.2 自检：逐张打印「牌面真实数值」，用于一次实战确认动态变量读取是否准确
            // （期望：痛击 8伤/2易伤、防御 5格挡、打击 6伤）。
            if (PilotConfig.Current.Verbose)
            {
                try
                {
                    var dump = pool.Select(c =>
                    {
                        var ef = CardEffectOf(c, me);
                        string tag = (ef.VulnApply > 0 || ef.WeakApply > 0)
                            ? $"易伤{ef.VulnApply}/虚弱{ef.WeakApply}" : "-";
                        return $"{c.Id.Entry}:{ef.Damage}伤/{ef.Block}格/{tag}";
                    });
                    // v2.7.6：同时打印每个敌人的「意图承伤」，用于核对
                    // "前瞻看到的承伤" 是否 == "时间线写的承伤"（口径必须一致）。
                    var incDump = string.Join(",", enemies.Select(e =>
                        $"{e.Name}={Brain.EstimateIncomingFromPublic(e)}"));
                    Pilot.Log("🧪 前瞻卡牌识别: " + string.Join(" | ", dump));
                    Pilot.Log($"🧪 前瞻承伤口径: 我血={myHp} 格挡={myBlock} " +
                              $"合计承伤={snap.AliveIncoming()} 逐敌[{incDump}] " +
                              $"基准分={Fmt(res.Baseline)}");
                }
                catch { }
            }

            return res;
        }
        catch (Exception e)
        {
            try { Pilot.Warn("战斗前瞻异常（已忽略）: " + e.Message); } catch { }
            return null;
        }
    }

    // =====================================================================
    //  三、DFS 穷举（只当前回合）
    // =====================================================================

    private sealed class Snapshot
    {
        public int Count;
        public int[] Hp;
        public int[] Block;
        public int[] Vulnerable;        // v2.7.1 每个敌人的易伤层数
        public int[] Weak;              // v2.7.1 每个敌人的虚弱层数
        public int[] Incoming;          // 每个敌人的基础意图伤害
        public int[] IncomingEffective; // 按虚弱折算后的实际承伤
        /// <summary>v2.7.2 累计的**溢出（无效）伤害** —— 打在已死/血不够的敌人身上的那部分。</summary>
        public int Wasted;

        public Snapshot Clone() => new Snapshot
        {
            Count = Count,
            Hp = (int[])Hp.Clone(),
            Block = (int[])Block.Clone(),
            Vulnerable = (int[])Vulnerable.Clone(),
            Weak = (int[])Weak.Clone(),
            Incoming = (int[])Incoming.Clone(),
            IncomingEffective = (int[])IncomingEffective.Clone(),
            Wasted = Wasted,
        };

        public List<int> AliveIdx()
        {
            var l = new List<int>(Count);
            for (int i = 0; i < Count; i++) if (Hp[i] > 0) l.Add(i);
            return l;
        }

        public int SumHp()
        {
            int s = 0;
            for (int i = 0; i < Count; i++) s += Math.Max(0, Hp[i]);
            return s;
        }

        /// <summary>存活敌人对我造成的总承伤（虚弱折算，敌方输出 ×0.75）。</summary>
        public int AliveIncoming()
        {
            int s = 0;
            for (int i = 0; i < Count; i++) if (Hp[i] > 0) s += IncomingEffective[i];
            return s;
        }
    }

    private static void Search(
        int depth, int energy, int myBlock, int myHp, Snapshot snap,
        List<Step> path, List<Bundle> found,
        ref int nodes, ref bool truncated,
        List<CardModel> pool, int myMaxHp, bool frail, Player me)
    {
        if (nodes >= NodeBudget) { truncated = true; return; }
        if (depth >= MaxSequenceLength) return;

        if (path.Count > 0)
        {
            nodes++;
            found.Add(Evaluate(path, energy, myBlock, myHp, snap, frail, me));
            if (nodes >= NodeBudget) { truncated = true; return; }
        }

        for (int i = 0; i < pool.Count; i++)
        {
            CardModel c = pool[i];
            if (path.Any(s => ReferenceEquals(s.Card, c))) continue;   // 同张牌只用一次
            int cost = GameState.SafeCost(c);
            if (cost > energy) continue;

            CardEffect eff = CardEffectOf(c, me);
            bool needsTarget = NeedsEnemyTarget(c);
            bool hitsAll = HitsAllEnemies(c);

            List<int> assigns = needsTarget ? TargetAssignments(eff, snap) : new List<int> { -1 };
            if (assigns.Count == 0) assigns = new List<int> { -1 };

            int tried = 0;
            foreach (int t in assigns)
            {
                if (tried++ >= MaxTargetAssignments) break;

                Snapshot next = snap.Clone();
                ApplyEffect(next, eff, t, hitsAll, frail);

                path.Add(new Step
                {
                    Card = c,
                    TargetIndex = (t >= 0) ? t + 1 : null,
                    Cost = Math.Max(0, cost),
                    Note = eff.Note,
                });

                Search(depth + 1, energy - Math.Max(0, cost), myBlock, myHp, next,
                       path, found, ref nodes, ref truncated, pool, myMaxHp, frail, me);

                path.RemoveAt(path.Count - 1);
                if (truncated) return;
            }
        }
    }

    /// <summary>把一张牌的效果施加到快照上（v2.7.1：含易伤/虚弱的链式传递）。</summary>
    private static void ApplyEffect(Snapshot s, CardEffect eff, int target, bool hitsAll, bool frail)
    {
        if (eff.Damage > 0)
        {
            if (hitsAll)
            {
                foreach (int k in s.AliveIdx()) s.Wasted += ApplyDamage(s, k, eff.Damage);
            }
            else
            {
                int k = (target >= 0 && target < s.Count) ? target : 0;
                // v2.7.2：目标已死 → 这张牌白白浪费（伤害全算溢出），不再"转移"到别的敌人头上
                if (k < 0 || k >= s.Count || s.Hp[k] <= 0) s.Wasted += eff.Damage;
                else s.Wasted += ApplyDamage(s, k, eff.Damage);
            }
        }
        if (eff.VulnApply > 0)
        {
            if (hitsAll) { foreach (int k in s.AliveIdx()) s.Vulnerable[k] += eff.VulnApply; }
            else { int k = (target >= 0 && target < s.Count) ? target : 0; if (s.Hp[k] > 0) s.Vulnerable[k] += eff.VulnApply; }
        }
        if (eff.WeakApply > 0)
        {
            if (hitsAll) { foreach (int k in s.AliveIdx()) s.Weak[k] += eff.WeakApply; }
            else { int k = (target >= 0 && target < s.Count) ? target : 0; if (s.Hp[k] > 0) s.Weak[k] += eff.WeakApply; }
        }
        // 虚弱会降低该敌人对我方的输出 → 重算承伤
        RecomputeIncoming(s);
    }

    /// <summary>按当前虚弱层数重算每个敌人的"有效承伤"。</summary>
    private static void RecomputeIncoming(Snapshot s)
    {
        for (int i = 0; i < s.Count; i++)
        {
            int raw = s.Incoming != null && i < s.Incoming.Length ? s.Incoming[i] : 0;
            s.IncomingEffective[i] = (int)Math.Floor(raw * (s.Weak[i] > 0 ? WeakMul : 1.0));
        }
    }

    /// <summary>把伤害施加到第 k 个敌人（v2.7.1：先按易伤 ×1.5 折算，再扣格挡）。</summary>
    private static int ApplyDamage(Snapshot s, int k, int rawDamage)
    {
        if (k < 0 || k >= s.Count || s.Hp[k] <= 0) return 0;

        double mul = s.Vulnerable[k] > 0 ? VulnerableMul : 1.0;
        int dmg = (int)Math.Floor(rawDamage * mul);
        if (dmg <= 0) return 0;

        int remain = dmg;
        if (s.Block[k] > 0)
        {
            int absorbed = Math.Min(s.Block[k], remain);
            s.Block[k] -= absorbed;
            remain -= absorbed;
        }
        int lost = Math.Min(s.Hp[k], remain);
        s.Hp[k] -= lost;
        if (s.Hp[k] < 0) s.Hp[k] = 0;

        // v2.7.2 修正 Wasted：剩余伤害里**超出该敌人血量的部分**全是溢出（无效）伤害。
        // 旧实现把「已经打死还要继续砍」的那部分也算进有效伤害，
        // 于是「两张打击打两只残血怪」看起来和「两张打击集火一只」掉一样多的总血 →
        // 分散打击被系统性高估、集火被系统性低估。
        int wasted = Math.Max(0, remain - lost);
        return wasted;
    }

    // =====================================================================
    //  四、状态评分（移植 CombatSolver.StateEvaluation.Snapshot 的核心项）
    // =====================================================================

    private static double ScoreState(
        int projectedHp, int myMaxHp, int myBlock, Snapshot s,
        int incoming, bool won, bool lethal, int energyLeft, Player me)
    {
        bool dead = projectedHp <= 0;
        // 生命项：projectedHp 是「结算完承伤后」的剩余血量，本身就体现了格挡的功劳
        // （格挡多 → 掉血少 → projectedHp 高）。因此这里**不再**对格挡/未挡住承伤重复计价，
        // 否则「多挡的部分」会被二次奖励，导致宁可堆过量格挡也不输出（v2.7.3 修）。
        double score = dead ? W.DeathPenalty : projectedHp * W.Hp;

        // 我方"本次实际掉血"额外小罚：让"少掉 1 血"略优于"多打 1 点敌血"，
        // 但幅度远小于生命项本身，避免压过输出（v2.7.3）。
        int actuallyLost = Math.Max(0, Math.Max(0, incoming - myBlock));
        score += actuallyLost * W.HpLostPenalty;

        score += s.SumHp() * W.EnemyHp;

        // v2.7.2：溢出伤害罚分（打死了还继续砍 / 打在血不够的身上 = 白送的机会）
        score += s.Wasted * W.WastedDamagePenalty;

        if (won && !dead) score += W.VictoryBonus;

        // v2.7.3：剩余能量只值"能留就留一点"（500/点，最多 3 点）。
        // 绝不能用 CombatSolver 的 60000 —— 那是跨回合权重，在本回合语境下会把
        // 「打一张 1 费攻击」的净收益抹成 0，导致评分一路选防御（用户实测抱怨）。
        score += Math.Min(Math.Max(0, energyLeft), W.CurrentEnergyValueCap) * W.CurrentEnergyBeamValue;

        if (Brain.HasFrailPublic(me)) score -= 1500;

        score += Math.Min(MyStat(me, "Strength") + MyStat(me, "Dexterity"),
                          W.PersistentBuffDeltaBeamCap) * 200;

        return score;
    }

    private static Bundle Evaluate(
        List<Step> path, int energy, int myBlock, int myHp, Snapshot start, bool frail, Player me)
    {
        Snapshot cur = start.Clone();
        int e = energy;
        int b = myBlock;
        int dealt = 0;
        int selfLoss = 0;   // v2.7.4：本方案累计的自伤（御血术/放血等）

        foreach (Step st in path)
        {
            CardEffect eff = CardEffectOf(st.Card, me);
            e -= Math.Max(0, st.Cost);
            selfLoss += Math.Max(0, eff.SelfHpLoss);   // v2.7.4
            int hpBefore = cur.SumHp();
            ApplyEffect(cur, eff, (st.TargetIndex ?? 1) - 1, HitsAllEnemies(st.Card), frail);
            if (eff.Block > 0) b += (int)Math.Floor(eff.Block * (frail ? FrailMul : 1.0));
            dealt += Math.Max(0, hpBefore - cur.SumHp());
        }

        int remainIncoming = cur.AliveIncoming();
        bool won = cur.SumHp() <= 0;
        if (won) remainIncoming = 0;

        // v2.7.4：先扣自己的自伤（不可格挡），再算承伤
        int hpAfterSelf = Math.Max(0, myHp - selfLoss);

        int finalHp = hpAfterSelf;
        if (remainIncoming > 0)
        {
            int afterBlock = remainIncoming - b;
            if (afterBlock > 0) finalHp = Math.Max(0, hpAfterSelf - afterBlock);
        }

        var bundle = new Bundle
        {
            Steps = new List<Step>(path),
            ProjectedHp = finalHp,
            ProjectedEnemyHp = cur.SumHp(),
            Lethal = finalHp <= 0,
            EnergySpent = energy - e,
            TotalDamageDealt = dealt,
        };
        bundle.Score = ScoreState(finalHp, myMaxHp: 0, b, cur, remainIncoming, won,
                                  bundle.Lethal, e, me);
        bundle.Score += path.Count * W.ActionPenalty;
        return bundle;
    }

    // =====================================================================
    //  五、卡牌语义判定（含减益施加量）
    // =====================================================================

    private sealed class CardEffect
    {
        public int Damage;
        public int Block;
        public int VulnApply;   // 施加给目标的易伤层数
        public int WeakApply;   // 施加给目标的虚弱层数
        /// <summary>
        /// v2.7.4 新增：打出这张牌**自己要损失的生命**（`HpLossVar.BaseValue`）。
        /// 典型：御血术（1 费，失 2 血 + 15 伤）、放血（失 3 血换 1 能量）。
        /// 旧实现完全没建模 → 前瞻算出的"剩余血量"系统性偏高，
        /// 濒死时会把「御血术」当成零代价的 20 伤输出牌推荐（实测）。
        /// </summary>
        public int SelfHpLoss;
        public string Note = "";
    }

    private static readonly Dictionary<string, CardEffect> _effCache = new();

    /// <summary>
    /// 读取一张牌的"施加易伤/虚弱"能力。
    ///
    /// v2.7.2 铁律 —— **一律读结构化数据，禁止解析卡面文本**：
    ///   ① **`c.DynamicVars`**：`DynamicVarSet` 自带强类型索引器
    ///      `Vulnerable`（`PowerVar&lt;VulnerablePower&gt;`）/`Weak`（`PowerVar&lt;WeakPower&gt;`），
    ///      直接取 `BaseValue` 就是该牌施加的层数（痛击 = 2 易伤，震荡波 = 2 虚弱 …）。
    ///   ② 兜底：扫描 `DynamicVars` 的全部键名含 `Vulnerable` / `Weak` 的项。
    ///
    /// ⚠ 旧实现（v2.7.1）走 `GameState.SafeDesc` 解析「N层易伤」——
    ///   那是走 `GetDescriptionForPile` 的**已求值**文本，本机实测只认出 `易伤1`
    ///   （痛击应为 2），而格挡/伤害文本更是完全读不到 → 数值系统性偏低。
    ///   现改为纯结构化读取，不再依赖任何本地化文本格式。
    /// </summary>
    private static CardEffect CardEffectOf(CardModel c, Player me)
    {
        var eff = new CardEffect();
        try
        {
            eff.Damage = Math.Max(0, Brain.EstimateCardDamagePublic(c, me));
            eff.Block = Math.Max(0, Brain.EstimateCardBlockPublic(c, me));
        }
        catch { }

        try
        {
            string id = null;
            try { id = c.Id.Entry; } catch { }

            if (!string.IsNullOrEmpty(id) && _effCache.TryGetValue(id, out var cached))
            {
                eff.VulnApply = cached.VulnApply;
                eff.WeakApply = cached.WeakApply;
                eff.SelfHpLoss = cached.SelfHpLoss;
                eff.Note = BuildNote(eff);
                return eff;
            }

            int v = 0, w = 0, loss = 0;
            try
            {
                var vars = c.DynamicVars;
                if (vars != null)
                {
                    // ① 强类型索引器（DynamicVarSet.Vulnerable / .Weak）
                    try { if (vars.ContainsKey("VulnerablePower")) v = vars["VulnerablePower"]?.IntValue ?? 0; } catch { }
                    try { if (vars.ContainsKey("WeakPower")) w = vars["WeakPower"]?.IntValue ?? 0; } catch { }

                    // ② 键名模糊扫描（应对自定义命名，如 "VulnerablePower2"）
                    foreach (string key in vars.Keys)
                    {
                        if (key == null) continue;
                        if (v <= 0 && key.IndexOf("Vulnerab", StringComparison.OrdinalIgnoreCase) >= 0)
                            v = vars[key]?.IntValue ?? 0;
                        if (w <= 0 && key.IndexOf("Weak", StringComparison.OrdinalIgnoreCase) >= 0)
                            w = vars[key]?.IntValue ?? 0;
                    }

                    // ③ v2.7.4：自身生命损失（HpLossVar，如御血术 2 血 / 放血 3 血）
                    try { if (vars.ContainsKey("HpLoss")) loss = vars["HpLoss"]?.IntValue ?? 0; } catch { }
                    if (loss <= 0)
                    {
                        foreach (string key in vars.Keys)
                        {
                            if (key == null) continue;
                            if (key.IndexOf("HpLoss", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                key.IndexOf("HealthLoss", StringComparison.OrdinalIgnoreCase) >= 0)
                            { loss = vars[key]?.IntValue ?? 0; break; }
                        }
                    }
                }
            }
            catch { }

            eff.VulnApply = Math.Max(0, v);
            eff.WeakApply = Math.Max(0, w);
            eff.SelfHpLoss = Math.Max(0, loss);

            if (!string.IsNullOrEmpty(id))
                _effCache[id] = new CardEffect
                {
                    VulnApply = eff.VulnApply,
                    WeakApply = eff.WeakApply,
                    SelfHpLoss = eff.SelfHpLoss,
                };
        }
        catch { }

        eff.Note = BuildNote(eff);
        return eff;
    }

    private static string BuildNote(CardEffect eff)
    {
        var parts = new List<string>();
        if (eff.Damage > 0) parts.Add($"{eff.Damage}伤");
        if (eff.Block > 0) parts.Add($"{eff.Block}格挡");
        if (eff.VulnApply > 0) parts.Add($"+{eff.VulnApply}易伤");
        if (eff.WeakApply > 0) parts.Add($"+{eff.WeakApply}虚弱");
        if (eff.SelfHpLoss > 0) parts.Add($"自伤{eff.SelfHpLoss}");   // v2.7.4
        return parts.Count == 0 ? "辅助" : string.Join("+", parts);
    }

    private static bool NeedsEnemyTarget(CardModel c)
    {
        try { return c.TargetType == TargetType.AnyEnemy; }
        catch { return false; }
    }

    /// <summary>是否对全体敌人生效（近似判定）。无法确定时按单目标处理（保守）。</summary>
    private static bool HitsAllEnemies(CardModel c)
    {
        try
        {
            string tn = c.TargetType.ToString();
            return tn.Equals("AllEnemies", StringComparison.OrdinalIgnoreCase)
                || tn.Equals("All", StringComparison.OrdinalIgnoreCase)
                || tn.Equals("AllOpponents", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>
    /// 为一张带目标的牌枚举候选目标（按价值排序，最多 <see cref="MaxTargetAssignments"/> 个）：
    ///   · 铺垫牌（挂易伤/虚弱）→ 优先挂在「尚未被铺垫”的敌人上（避免重复挂浪费），
    ///     并按「最脆 / 最有威胁」排；
    ///   · 伤害牌 → 先试"折算后能砍死"的敌人（收益最大）。
    /// </summary>
    private static List<int> TargetAssignments(CardEffect eff, Snapshot s)
    {
        List<int> alive = s.AliveIdx();
        if (alive.Count == 0) return new List<int> { -1 };

        var ordered = new List<int>();

        if (eff.VulnApply > 0)
            ordered.AddRange(alive.Where(i => s.Vulnerable[i] <= 0).OrderBy(i => s.Hp[i]));
        if (eff.WeakApply > 0)
            ordered.AddRange(alive.Where(i => s.Weak[i] <= 0).OrderByDescending(i => s.Incoming[i]));

        if (eff.Damage > 0)
            ordered.AddRange(alive.Where(i => KillDamage(s, i, eff.Damage) >= s.Hp[i]).OrderBy(i => s.Hp[i]));

        foreach (int i in alive.OrderBy(i => s.Hp[i])) if (!ordered.Contains(i)) ordered.Add(i);
        foreach (int i in alive.OrderByDescending(i => s.Incoming[i])) if (!ordered.Contains(i)) ordered.Add(i);

        return ordered.Take(MaxTargetAssignments).ToList();
    }

    /// <summary>算上易伤倍率后这张牌对第 k 个敌人的实际伤害。</summary>
    private static int KillDamage(Snapshot s, int k, int rawDamage)
        => (int)Math.Floor(rawDamage * (s.Vulnerable[k] > 0 ? VulnerableMul : 1.0));

    // =====================================================================
    //  六、提示词渲染
    // =====================================================================

    public static string Prompt(LookaheadResult r, IReadOnlyList<CardModel> hand)
    {
        if (r == null || r.Top == null || r.Top.Count == 0) return "";
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("【本回合已验证候选方案（系统穷举 + 数学评估，含敌人目标分配）】");
            sb.AppendLine("  说明：下列方案由系统枚举本回合出牌次序并逐一点算得出（**已计入易伤×1.5 / " +
                          "虚弱×0.75 的连锁加成**），按「生存优先」排序（分越高越好，死亡方案标 ❌）。");
            sb.AppendLine($"  基准分（什么都不打）：{Fmt(r.Baseline)}" +
                          (r.Truncated ? "   ⚠ 枚举已按预算截断" : ""));

            List<CardModel> handList = hand?.ToList() ?? new List<CardModel>();
            for (int i = 0; i < r.Top.Count; i++)
            {
                Bundle b = r.Top[i];
                string rank = i == 0 ? "★最优" : $"候选{i + 1}";
                string delta = r.Baseline == 0 ? "" : $"（较基准 {Fmt(b.Score - r.Baseline)}）";
                sb.AppendLine($"  {rank} 分 {Fmt(b.Score)}{delta}  预期 {b.Sign}  " +
                              $"耗能 {b.EnergySpent}  剩余敌血 {b.ProjectedEnemyHp}  实伤 {b.TotalDamageDealt}");
                sb.Append("      顺序: ");
                var steps = new List<string>();
                foreach (Step st in b.Steps)
                {
                    int no = handList.FindIndex(h => ReferenceEquals(h, st.Card));
                    string cardRef = no >= 0 ? $"手牌#{no + 1}" : $"「{st.Card.Id.Entry}」";
                    string tgt = st.TargetIndex.HasValue ? $"→#{st.TargetIndex.Value}敌人" : "";
                    steps.Add($"{cardRef}{tgt}({st.Note})");
                }
                sb.AppendLine(string.Join(" → ", steps));
            }

            var best = r.Best;
            if (best != null)
            {
                sb.AppendLine("  ▶ **建议**：" + (best.Lethal
                    ? "⚠ 所有候选都无法避免死亡 —— 请重新检查是否漏用格挡/药水。"
                    : best.ClearsRoom
                        ? "★最优候选可本回合清场，**优先照它执行**（顺序与目标都按上面给的来，不要改）。"
                        : $"优先采用★最优候选的顺序与目标；若你有更优想法，其分数必须能超过 {Fmt(best.Score)}。"));
                sb.AppendLine("  注意：候选里的「→#N敌人」是系统算好的集火目标，**请把同样的敌人序号写进你的 plan.target**；" +
                              "**铺垫牌(易伤/虚弱)必须排在吃加成的攻击牌之前**，不要调换顺序。");
            }
            return sb.ToString();
        }
        catch (Exception e)
        {
            try { Pilot.Warn("前瞻提示词渲染失败: " + e.Message); } catch { }
            return "";
        }
    }

    public static string Summary(LookaheadResult r)
    {
        if (r?.Best == null) return "";
        var b = r.Best;
        string steps = string.Join("→", b.Steps.Select(s =>
            s.TargetIndex.HasValue ? $"{s.Card.Id.Entry}#{s.TargetIndex}" : s.Card.Id.Entry));
        return $"前瞻 {r.Top.Count} 候选 / {r.Nodes} 节点{(r.Truncated ? "(截断)" : "")}｜最优 {b.Sign} " +
               $"预期剩血 {b.ProjectedHp} 实伤 {b.TotalDamageDealt}｜{steps}";
    }

    // =====================================================================
    //  七、小工具
    // =====================================================================

    private static int MyStat(Player me, string powerName)
    {
        try
        {
            var p = me?.Creature?.Powers?.FirstOrDefault(x =>
                x.GetType().Name.Equals(powerName + "Power", StringComparison.OrdinalIgnoreCase));
            return p?.Amount ?? 0;
        }
        catch { return 0; }
    }

    private static int PowerAmount(Creature c, string powerName)
    {
        try
        {
            var p = c?.Powers?.FirstOrDefault(x =>
                x.GetType().Name.Equals(powerName + "Power", StringComparison.OrdinalIgnoreCase));
            return p?.Amount ?? 0;
        }
        catch { return 0; }
    }

    private static List<Creature> HittableAlive(Player me)
    {
        try
        {
            // v2.7.6：改用与【逐回合威胁时间线】相同的敌人列表（GameState.HittableEnemies）。
            // 旧实现走 cs.HittableEnemies，与时间线走 cs.Enemies 不同 —— 两处口径不一致时，
            // 前瞻算的风险和提示词写的风险会对不上，导致"该格挡时不格挡"。
            var list = GameState.HittableEnemies(me);
            if (list != null && list.Count > 0) return list;
        }
        catch { }
        try
        {
            ICombatState cs = me?.Creature?.CombatState;
            if (cs == null) return new List<Creature>();
            return cs.HittableEnemies?.Where(e => e != null && e.IsAlive).ToList() ?? new List<Creature>();
        }
        catch { return new List<Creature>(); }
    }

    private static string Fmt(double v)
    {
        if (Math.Abs(v) >= 1e11) return v < 0 ? "-∞" : "+∞";
        return v.ToString("N0");
    }
}
