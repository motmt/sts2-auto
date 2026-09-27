using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace AutoPilot;

/// <summary>
/// 决策大脑 —— AutoPilot 的智能核心。
///
/// 与官方 AutoSlayer 的本质区别：官方在战斗里“随机抽一张能打的就打 + 自身挂 999 格挡保命”，
/// 本类则用一套可解释的启发式评分替代随机：
///
///   ① 威胁评估：按敌人意图计算本回合将承受的伤害，决定“必须防多少”。
///   ② 出牌优先级：斩杀 &gt; 护住致命伤害 &gt; 高价值能力牌 &gt; 输出 &gt; 过牌/准备。
///   ③ 目标选择：优先打最快要死的、或伤害意图最高的敌人（先杀威胁，不平均输出）。
///   ④ 资源管理：残血才用治疗药水；有免费/低费删牌与过牌时先过牌再打核心输出。
///   ⑤ 人工干预：配置里的“选卡偏好/禁选卡”拥有最高权重 —— 玩家说的算。
///
/// 所有决策都会经 <see cref="Explain"/> 留下可读理由，便于玩家复盘与调参。
/// </summary>
public static class Brain
{
    // 决策留痕（供记忆总结使用）
    private static readonly List<string> _trace = new();
    private static readonly Random _rand = new();

    // v2.6.0 集火目标（血泪）—— 回合内目标一致性。
    // 用户实测：LLM 计划里把易伤挂到 3 号蛞蝓，紧接着的攻击却打在 2 号上——
    // 因为旧 ChooseTarget 完全无视 LLM 的意图，只用"能杀→血最少"的本地启发式，
    // 多敌人且血量相近时会各自乱挑，铺垫与输出脱节，易伤白挂。
    // 修复：**整场战斗维护一个"集火目标"**（按目标名持久化，跨回合保持），
    // 只要该目标仍存活，所有未显式指定序号的多目标牌都优先打它；
    // 目标死亡后自动切换到新威胁（能杀→意图伤害最高→血最少）。
    private static string _focusTargetName;

    /// <summary>强制重设集火目标（LLM 计划里首个显式指定的目标会调用它）。</summary>
    private static void SetFocusTarget(Creature c)
    {
        try { _focusTargetName = c?.Name; } catch { _focusTargetName = null; }
    }

    /// <summary>取出当前存活敌人里的集火目标（已死或不存在则返回 null）。</summary>
    private static Creature GetFocusTarget(Player me)
    {
        if (string.IsNullOrEmpty(_focusTargetName)) return null;
        try
        {
            ICombatState cs = me?.Creature?.CombatState;
            var enemies = cs?.HittableEnemies?.Where(e => e.IsAlive).ToList();
            if (enemies == null) return null;
            return enemies.FirstOrDefault(e => e.Name == _focusTargetName);
        }
        catch { return null; }
    }

    /// <summary>是否走大模型决策（engine=llm/hybrid 且 Key 已配置）。hybrid 与 llm 都优先问大模型。</summary>
    private static bool UseLlm
    {
        get
        {
            try
            {
                if (PilotConfig.NormalizeEngine(PilotConfig.Current.EngineMode) == "local") return false;
                return LlmClient.Ready;
            }
            catch { return false; }
        }
    }

    /// <summary>大模型失败时是否允许回退本地规则（hybrid 与 llm 都允许，保证永不停摆）。</summary>
    private static bool AllowFallback
    {
        get
        {
            try { return PilotConfig.NormalizeEngine(PilotConfig.Current.EngineMode) != "local"; }
            catch { return true; }
        }
    }

    // 供 RoomBrain / 其它决策模块复用的公开门控（引擎判断逻辑只写一份）
    public static bool UseLlmPublic => UseLlm;
    public static bool AllowFallbackPublic => AllowFallback;

    public static void Explain(string why)
    {
        _trace.Add($"[{DateTime.Now:HH:mm:ss}] {why}");
        if (_trace.Count > 500) _trace.RemoveRange(0, 200);
    }

    public static IReadOnlyList<string> Trace => _trace;

    // 供 GameState / 外部使用的公开包装
    public static int EstimateCardDamagePublic(CardModel c, Player me) => EstimateCardDamage(c, me);
    public static int EstimateCardBlockPublic(CardModel c, Player me) => EstimateCardBlock(c, me);
    public static int EstimateIncomingDamagePublic(Player me) => EstimateIncomingDamage(me);

    // v2.7.0：战斗前瞻（CombatLookahead）复用的公开包装。
    // 这些是**纯读取**接口（不写任何游戏状态），所以可安全供离线推演使用。
    public static int CurrentEnergyPublic(Player me) => CurrentEnergy(me);
    public static int GetMyBlockPublic(Player me) => GetMyBlock(me);
    public static bool HasFrailPublic(Player me) => HasFrail(me);
    public static bool IsBannedPublic(CardModel c) => IsBanned(c);
    public static int EstimateIncomingFromPublic(Creature enemy) => EstimateIncomingFrom(enemy);

    // =====================================================================
    //  一、战斗
    // =====================================================================

    /// <summary>
    /// 打一整场战斗：逐回合出牌直到战斗结束。
    /// 注意：这里不施加任何作弊增益（官方会挂 999 Plating/Regen），保留真实对抗。
    /// </summary>
    /// <summary>
    /// v2.8.0：把本场战斗整体托管给 CombatSolver。
    ///
    /// 流程：
    ///   ① 等本机玩家进入 Play 阶段（CombatSolver 要求状态稳定，否则 SetFullAuto 会被拒）；
    ///   ② 调 SolverBridge.SetDelegated(true) → CombatSolver 自己后台搜索 + 执行 + 结束回合；
    ///   ③ 我方在此只需等待战斗结束（不再出任何牌，避免两个 AI 抢着操作造成紊乱）；
    ///   ④ 战斗结束 → SetDelegated(false) 收尾，交回房间级流程。
    ///
    /// 若中途托管失败（CombatSolver 拒绝 / 抛错），自动降级到本地出牌循环，
    /// 保证"至少能打"，不会因为桥接问题卡死。
    /// </summary>
    private static async Task DelegateCombatAsync(CancellationToken ct)
    {
        Player me = Pilot.Me;
        NGame host = NGame.Instance;
        CombatState state = CombatManager.Instance.DebugOnlyGetState();

        if (me == null || host == null || state == null)
        {
            Pilot.Warn("战斗托管：上下文不完整，降级本地出牌");
            await FightAsync(ct);
            return;
        }

        _focusTargetName = null;

        // ① 等待进入本回合出牌阶段（最多 ~6 秒，避免开局动画/回合开始效果尚未结算）
        for (int i = 0; i < 60; i++)
        {
            if (ct.IsCancellationRequested || !Pilot.IsRunning || !CombatManager.Instance.IsInProgress) return;
            var pcs = me.PlayerCombatState;
            if (pcs != null && pcs.Phase == PlayerTurnPhase.Play) break;
            await MainThreadRunner.DelayAsync(100, ct);
        }

        // ② 开启托管
        bool ok = SolverBridge.SetDelegated(host, state, true);
        if (!ok)
        {
            Pilot.Warn("战斗托管失败（" + SolverBridge.LastError + "）→ 降级本地出牌");
            await FightAsync(ct);
            return;
        }

        Pilot.Log("🤝 战斗托管已开启：本场战斗交给 CombatSolver 引擎（本地前瞻停用）");

        // ③ 只等待战斗结束
        int guard = 0;
        while (CombatManager.Instance.IsInProgress && guard++ < 6000)
        {
            Pilot.MarkProgress();
            if (ct.IsCancellationRequested || !Pilot.IsRunning) break;
            await MainThreadRunner.DelayAsync(200, ct);
        }

        // ④ 收尾：关闭托管，保证下一场战斗/下一局不受影响
        try
        {
            CombatState? endState = CombatManager.Instance.IsInProgress
                ? CombatManager.Instance.DebugOnlyGetState()
                : state;
            if (endState != null) SolverBridge.SetDelegated(host, endState, false);
            else SolverBridge.SetDelegated(host, state, false);
        }
        catch { try { SolverBridge.SetDelegated(host, state, false); } catch { } }

        Pilot.Log("🤝 战斗托管已关闭（本场战斗结束）");
    }

    public static async Task FightAsync(CancellationToken ct)
    {
        Player me = Pilot.Me;
        if (me == null) { Pilot.Warn("战斗中找不到本机玩家"); return; }

        // v2.8.0：战斗托管 —— 只要 CombatSolver 在，本场战斗就整体交给它的引擎。
        // 它的「完整状态镜像 + Beam Search + 跨回合 + RNG 克隆」远超本地前瞻，
        // 所以这里不再自己出牌。AutoPilot 只负责：进入等待 → 关闭 → 交回房间流程。
        if (PilotConfig.Current.CombatEngine == "solver" && SolverBridge.Available)
        {
            await DelegateCombatAsync(ct);
            return;
        }

        // v2.6.0：新战斗 → 清空集火目标（避免上一场的敌人名残留
        // 造成"打了半天才发现目标早没了"的粘滞）
        _focusTargetName = null;

        int turnGuard = 0;
        int lastPlayedTurn = 0;
        while (CombatManager.Instance.IsInProgress && turnGuard++ < 200)
        {
            Pilot.MarkProgress();
            await Pilot.WaitIfPausedAsync(ct);
            if (ct.IsCancellationRequested || !Pilot.IsRunning) return;

            // 等待进入本回合出牌阶段
            bool ready = await WaitForPlayableTurnAsync(me, lastPlayedTurn, ct);
            if (!ready) break;

            int turn = me.PlayerCombatState.TurnNumber;
            lastPlayedTurn = turn;
            Pilot.Log($"回合 {turn}：{DescribeBattlefield()}");

            await MainThreadRunner.DelayAsync(PilotConfig.Current.TurnDelayMs, ct);

            if (PilotConfig.Current.AutoUsePotions) await UsePotionsSmartAsync(me, ct);

            // ---- 大模型回合规划：一次问清楚“这一回合依次出哪些牌、打谁” ----
            List<PlannedPlay> plan = null;
            HashSet<CardModel> plannedCards = new();
            int plannedTurn = -1;
            if (UseLlm)
            {
                List<CardModel> handNow = GetPlayableCards(me, new HashSet<CardModel>());
                plan = await LlmPlanTurnAsync(me, handNow, ct);
                if (plan != null)
                {
                    plannedCards = new HashSet<CardModel>(plan.Select(p => p.Card));
                    plannedTurn = turn;
                }
            }

            int played = 0;
            var attempted = new HashSet<CardModel>();
            while (played < 60)
            {
                Pilot.MarkProgress();
                await Pilot.WaitIfPausedAsync(ct);
                if (ct.IsCancellationRequested) return;
                if (me.PlayerCombatState == null ||
                    me.PlayerCombatState.Phase != PlayerTurnPhase.Play ||
                    me.PlayerCombatState.TurnNumber != turn) break;

                // ---- v2.4.3 战斗内浮层守卫（血泪）--------------------------------
                // 战斗中也可能弹出**需要交互的浮层**（典型：技能/攻击药水 → NChooseACardSelectionScreen）。
                // 这类浮层的 await 会挂起一个 PlayerChoice，并调用 NPlayerHand.CancelAllCardPlay() 取消
                // 队列里所有待执行的出牌动作；此时若我们继续入队出牌，PlayCardAction.CancelAction()
                // 会在 NPlayerHand.Add() 里抛 ArgumentOutOfRangeException → **战斗回合循环直接死亡**
                // （日志铁证：`Combat #1 turn loop died ... the combat is stuck until the room is restarted`）。
                // 所以出牌前必须先检查并处理掉这些浮层。
                if (await HandleCombatOverlayAsync(ct)) continue;

                List<CardModel> playable = GetPlayableCards(me, attempted);
                if (playable.Count == 0) break;

                CardModel pick;
                Creature target;
                string why;

                if (plan != null && plan.Count > 0)
                {
                    // v2.5.4 血泪修复：**不要把整份计划丢掉**。
                    // 旧实现只要当前这一步"打不出"（能量不够 / 该牌已不在手牌 / 已被消耗），
                    // 就 `plan = null` 整份作废，退化为本地单卡贪心 —— 于是 LLM 精心排好的
                    // "易伤铺垫 → 攻击输出 → 斩杀"顺序在第一个不可打步骤就全废，
                    // 后续变成"谁分高打谁"，铺垫牌可能被永远压后 = 出牌顺序大乱的根因。
                    //
                    // 正确做法：
                    //   ① 先在本步找"计划中还没出过、当前可打"的下一步 → 正常执行；
                    //   ② 找不到 → **跳过**那些暂时打不出的步骤（它们可能因为能量不足或被消耗），
                    //      继续在计划里往后找第一个可打的步骤；
                    //   ③ 只有当计划里**再也没有任何可打步骤**时，才认为计划耗尽。
                    var step = plan.FirstOrDefault(p => !p.Done && playable.Contains(p.Card)
                                                        && SafeCostOf(p.Card) <= CurrentEnergy(me));
                    if (step == null)
                    {
                        // 跳过"已不可能再打"的步骤（牌已不在手牌），保留仍可能打的
                        int pruned = plan.RemoveAll(p => !p.Done && !playable.Contains(p.Card));
                        if (pruned > 0)
                            Explain($"🧹 计划修剪：移除 {pruned} 个已离手步骤，保留 {plan.Count(p => !p.Done)} 个待打");
                        step = plan.FirstOrDefault(p => !p.Done && playable.Contains(p.Card)
                                                        && SafeCostOf(p.Card) <= CurrentEnergy(me));
                    }

                    if (step != null)
                    {
                        step.Done = true;
                        pick = step.Card;
                        if (step.TargetIndex.HasValue)
                        {
                            target = EnemyByIndex(me, step.TargetIndex.Value);
                            // v2.6.0：LLM 显式指定的目标 = 本回合集火目标，后续多目标牌自动跟上
                            if (target != null) SetFocusTarget(target);
                        }
                        else target = ChooseTarget(pick, me);
                        why = "LLM计划" + (string.IsNullOrEmpty(step.Note) ? "" : $"：{step.Note}");
                    }
                    else
                    {
                        // 计划里已无可打步骤 —— 检查是否只是"暂时能量不够"
                        bool anyPendingAffordableLater = plan.Any(p => !p.Done && playable.Contains(p.Card));
                        if (anyPendingAffordableLater)
                        {
                            // 还有牌在计划里但当前能量不足 → 保留计划，让本轮本地补齐，
                            // 下轮能量/抽牌变化后仍可回到计划顺序（不再整份作废）
                            Explain($"⏳ 计划中仍有待打牌但当前能量不足（剩 {CurrentEnergy(me)}）→ 保留计划待下轮");
                            plan = null;   // 本轮先走本地，但已记入日志；下轮会重规划
                        }
                        else
                        {
                            plan = null;   // 计划确实耗尽
                        }
                        goto localPick;
                    }
                }
                else
                {
                    // 本地补齐：新抽到的牌（不在原计划里）优先交给 LLM 重规划一次
                    if (UseLlm && plannedTurn == turn && played > 0)
                    {
                        bool hasNewCards = playable.Any(c => !plannedCards.Contains(c) && !attempted.Contains(c));
                        bool enoughEnergy = CurrentEnergy(me) >= 1;
                        if (hasNewCards && enoughEnergy)
                        {
                            var replan = await LlmPlanTurnAsync(me, playable, ct);
                            if (replan != null && replan.Count > 0)
                            {
                                Explain($"🧠 LLM 重规划（抽到新牌，剩 {CurrentEnergy(me)} 能量）");
                                var first = replan.FirstOrDefault(p => !p.Done);
                                if (first != null)
                                {
                                    first.Done = true;
                                    pick = first.Card;
                                    if (first.TargetIndex.HasValue)
                                    {
                                        target = EnemyByIndex(me, first.TargetIndex.Value);
                                        if (target != null) SetFocusTarget(target);
                                    }
                                    else target = ChooseTarget(pick, me);
                                    why = "LLM重规划" + (string.IsNullOrEmpty(first.Note) ? "" : $"：{first.Note}");
                                    plan = replan;
                                    goto afterPick;
                                }
                            }
                        }
                    }
                    goto localPick;
                }

                // 需要先给 pick 赋值才继续（本地分支跳转）
                goto afterPick;

            localPick:
                pick = ChooseCard(playable, me, out why);
                target = ChooseTarget(pick, me);

            afterPick:
                if (pick == null) break;
                attempted.Add(pick);

                int energyBefore = CurrentEnergy(me);
                Explain($"出牌 {pick.Id.Entry}（{why}）" + (target != null ? $" → {target.Name}" : "") +
                        $"  能量 {energyBefore}");
                try
                {
                    await ManualPlayAsync(me, pick, target, ct);
                }
                catch (Exception e)
                {
                    Pilot.Warn($"出牌 {pick.Id.Entry} 失败: {e.Message}");
                    break;
                }

                int energyAfter = CurrentEnergy(me);
                // TryManualPlay 是“入队”语义：真实扣费发生在入队动作被队列执行时。
                // 立刻读能量会拿到旧值（实测偶发误报“能量未减少”），故先等队列把能量扣完再判定。
                if (energyAfter >= energyBefore)
                {
                    await WaitEnergyDropAsync(me, energyBefore, ct);
                    energyAfter = CurrentEnergy(me);
                }
                if (energyAfter >= energyBefore && SafeCostOf(pick) > 0)
                    Pilot.Warn($"注意：{pick.Id.Entry} 打出后能量未减少（{energyBefore}→{energyAfter}），可能未真实扣费");

                played++;
                await MainThreadRunner.DelayAsync(PilotConfig.Current.ActionDelayMs, ct);
            }

            if (!CombatManager.Instance.IsInProgress) break;

            // 结束回合（先等出牌队列跑完——TryManualPlay 是入队的，能量扣减有延迟）
            if (me.PlayerCombatState != null &&
                me.PlayerCombatState.Phase == PlayerTurnPhase.Play &&
                me.PlayerCombatState.TurnNumber == turn)
            {
                await WaitQueueIdleAsync(ct);
                Explain($"回合 {turn} 结束（已出 {played} 张，剩 {CurrentEnergy(me)} 能量）");
                try { PlayerCmd.EndTurn(me, canBackOut: false); }
                catch (Exception e) { Pilot.Warn("结束回合失败: " + e.Message); }
            }
            await MainThreadRunner.DelayAsync(300, ct);
        }

        await WaitCombatEndAsync(ct);
        Pilot.CombatsWon++;
        Explain($"战斗结束（累计 {Pilot.CombatsWon} 场）");
    }

    /// <summary>
    /// 等待能量从 before 掉下来（最多 ~1.2s）。TryManualPlay 只是入队，真正扣费在队列执行时发生，
    /// 立刻读会拿到旧值。此等待仅用于让“是否真实扣费”的判定准确，不影响出牌时序。
    /// </summary>
    private static async Task WaitEnergyDropAsync(Player me, int before, CancellationToken ct)
    {
        for (int i = 0; i < 12; i++)
        {
            if (ct.IsCancellationRequested) return;
            if (!CombatManager.Instance.IsInProgress) return;
            await MainThreadRunner.DelayAsync(100, ct);
            try
            {
                if (me?.PlayerCombatState == null) return;
                if (me.PlayerCombatState.Energy < before) return; // 已扣费
            }
            catch { return; }
        }
    }

    /// <summary>等待“还有可打的牌 + 还有能量”这个状态稳定下来，避免队列未跑完就结束回合。</summary>
    private static async Task WaitQueueIdleAsync(CancellationToken ct)
    {
        // 出牌是入队异步执行的：等能量/手牌稳定，或短超时
        for (int i = 0; i < 20; i++)
        {
            if (ct.IsCancellationRequested) return;
            if (!CombatManager.Instance.IsInProgress) return;
            await MainThreadRunner.DelayAsync(100, ct);
            try
            {
                Player me = Pilot.Me;
                if (me?.PlayerCombatState == null) return;
                if (me.PlayerCombatState.Phase != PlayerTurnPhase.Play) return;
                int energy = me.PlayerCombatState.Energy;
                var hand = PileType.Hand.GetPile(me)?.Cards;
                bool anyPlayable = hand != null && hand.Any(c => c != null && c.CanPlay(out _, out _) && GameState.SafeCost(c) <= energy);
                if (!anyPlayable) return; // 没有可打的了，队列多半已跑完
            }
            catch { return; }
        }
    }

    private static async Task<bool> WaitForPlayableTurnAsync(Player me, int lastTurn, CancellationToken ct)
    {
        for (int i = 0; i < 300; i++)
        {
            if (ct.IsCancellationRequested) return false;
            if (!CombatManager.Instance.IsInProgress) return false;
            await Pilot.WaitIfPausedAsync(ct);
            PlayerCombatState st = me.PlayerCombatState;
            if (st != null && st.Phase == PlayerTurnPhase.Play && st.TurnNumber > lastTurn) return true;
            await MainThreadRunner.DelayAsync(100, ct);
        }
        return false;
    }

    // =====================================================================
    //  大模型回合规划（combat）
    // =====================================================================

    /// <summary>大模型给出的一步出牌计划。</summary>
    private sealed class PlannedPlay
    {
        public CardModel Card;
        public int? TargetIndex;   // 1-based 敌人序号，null=自动选
        public string Note;
        public bool Done;
    }

    /// <summary>把敌人序号(1-based)映射回 Creature。</summary>
    private static Creature EnemyByIndex(Player me, int oneBased)
    {
        try
        {
            var enemies = GameState.HittableEnemies(me);
            int i = oneBased - 1;
            return (i >= 0 && i < enemies.Count) ? enemies[i] : null;
        }
        catch { return null; }
    }

    /// <summary>当前剩余能量。</summary>
    private static int CurrentEnergy(Player me)
    {
        try { return me?.PlayerCombatState?.Energy ?? 0; }
        catch { return 0; }
    }

    /// <summary>卡牌当前真实费用。</summary>
    private static int SafeCostOf(CardModel c) => GameState.SafeCost(c);

    /// <summary>
    /// 真正“像玩家一样”打出一张牌 —— 会消耗能量。
    ///
    /// ⚠️ 血泪教训：<c>CardCmd.AutoPlay</c> 是**免费出牌**接口（它把 ResourceInfo.EnergySpent 硬编码为 0，
    /// 且不调用 SpendResources），官方 AutoSlayer 就是用它，所以官方自动测试才会“不扣费”。
    /// 玩家手点出牌走的是 <c>CardModel.EnqueueManualPlay</c> → <c>PlayCardAction</c> → <c>SpendResources()</c>。
    /// 这里用官方的公开入口 <c>TryManualPlay</c>（内部同一个 PlayCardAction 队列），能量正常扣除、动画正常播。
    /// </summary>
    private static Task ManualPlayAsync(Player me, CardModel card, Creature target, CancellationToken ct)
    {
        if (card == null) return Task.CompletedTask;

        // 目标校验：AnyEnemy 必须给目标，否则官方会走随机目标/直接不执行
        if (card.TargetType == TargetType.AnyEnemy && target == null)
            target = ChooseTarget(card, me);
        if (card.TargetType == TargetType.AnyEnemy && target == null)
        {
            Pilot.Warn("ManualPlay: 无有效敌人目标");
            return Task.CompletedTask;
        }

        // 优先走官方“手动出牌”入口（真实扣费）
        if (card.TryManualPlay(target)) return Task.CompletedTask;

        // 兜底：判一次可打性并报错，绝不静默改成免费出牌
        try
        {
            card.CanPlay(out var reason, out _);
            Pilot.Warn($"TryManualPlay 返回 false（{reason}）：{card.Id.Entry}");
        }
        catch { }
        return Task.CompletedTask;
    }

    /// <summary>
    /// 问大模型“这一回合怎么打”。返回按顺序的出牌计划；失败返回 null（调用方回退本地）。
    /// </summary>
    private static async Task<List<PlannedPlay>> LlmPlanTurnAsync(Player me, List<CardModel> hand, CancellationToken ct)
    {
        try
        {
            if (hand == null || hand.Count == 0) return null;

            string system =
                "你是《杀戮尖塔2》(Slay the Spire 2) 的顶尖自动对战AI。你的任务：在当前回合用最合理的方式打赢战斗。\n" +
                StsKnowledge.Compose(StsKnowledge.Combat, StsKnowledge.EnergyRules) +
                CardMechanics.PromptRules +
                "【硬约束——必须遵守】\n" +
                "A) 能量是有限资源。所有**费用 >= 1** 的牌，其费用总和不得超过当前能量。\n" +
                "   规划时必须自己累加费用做校验；接近能量上限时，优先把能量花在收益最高的牌上。\n" +
                "A2) **法术力曲线校验（v2.5.4 新增，务必执行）**：因为出牌是**按顺序**一个个消耗能量的，\n" +
                "   你必须按 plan 的顺序**逐张累加**费用，确保**每一个前缀和**都不超过总能量。\n" +
                "   例如能量 3、计划 [1费, 2费, 1费]：累加为 1、3、4 —— 第 3 步前缀和 4 > 3，**非法**。\n" +
                "   正确做法是把第 3 步换成 0 费牌，或干脆不要它，而不是把 3 张都写上去。\n" +
                "   请在每步的 note 里附上该步执行后的**剩余能量**，格式如 \"剩2\"，便于自查。\n" +
                "B) 打出的顺序会影响结果（例如：先用易伤/虚弱/减益类铺垫，再打攻击牌才能吃到加成）。\n" +
                "   请把顺序排好：0费/过牌/充能 → 铺垫牌 → 伤害牌。\n" +
                "   注意：若某张铺垫牌是按顺序才能吃到加成的，它**必须**排在受益牌之前，\n" +
                "   且要为它预留能量（不要把它排到能量已经花完的位置）。\n" +
                "C) 只能从【手牌】里选择，序号必须与手牌编号一致。标记“当前不可打出”的牌不许选。\n" +
                "D) 敌人序号必须与【敌人】里的 # 编号一致；对不需要目标的牌填 target=0。\n" +
                "E) 【生存第一，硬约束】下方【逐回合威胁时间线】已把致死结论算好，**必须服从**：\n" +
                "   · 出现「🩸🩸 致死预警」时：plan 的**前若干步必须是格挡/减伤牌**，\n" +
                "     必须把格挡叠到至少「本回合承伤 − 已有格挡」点；在凑够之前**禁止安排任何攻击/铺垫牌**。\n" +
                "   · 出现「🩸 高风险」时：至少要安排 1 张格挡牌，剩余能量才可用于输出。\n" +
                "   · 出现「✅ 格挡已够」时：不要浪费出牌机会叠格挡（除非该格挡牌附带过牌/额外收益），全力输出。\n" +
                "   · 血量低于 40% 时，任何「不挡会死」的回合都**优先保命**，不要赌。\n" +
                "   反例（禁止）：血量 10、本回合承伤 20，却安排 [攻击, 攻击] —— 这会直接暴毙。\n" +
                "E2) 【怪物机制优先于当前数字（v2.6.0 新增，硬约束）】下方【本场敌人的特殊机制】写明了这些\n" +
                "   敌人的联动/触发规则，**它们会改变你对承伤与目标的判断**，必须遵守：\n" +
                "   · 若机制写明「击杀某一只会让其余进入休眠/不行动」，那么**主动先杀那一只**可换来一个\n" +
                "     「承伤=0 的回合」——**那个回合不要打格挡牌**，把能量全部用于输出/铺垫/能力牌。\n" +
                "     反之，当敌人**本回合正在休眠/无任何攻击意图**（时间线里本回合承伤=0）时，\n" +
                "     **绝不要打格挡牌**，那纯属浪费；应全力输出。\n" +
                "   · 若机制写明敌人会给你叠「脆弱(Frail)」，你打出的格挡牌按 **×0.75** 折算，\n" +
                "     算够不够挡时必须用折算后的值，别用面板值。\n" +
                "   · 若机制写明「每死一只其余变强(加力量)」，就要**尽快清场**、别拖回合平均输出。\n" +
                "F) 能一击斩杀某敌人时优先斩杀，别把伤害平均摊到多个敌人身上。\n" +
                "F2) 【目标一致性，v2.6.0 硬约束】\n" +
                "   · 你为**每张带目标的牌显式指定 target**（敌人序号），系统会严格按你给的目标执行，\n" +
                "     不会替你改目标。所以你必须**自己保证同一回合内集中火力**：\n" +
                "     一旦决定打某只敌人（例如已给它挂易伤/虚弱），**后续输出牌就继续打它**，\n" +
                "     不要中途改打另一只（除非它已死、或另一只能被你斩杀）。\n" +
                "   · 铺垫牌(易伤/虚弱/中毒)挂给谁，输出就该跟着打谁 —— 否则铺垫白费。\n" +
                "   · 不要给 A 挂易伤却去打 B（这是严重失误）。\n" +
                "G) 除非手上牌全都不值得出，否则 plan 不要为空。0 费牌永远值得出。\n" +
                "I) 【已验证候选方案（v2.7.0 新增，硬约束）】下方【本回合已验证候选方案】是系统**穷举了\n" +
                "   本回合所有出牌次序与目标分配**后、用数学评估（生存优先：死亡罚分 −1e12、每点生命 ×30000、\n" +
                "   敌方每点血 −10000）算出的结果，并已按分数排序。必须这样用：\n" +
                "   · ★最优候选**可以直接照抄**（顺序与目标一起抄），这是被验证过的最优解；\n" +
                "   · 你要改它，必须确信你的方案**能超过它的分数**（通常是：多留了血、多打了伤害、\n" +
                "     或达成了清场），并在 reason 里写明差在哪；\n" +
                "   · 候选里标注「→#N敌人」的目标就是系统算好的集火对象 —— **请把相同序号写进你的 plan.target**；\n" +
                "   · 若某个候选被标为「❌会死」，**绝不要采用它的顺序**。\n" +
                "H) 【条件机制牌】若下方【本轮条件机制提示】把某张牌标为“⚠此时无价值”，**禁止选它**。" +
                "   典型：当前格挡为 0 时打全身撞击 = 0 伤害；目标无中毒时打催化 = 无效。\n" +
                "【决策优先级】生存 > 斩杀 > 高价值能力牌(Power) > 铺垫(易伤/虚弱) > 输出 > 过牌\n" +
                "只输出 JSON，禁止任何解释文字。格式：\n" +
                "{\"plan\":[{\"card\":手牌序号,\"target\":敌人序号或0,\"note\":\"不超过10字的理由\"}]," +
                "\"energy_check\":本回合计划中费用>=1的牌总费用,\"reason\":\"本回合总体思路，不超过20字\"}\n" +
                "plan 按出牌先后排列。";

            string mechHint = CardMechanics.DescribeHandMechanics(hand, me);

            // ---- v2.7.0 当前回合穷举（移植 CombatSolver 评估函数 + 只算本回合）----
            // 把"手牌可打顺序 × 目标分配"全部枚举一遍，用 SolverWeights 打分，取 Top-N
            // 作为【已验证候选】注入提示词 —— 让 LLM 在算过的候选里挑/校验，而不是自由发挥。
            // 全程只读、不写游戏状态；异常静默忽略，绝不影响出牌。
            string lookaheadBlock = "";
            try
            {
                var la = CombatLookahead.Compute(me, hand);
                if (la != null)
                {
                    lookaheadBlock = CombatLookahead.Prompt(la, hand);
                    string sum = CombatLookahead.Summary(la);
                    if (!string.IsNullOrEmpty(sum) && PilotConfig.Current.Verbose) Pilot.Log(sum);
                    if (la.Best != null) Explain($"🔮 {sum}");
                }
            }
            catch (Exception lex) { Pilot.Warn("前瞻模块异常（忽略）: " + lex.Message); }

            string user =
                "当前对局局面如下（手牌左侧【费用N】即打出该牌消耗的能量；费用0=不耗能量，必打）：\n" +
                GameState.DescribeCombat(me, hand) + "\n" +
                // v2.5.5：把「血量 + 格挡 + 每个敌人逐回合状态」整合成显式结论，含致死判定
                GameState.DescribeThreatTimeline(me) + "\n" +
                (string.IsNullOrEmpty(lookaheadBlock) ? "" : lookaheadBlock + "\n") +
                (string.IsNullOrEmpty(mechHint) ? "" :
                    "【本轮条件机制提示】（这些牌的**实际效果取决于当前局面**，请严格遵守）\n" + mechHint + "\n") +
                $"请给出本回合最佳出牌顺序：费用>=1的牌总和不得超过 {CurrentEnergy(me)} 点能量（0费牌不计数），" +
                $"最多 {Math.Min(hand.Count, 12)} 步。先打0费/过牌，再铺垫后输出、集中火力斩杀。";

            string answer = await LlmClient.AskAsync(system, user, ct);
            if (string.IsNullOrEmpty(answer)) return null;

            var dict = LlmClient.ParseJsonObject(answer);
            if (dict == null) return null;

            if (!dict.TryGetValue("plan", out System.Text.Json.JsonElement planEl) ||
                planEl.ValueKind != System.Text.Json.JsonValueKind.Array)
                return null;

            var result = new List<PlannedPlay>();
            int budget = CurrentEnergy(me);
            int spent = 0;
            bool overBudget = false;

            // v2.5.4：不再"超预算就丢弃该步"。旧做法会把模型排好的顺序剪碎
            // （比如 [1费铺垫, 2费输出] 在 3 能量下合法，但若模型多写一张就会被截断）。
            // 新做法分两步：① 先把所有合法步骤按模型顺序**原样收集**；
            //              ② 若整体超预算，则按"曲线可执行性"**重排 + 裁剪**，
            //                 保证每个前缀和都 ≤ 能量，且高价值步骤优先留下。
            var raw = new List<PlannedPlay>();
            foreach (System.Text.Json.JsonElement item in planEl.EnumerateArray())
            {
                if (item.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
                int? cardNo = null, targetNo = null;
                string note = "";
                if (item.TryGetProperty("card", out var cEl))
                {
                    if (cEl.ValueKind == System.Text.Json.JsonValueKind.Number && cEl.TryGetInt32(out int cn)) cardNo = cn;
                    else if (cEl.ValueKind == System.Text.Json.JsonValueKind.String && int.TryParse(cEl.GetString(), out int cn2)) cardNo = cn2;
                }
                if (item.TryGetProperty("target", out var tEl))
                {
                    if (tEl.ValueKind == System.Text.Json.JsonValueKind.Number && tEl.TryGetInt32(out int tn)) targetNo = tn;
                    else if (tEl.ValueKind == System.Text.Json.JsonValueKind.String && int.TryParse(tEl.GetString(), out int tn2)) targetNo = tn2;
                }
                if (item.TryGetProperty("note", out var nEl) && nEl.ValueKind == System.Text.Json.JsonValueKind.String)
                    note = nEl.GetString();

                if (cardNo == null || cardNo < 1 || cardNo > hand.Count) continue;
                CardModel card = hand[cardNo.Value - 1];
                if (card == null) continue;

                raw.Add(new PlannedPlay
                {
                    Card = card,
                    TargetIndex = (targetNo.HasValue && targetNo.Value > 0) ? targetNo : null,
                    Note = note,
                });
            }

            // ① 先按模型给的原顺序做前缀和校验
            bool feasible = true;
            int acc = 0;
            foreach (var st in raw)
            {
                int c = Math.Max(0, SafeCostOf(st.Card));
                acc += c;
                if (acc > budget) { feasible = false; break; }
            }

            if (feasible)
            {
                result = raw;
                spent = acc;
            }
            else
            {
                // ② 曲线不可行 → 先尝试**保序重排**：把 0 费牌全部提到最前（零费不占能量，提前无损失），
                //    再在非零费牌里做贪心"放得下就放"，放不下的记录为被裁剪。
                //    这样既保住了铺垫→输出的相对顺序，又满足前缀约束。
                overBudget = true;
                var zeroCost = new List<PlannedPlay>();
                var paid = new List<PlannedPlay>();
                foreach (var st in raw)
                    (SafeCostOf(st.Card) <= 0 ? zeroCost : paid).Add(st);

                var ordered = new List<PlannedPlay>(zeroCost);
                acc = 0;
                foreach (var st in paid)
                {
                    int c = Math.Max(0, SafeCostOf(st.Card));
                    if (acc + c > budget) continue;      // 这一步在当前顺序下放不下 → 裁剪
                    acc += c;
                    ordered.Add(st);
                }
                result = ordered;
                spent = acc;

                Explain($"🔧 计划曲线不可行 → 已重排（0费前置 {zeroCost.Count} 张，保留 {result.Count}/{raw.Count} 步，耗能 {spent}/{budget}）");
            }

            string overall = LlmClient.GetStr(dict, "reason") ?? "";
            int? claimed = LlmClient.GetInt(dict, "energy_check");
            Explain($"🧠 LLM 回合规划：{result.Count} 步 / 耗能 {spent}/{budget}" +
                    (overBudget ? "（模型超预算，已裁剪）" : "") +
                    (claimed.HasValue && claimed.Value != spent ? $"（模型自报 {claimed}）" : "") +
                    (string.IsNullOrEmpty(overall) ? "" : $"（{overall}）"));
            if (PilotConfig.Current.Verbose && result.Count > 0)
                Pilot.Log("LLM 计划: " + string.Join(" → ", result.Select(r => SafeTitle(r.Card))) + $"  [耗能 {spent}/{budget}]");
            return result;
        }
        catch (Exception e)
        {
            Pilot.Warn("LLM 回合规划异常（回退本地）: " + e.Message);
            return null;
        }
    }

    private static async Task WaitCombatEndAsync(CancellationToken ct)
    {
        for (int i = 0; i < 400; i++)
        {
            if (ct.IsCancellationRequested) return;
            if (!CombatManager.Instance.IsInProgress) return;
            await MainThreadRunner.DelayAsync(100, ct);
        }
    }

    /// <summary>
    /// v2.4.3：战斗中的**浮层守卫**。战斗内也会弹出需要交互的浮层，最典型的是
    /// 药水触发的「选择一张牌」（见 `SkillPotion.OnUse` → `CardSelectCmd.FromChooseACardScreen`
    /// → `NChooseACardSelectionScreen`）。若放任不管：
    ///   ① 该浮层的 `CardsSelected()` 永不返回 → 药水 OnUse 挂起 → 本回合后续流程全部阻塞；
    ///   ② 它调用了 `NPlayerHand.CancelAllCardPlay()` 取消待执行的出牌动作 → 我们再入队出牌就会
    ///      在 `PlayCardAction.CancelAction()` 里抛 `ArgumentOutOfRangeException` → **战斗回合循环死亡**。
    /// 因此出牌循环每轮先来这里把它处理掉（交给 ScreenRouter 的通用分发），处理完再继续出牌。
    /// 返回 true 表示"本轮处理了浮层，应跳过本次出牌"。
    /// </summary>
    private static async Task<bool> HandleCombatOverlayAsync(CancellationToken ct)
    {
        try
        {
            NOverlayStack stack = NOverlayStack.Instance;
            if (stack == null) return false;
            // 战斗内的浮层允许反复尝试（HandleTopAsync 的 _sameTopClicks 是给主循环空转防死循环用的）
            ScreenRouter.ResetTopClickGuard();
            for (int guard = 0; guard < 6; guard++)
            {
                IOverlayScreen top = stack.Peek();
                if (top == null) return guard > 0;
                if (top is Node n && !GodotObject.IsInstanceValid(n)) return guard > 0;

                // 只处理"战斗中会挡出牌"的那几类浮层；其它交回主循环的房间级分发，
                // 避免在这里误点掉本该由主循环按房间逻辑处理的屏。
                bool combatBlocking =
                    top is MegaCrit.Sts2.Core.Nodes.Screens.CardSelection.NChooseACardSelectionScreen ||
                    top is MegaCrit.Sts2.Core.Nodes.Screens.CardSelection.NCombatPileCardSelectScreen ||
                    top is MegaCrit.Sts2.Core.Nodes.Screens.CardSelection.NSimpleCardSelectScreen ||
                    top is MegaCrit.Sts2.Core.Nodes.Screens.CardSelection.NChooseABundleSelectionScreen ||
                    top is MegaCrit.Sts2.Core.Nodes.Screens.NChooseARelicSelection;
                if (!combatBlocking) return guard > 0;

                bool acted;
                try { acted = await ScreenRouter.HandleTopAsync(top, ct); }
                catch (Exception e) { Pilot.Warn("处理战斗内浮层失败: " + e.Message); acted = false; }

                if (!acted)
                {
                    Pilot.Warn($"战斗内浮层 {top.GetType().Name} 无处理器，跳过（继续出牌）");
                    return guard > 0;
                }
                Explain($"⚔ 处理战斗中浮层 {top.GetType().Name}");
                await MainThreadRunner.DelayAsync(150, ct);
            }
        }
        catch (Exception e)
        {
            Pilot.Warn("战斗内浮层守卫异常: " + e.Message);
        }
        return false;
    }

    /// <summary>当前是否压着"会挡出牌"的战斗内浮层（供用药水后轮询等待用）。</summary>
    private static async Task<bool> HasCombatBlockingOverlayAsync()
    {
        try
        {
            await Task.CompletedTask;
            NOverlayStack stack = NOverlayStack.Instance;
            if (stack == null) return false;
            IOverlayScreen top = stack.Peek();
            if (top == null) return false;
            return top is MegaCrit.Sts2.Core.Nodes.Screens.CardSelection.NChooseACardSelectionScreen
                || top is MegaCrit.Sts2.Core.Nodes.Screens.CardSelection.NCombatPileCardSelectScreen
                || top is MegaCrit.Sts2.Core.Nodes.Screens.CardSelection.NSimpleCardSelectScreen
                || top is MegaCrit.Sts2.Core.Nodes.Screens.CardSelection.NChooseABundleSelectionScreen
                || top is MegaCrit.Sts2.Core.Nodes.Screens.NChooseARelicSelection;
        }
        catch { return false; }
    }

    private static List<CardModel> GetPlayableCards(Player me, HashSet<CardModel> attempted)
    {
        try
        {
            CardPile pile = PileType.Hand.GetPile(me);
            if (pile?.Cards == null) return new List<CardModel>();
            return pile.Cards
                .Where(c => c != null && !attempted.Contains(c) && c.CanPlay(out _, out _))
                .ToList();
        }
        catch
        {
            return new List<CardModel>();
        }
    }

    /// <summary>
    /// 核心出牌决策：给每张可打牌打分，取最高分。
    /// 评分维度见 <see cref="ScoreCard"/>。
    ///
    /// v2.3.6 修正：
    ///   ① 先过滤掉**费用超过当前能量**的牌（CanPlay 已过滤，这里是二次保险）——
    ///      防止 scoring 误把付不起的牌选出来。
    ///   ② 全 0 费或全负分时不再返回 null，而是选“当前能量能负担的最高分牌”。
    /// </summary>
    private static CardModel ChooseCard(List<CardModel> playable, Player me, out string reason)
    {
        CardModel best = null;
        double bestScore = double.MinValue;
        string bestWhy = "";
        int energy = CurrentEnergy(me);

        foreach (CardModel c in playable)
        {
            if (c == null) continue;
            try { if (GameState.SafeCost(c) > energy) continue; } catch { }
            double s = ScoreCard(c, me, out string why);
            if (s > bestScore)
            {
                bestScore = s; best = c; bestWhy = why;
            }
        }
        reason = best == null ? "无可打牌" : bestWhy;
        return best;
    }

    /// <summary>
    /// 单张卡评分。分数越高越先出。
    /// 这是可调策略的核心，注释里标出每一项的意图，方便玩家按自己理解微调。
    /// </summary>
    private static double ScoreCard(CardModel c, Player me, out string why)
    {
        double score = 0;
        var reasons = new List<string>();

        try
        {
            int energy = me.PlayerCombatState?.Energy ?? 0;
            int cost = 0;
            try { cost = c.EnergyCost.GetWithModifiers(CostModifiers.Local); } catch { cost = 0; }
            if (cost < 0) cost = 0;

            // 残血程度
            int hp = me.Creature?.CurrentHp ?? 1;
            int maxHp = me.Creature?.MaxHp ?? 1;
            double hpRatio = maxHp > 0 ? (double)hp / maxHp : 1.0;

            // ---- 敌人威胁：本回合会挨多少打 ----
            int incoming = EstimateIncomingDamage(me);
            int myBlock = GetMyBlock(me);
            // v2.6.1：**本回合承伤为 0**（敌人全体休眠/眩晕/无攻击意图，如往生雕像首回合睡觉、
            // 蛞蝓被同伴死亡触发休眠）→ 打格挡牌纯属浪费，应全力输出。
            bool noThreatThisTurn = incoming <= 0;
            // v2.6.1：**虚弱(Frail)折扣**——身上有脆弱时，打出的格挡按 ×0.75 计。
            // 用于把"看起来够挡"的错觉打掉（蛞蝓 GOOP / 信徒 / 幽灵船都会叠）。
            double frailMul = HasFrail(me) ? 0.75 : 1.0;
            // v2.5.5 修正：原来用 `>` —— 承伤恰好等于「血+格挡」时（如血 10、承伤 10）
            // 也会被打死，必须用 `>=`。差一个等号就是一条命。
            bool lethalRisk = incoming >= myBlock + hp;     // 不防会死
            bool lethalEvenBlocked = incoming >= hp;        // 完全不出格挡牌必然掉光血
            bool blockCoversAll = incoming <= myBlock;      // 现有格挡已够，无需再防
            bool risky = incoming > myBlock + hp * 0.4;     // 掉血很痛
            bool needDefense = hpRatio * 100 < PilotConfig.Current.DefensiveHpPercent || risky;

            // ---- 敌人总血量 / 可斩杀 ----
            int enemyHp = TotalEnemyHp(me);
            int myDamage = EstimateCardDamage(c, me);

            CardType type = c.Type;
            double baseChance = hpRatio;

            switch (type)
            {
                case CardType.Attack:
                    if (!PilotConfig.Current.AllowAttack) { why = "已禁用攻击"; return -999; }
                    score += 50;
                    reasons.Add("攻击");
                    // 能斩杀 → 极大加分（减少本回合承受的伤害）
                    if (myDamage > 0 && myDamage >= enemyHp)
                    {
                        score += 500;
                        reasons.Add("可斩杀");
                    }
                    // v2.5.5：致死回合里**攻击牌大幅降权**。
                    // 旧值只扣 25 分，而格挡牌在 lethalRisk 时 +400 → 看似够，但一旦
                    // 手牌里没有格挡牌，攻击牌仍会被当最优解打出去 → 暴毙（用户实测 11 层）。
                    // 现在：必死回合里攻击牌除非**能斩杀**，否则重罚。
                    if (lethalRisk || lethalEvenBlocked)
                    {
                        bool canKill = myDamage > 0 && myDamage >= enemyHp;
                        if (!canKill) { score -= 600; reasons.Add("必死回合禁输出"); }
                        else { score += 50; reasons.Add("斩杀可解围"); }
                    }
                    else if (needDefense)
                    {
                        score -= 25;
                        reasons.Add("残血降权");
                    }
                    break;

                case CardType.Power:
                    // 能力牌越早越值钱（滚雪球）
                    score += 80;
                    reasons.Add("能力牌");
                    if (lethalRisk || lethalEvenBlocked) { score -= 300; reasons.Add("必死回合降权"); }
                    break;

                case CardType.Skill:
                    score += 40;
                    reasons.Add("技能");
                    int block = EstimateCardBlock(c, me);
                    if (block > 0)
                    {
                        // v2.6.1 机制修正①：本回合无任何威胁（敌人全体休眠/眩晕/不攻击）→
                        // 打格挡牌纯属浪费，重罚，把行动让给输出/铺垫/能力牌。
                        if (noThreatThisTurn) { score -= 300; reasons.Add("本回合无威胁禁格挡"); }
                        // 格挡牌：只在真的需要挡的时候加分
                        else if (lethalRisk) { score += 400; reasons.Add("救命格挡"); }
                        else if (lethalEvenBlocked) { score += 200; reasons.Add("必需格挡"); }
                        else if (needDefense) { score += 120; reasons.Add("需要格挡"); }
                        else { score += 20; reasons.Add("格挡"); }
                        // v2.6.1 机制修正②：身上有脆弱(Frail)时，这张格挡实际只值 ×0.75，
                        // 若折算后已经不够挡住本回合承伤 → 降权（避免"看着够其实不够"的错觉）。
                        if (!noThreatThisTurn && frailMul < 1.0 && block > 0 && !lethalRisk)
                        {
                            int effBlock = (int)Math.Floor(block * frailMul);
                            if (myBlock + effBlock < incoming) { score -= 60; reasons.Add("脆弱打折挡不住"); }
                        }
                        // 过量格挡浪费
                        if (block > 0 && myBlock + block > incoming && !lethalRisk)
                        {
                            score -= 40;
                            reasons.Add("格挡过量");
                        }
                    }
                    // 抽牌/加能量类技能在有能量余量时优先（先过牌再打核心）
                    if (IsDrawOrEnergyCard(c) && energy > 0)
                    {
                        score += 90;
                        reasons.Add("过牌/充能");
                    }
                    break;

                default:
                    score += 10;
                    reasons.Add("其他");
                    break;
            }

            // ---- 通用修正 ----
            // 0 费牌不消耗能量 → 永远是"净赚"，加一点固定权重鼓励打出（修正"零费卡不打"）
            if (cost == 0 && type != CardType.Status && type != CardType.Curse)
            {
                score += 12;
                reasons.Add("0费白赚");
            }
            // 高费牌在没有能量时不该被选中（CanPlay 已保证付得起，但留着给低费牌机会）
            else if (cost >= energy && energy <= 1 && type != CardType.Attack)
            {
                score -= 15;
                reasons.Add("吃满能量");
            }

            // ---- v2.4.2 条件机制修正（解决“无格挡却打全身撞击”等） ----
            // 有些牌的**实际效果取决于局面条件**（全身撞击伤害=当前格挡、催化需要目标已中毒…），
            // 光看牌型评分会把它们当成普通牌优先打出。这里统一施加机制修正：
            //   · ScoreDelta = -999 表示“当前打出完全无价值”（如 0 格挡打全身撞击）→ 直接判负，
            //     本地回退也不会选它（CanPlay 仍为 true，所以必须在这里拦）。
            try
            {
                var mech = CardMechanics.Evaluate(c, me);
                if (mech != null)
                {
                    if (mech.ScoreDelta <= -900)
                    {
                        why = $"机制否决：{mech.Status}";
                        return -900;
                    }
                    score += mech.ScoreDelta;
                    if (!string.IsNullOrEmpty(mech.Status)) reasons.Add("机制:" + Shorten(mech.Status, 26));
                }
            }
            catch { /* 机制评估失败不影响原有评分 */ }

            // 人工禁选（理论上 CanPlay 已判，保险起见）
            if (IsBanned(c)) { why = "人工禁选"; return -1000; }

            score += _rand.NextDouble() * 3; // 极小随机，避免同分死板
        }
        catch (Exception e)
        {
            Pilot.Warn("评分 " + c?.Id.Entry + " 出错: " + e.Message);
            score = 1;
            reasons.Add("评分异常");
        }

        why = reasons.Count == 0 ? "默认" : string.Join("+", reasons);
        return score;
    }

    /// <summary>本方是否带脆弱(Frail)——有则格挡效果 −25%。</summary>
    private static bool HasFrail(Player me)
    {
        try
        {
            var c = me?.Creature;
            if (c == null) return false;
            return c.HasPower<MegaCrit.Sts2.Core.Models.Powers.FrailPower>();
        }
        catch { return false; }
    }

    /// <summary>
    /// 目标选择（v2.6.0 集火一致性）：
    ///   0) **集火目标优先** —— 若本场已有集火目标且仍存活，直接打它（保证铺垫与输出同向）；
    ///   1) 没集火目标时：能一击必杀的敌人优先；
    ///   2) 否则：意图伤害最高的敌人（先拆威胁）；
    ///   3) 兜底：血最少的敌人。
    /// 选中的目标会被记为新的集火目标，使后续攻击牌自动跟上。
    /// </summary>
    private static Creature ChooseTarget(CardModel card, Player me)
    {
        try
        {
            if (card.TargetType != TargetType.AnyEnemy) return null;
            ICombatState cs = card.CombatState ?? me.Creature?.CombatState;
            if (cs == null) return null;
            List<Creature> enemies = cs.HittableEnemies?.Where(e => e.IsAlive).ToList();
            if (enemies == null || enemies.Count == 0) return null;

            // 0) 集火目标优先（跨回合黏住，除非它已死）
            Creature focus = GetFocusTarget(me);
            if (focus != null) return focus;

            int myDamage = EstimateCardDamage(card, me);
            // 1) 能杀的先杀
            Creature killable = enemies.OrderBy(e => e.CurrentHp).FirstOrDefault(e => myDamage >= e.CurrentHp);
            if (killable != null) { SetFocusTarget(killable); return killable; }
            // 2) 威胁最大（意图伤害最高）的优先 —— 拆掉打你最疼的
            Creature threat = enemies.OrderByDescending(e => EstimateIncomingFrom(e)).FirstOrDefault();
            if (threat != null) { SetFocusTarget(threat); return threat; }
            // 3) 兜底：血最少的
            Creature weakest = enemies.OrderBy(e => e.CurrentHp).First();
            SetFocusTarget(weakest);
            return weakest;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 估算某个敌人在其下一动里对本方造成的伤害（用于挑威胁）。
    ///
    /// v2.7.5 修正（前瞻永远不为保命格挡的根因）：
    ///   旧实现用 `GetSingleDamage(new[]{Pilot.Me.Creature}, enemy)`，有两个问题 ——
    ///   ① **漏掉多段攻击**：`GetSingleDamage` 只算"一段"的伤害，不含 `Repeats`
    ///      （双尾鼠 3×4 会被读成 4）→ 应改用 `GetTotalDamage`（= 单段 × 段数）。
    ///   ② **与威胁时间线口径不一致**：`GameState.IntentDamage`（战斗描述用的是它，
    ///      实测「预计承伤 16」正确）走 `GetTotalDamage`，而这里走 `GetSingleDamage`。
    ///      两套口径并存 = 前瞻算出的承伤和玩家/LLM 看到的完全对不上。
    ///   现在**直接委托给 `GameState.IntentDamage`**，保证"前瞻看到的风险"与
    ///   "提示词里写的风险"是同一个数 —— 这是"濒死回合必须格挡"能被正确评估的前提。
    /// </summary>
    private static int EstimateIncomingFrom(Creature enemy)
    {
        try
        {
            // 统一口径：与提示词里的【逐回合威胁时间线】用同一函数
            return GameState.IntentDamage(enemy);
        }
        catch { return 0; }
    }

    // ---------- 战斗数值评估辅助 ----------

    private static int GetMyBlock(Player me)
    {
        try { return me.Creature?.Block ?? 0; }
        catch { return 0; }
    }

    private static int TotalEnemyHp(Player me)
    {
        try
        {
            ICombatState cs = me.Creature?.CombatState;
            if (cs == null) return 0;
            return cs.Enemies.Where(e => e.IsAlive).Sum(e => Math.Max(0, e.CurrentHp));
        }
        catch { return 0; }
    }

    /// <summary>
    /// 估算本回合将承受的伤害。枚举敌人身上带“意图”信息的攻击。
    /// 反编译显示敌人意图暴露在 Creature 的 intent/attack 相关成员上；
    /// 这里做了容错：拿不到意图时保守假设每只敌人打 8 点。
    /// </summary>
    private static int EstimateIncomingDamage(Player me)
    {
        try
        {
            ICombatState cs = me.Creature?.CombatState;
            if (cs == null) return 0;
            int total = 0;
            foreach (Creature e in cs.Enemies.Where(x => x.IsAlive))
            {
                total += GuessCreatureIntentDamage(e);
            }
            return total;
        }
        catch { return 0; }
    }

    /// <summary>
    /// 从敌人身上读取"本回合意图伤害"。
    ///
    /// v2.3.6 修正：原来只做反射猜属性名（`IntentDamage`/`AttackDamage` 在
    /// <c>Creature</c> 上并不存在 → 永远回落成"保守 8 点"），导致 AI **完全看不到
    /// 敌人真实威胁**，战损判断失真。现在改为走官方 API：
    ///   <c>Creature.Monster.NextMove.Intents</c> → `AttackIntent.GetTotalDamage(targets, owner)`
    /// 与 <see cref="GameState.IntentDamage"/> 完全一致（那边战斗描述用的就是这条路径）。
    /// </summary>
    private static int GuessCreatureIntentDamage(Creature e)
    {
        try
        {
            var intents = e?.Monster?.NextMove?.Intents;
            if (intents != null)
            {
                var targets = new List<Creature> { e };
                int total = 0;
                foreach (var intent in intents)
                {
                    if (intent is MegaCrit.Sts2.Core.MonsterMoves.Intents.AttackIntent atk)
                    {
                        try { total += atk.GetTotalDamage(targets, e); } catch { }
                    }
                }
                return Math.Max(0, total);   // 非攻击意图 → 0（不再误判成 8）
            }
        }
        catch { /* 意图不可读，走保守估计 */ }

        // 兜底：反射猜常见属性名（旧路径，保留）
        try
        {
            var t = e.GetType();
            foreach (string name in new[] { "IntentDamage", "AttackDamage", "CurrentAttackDamage", "IntentValue" })
            {
                var p = t.GetProperty(name);
                if (p != null && p.PropertyType == typeof(int))
                {
                    return Math.Max(0, (int)p.GetValue(e));
                }
            }
        }
        catch { /* 忽略，走保守估计 */ }

        // 最后兜底：只有该敌人确实打算攻击时才给保守值，否则视为 0（避免虚报战损）
        try { if (e?.Monster != null && e.Monster.IntendsToAttack) return 8; } catch { }
        return 0;
    }

    /// <summary>
    /// 估算一张卡能造成的伤害。
    ///
    /// v2.7.2 铁律 —— **必须读 `CardModel.DynamicVars` 的真实数值**：
    ///   · 旧实现反射 `BaseDamage` / `Damage` / `CanonicalDamage`，而 **CardModel 上根本没有这些属性**
    ///     （`CanonicalVars` 是 protected 的**枚举**，不是数值）→ 反射全部落空 → 所有攻击牌都退化成
    ///     兜底值 **6**（恰好只有打击是 6，于是"看起来还行"，实则痛击 8 / 重击 10 全被读成 6）。
    ///   · 正解：`c.DynamicVars`（`DynamicVarSet`，公开）→ `["Damage"]` 取 `DamageVar`，
    ///     用 **`BaseValue`**（真实结算值；`PreviewValue` 仅供显示、含外部加成预览，不可用于计算）。
    ///   · ① 普通攻击 → `Damage`；② 计算型攻击（全身撞击等）→ `CalculatedDamage`；
    ///     ③ 多段攻击 → 再乘 `Repeat`（打击风暴等）。
    ///   · 力量加成：每段都加（真实结算即如此）。
    /// </summary>
    private static int EstimateCardDamage(CardModel c, Player me)
    {
        try
        {
            int per = 0;
            var vars = c.DynamicVars;
            if (vars != null)
            {
                if (vars.ContainsKey("Damage")) per = vars["Damage"]?.IntValue ?? 0;
                if (per <= 0 && vars.ContainsKey("CalculatedDamage")) per = vars["CalculatedDamage"]?.IntValue ?? 0;
            }

            if (per > 0)
            {
                int repeats = 1;
                try
                {
                    if (vars != null && vars.ContainsKey("Repeat"))
                    {
                        int r = vars["Repeat"]?.IntValue ?? 0;
                        if (r > 0) repeats = r;
                    }
                }
                catch { }

                // 力量加成作用于每一段
                int strength = GetMyPowerAmount(me, "Strength");
                return Math.Max(0, (per + strength) * repeats);
            }
        }
        catch { /* 走兜底 */ }

        // 兜底：只有确实是攻击牌时才给保守值（数值不准，仅用于防御性估算）
        try { return c.Type == CardType.Attack ? 6 : 0; } catch { return 0; }
    }

    /// <summary>
    /// 估算一张卡提供的格挡。
    /// v2.7.2：改读 `c.DynamicVars["Block"]`（`BlockVar.BaseValue`）——
    /// 旧实现反射 `BaseBlock`/`Block`/`CanonicalBlock` 同样不存在 → 恒返回 0
    /// （于是防御牌在前瞻里被当成"什么都不干"，必然低估格挡收益）。
    /// 计算型格挡走 `CalculatedBlock`。敏捷加成每段都加。
    /// </summary>
    private static int EstimateCardBlock(CardModel c, Player me)
    {
        try
        {
            int per = 0;
            var vars = c.DynamicVars;
            if (vars != null)
            {
                if (vars.ContainsKey("Block")) per = vars["Block"]?.IntValue ?? 0;
                if (per <= 0 && vars.ContainsKey("CalculatedBlock")) per = vars["CalculatedBlock"]?.IntValue ?? 0;
            }
            if (per > 0)
                return Math.Max(0, per + GetMyPowerAmount(me, "Dexterity"));
        }
        catch { /* 忽略 */ }
        return 0;
    }

    private static int GetMyPowerAmount(Player me, string powerName)
    {
        try
        {
            PowerModel p = me.Creature?.Powers?.FirstOrDefault(x =>
                x.GetType().Name.Equals(powerName + "Power", StringComparison.OrdinalIgnoreCase));
            return p?.Amount ?? 0;
        }
        catch { return 0; }
    }

    private static bool IsDrawOrEnergyCard(CardModel c)
    {
        try
        {
            string id = c.Id.Entry;
            return id.Contains("DRAW") || id.Contains("CYCLE") || id.Contains("ENERGY") ||
                   id.Contains("OFFERING") || id.Contains("SEEK") || id.Contains("APOTHEOSIS");
        }
        catch { return false; }
    }

    private static bool IsBanned(CardModel c)
    {
        try
        {
            var banned = PilotConfig.Current.BannedCards;
            if (banned == null || banned.Count == 0) return false;
            string id = c.Id.Entry;
            string title = SafeTitle(c);
            return banned.Any(b => !string.IsNullOrWhiteSpace(b) &&
                (id.Equals(b, StringComparison.OrdinalIgnoreCase) ||
                 title.Contains(b, StringComparison.OrdinalIgnoreCase)));
        }
        catch { return false; }
    }

    public static string SafeTitle(CardModel c)
    {
        try { return c.Title ?? c.Id.Entry; }
        catch { return c.Id.Entry; }
    }

    /// <summary>截断字符串（用于把机制说明塞进简洁的理由里）。</summary>
    private static string Shorten(string s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = s.Replace("\n", " ").Trim();
        return s.Length <= max ? s : s[..max] + "…";
    }

    /// <summary>取玩家角色的短 ID（供 RunStats 查分角色统计表用）。</summary>
    private static string CharIdOf(Player me)
    {
        try
        {
            string raw = me?.Character?.Id?.Entry;
            if (!string.IsNullOrEmpty(raw))
            {
                int i = raw.LastIndexOf('.');
                return i >= 0 ? raw.Substring(i + 1) : raw;
            }
        }
        catch { }
        return null;
    }

    /// <summary>智能药水使用：残血用治疗，有敌人时输出/增益药水才用，避免浪费。</summary>
    private static async Task UsePotionsSmartAsync(Player me, CancellationToken ct)
    {
        try
        {
            List<PotionModel> potions = me.Potions?.ToList();
            if (potions == null || potions.Count == 0) return;

            int hp = me.Creature?.CurrentHp ?? 1;
            int maxHp = me.Creature?.MaxHp ?? 1;
            double hpRatio = maxHp > 0 ? (double)hp / maxHp : 1;
            int incoming = EstimateIncomingDamage(me);
            bool dangerous = incoming > GetMyBlock(me) + hp * 0.5;
            bool critical = hpRatio * 100 < PilotConfig.Current.DefensiveHpPercent;

            foreach (PotionModel p in potions)
            {
                if (ct.IsCancellationRequested) return;
                if (me.PlayerCombatState?.Phase != PlayerTurnPhase.Play) return;

                // 进攻性药水：只在有敌人且场面吃力时用
                if (!critical && !dangerous && potions.Count <= 1) continue;

                Creature target = PickPotionTarget(p, me);
                if (target == null && p.TargetType.IsSingleTarget()) continue;
                Explain($"使用药水 {p.Id.Entry}");
                try
                {
                    p.EnqueueManualUse(target);
                    // v2.4.3：药水**可能弹出需要选牌的界面**（技能药水/攻击药水/能力药水 →
                    // CardSelectCmd.FromChooseACardScreen → NChooseACardSelectionScreen）。
                    // 该界面挂起一个 PlayerChoice 并取消待执行出牌，必须等它出现并处理掉，
                    // 否则战斗回合循环会死（见 HandleCombatOverlayAsync 注释）。
                    await MainThreadRunner.DelayAsync(600, ct);
                    for (int wait = 0; wait < 12; wait++)
                    {
                        if (ct.IsCancellationRequested) return;
                        if (!await HasCombatBlockingOverlayAsync()) break;
                        await HandleCombatOverlayAsync(ct);
                        await MainThreadRunner.DelayAsync(250, ct);
                    }
                }
                catch (Exception e) { Pilot.Warn("用药水失败: " + e.Message); }
            }
        }
        catch (Exception e)
        {
            Pilot.Warn("药水决策出错: " + e.Message);
        }
    }

    private static Creature PickPotionTarget(PotionModel p, Player me)
    {
        try
        {
            ICombatState cs = me.Creature?.CombatState;
            if (cs == null) return null;
            return p.TargetType switch
            {
                TargetType.AnyEnemy => cs.HittableEnemies?.OrderBy(e => e.CurrentHp).FirstOrDefault(),
                TargetType.AnyAlly or TargetType.AnyPlayer or TargetType.Self =>
                    cs.PlayerCreatures?.FirstOrDefault(c => c.IsAlive),
                _ => null,
            };
        }
        catch { return null; }
    }

    private static string DescribeBattlefield()
    {
        try
        {
            Player me = Pilot.Me;
            ICombatState cs = me?.Creature?.CombatState;
            if (cs == null) return "";
            int ehp = cs.Enemies.Where(e => e.IsAlive).Sum(e => Math.Max(0, e.CurrentHp));
            return $"我方 {me.Creature.CurrentHp}/{me.Creature.MaxHp}（格挡 {GetMyBlock(me)}，能量 {me.PlayerCombatState?.Energy}）" +
                   $" 敌方 {cs.Enemies.Count(e => e.IsAlive)} 只共 {ehp} 血，预计承伤 {EstimateIncomingDamage(me)}";
        }
        catch { return ""; }
    }

    // =====================================================================
    //  二、卡牌奖励评分（选卡）
    // =====================================================================

    /// <summary>
    /// 选择卡牌奖励。人工偏好 &gt; 禁选 &gt; [大模型] / 牌组契合度 &gt; 稀有度。
    /// </summary>
    public static CardModel ChooseCardReward(IReadOnlyList<CardModel> options, out string why)
    {
        why = "";
        if (options == null || options.Count == 0) return null;

        // 1) 人工偏好最高优先（玩家说的算，凌驾于大模型之上）
        foreach (CardModel c in options)
        {
            if (IsPreferred(c)) { why = $"人工偏好卡 {SafeTitle(c)}"; return c; }
        }

        // 2) 大模型决策
        if (UseLlm)
        {
            CardModel llmPick = LlmChooseCardReward(options, out string llmWhy);
            if (llmPick != null) { why = llmWhy; return llmPick; }
            if (!AllowFallback) return null;
        }

        // 3) 本地打分兜底（v2.4.0：改用 Selector 打分引擎，军师公式）
        var sc = Selector.ScoreOptions(options, Pilot.Me, isCombatReward: true);
        var picked = sc.Where(x => x.Card != null && x.Verdict != Selector.Verdict.Skip)
                       .OrderByDescending(x => x.Score).FirstOrDefault();
        if (picked == null)
        {
            // 全部建议跳过
            if (!PilotConfig.Current.AllowCardSkip) { why = "本地评分全低但禁用跳过，取最高分"; }
            else { why = "本地评分判断不值得拿（跳过）"; return null; }
            picked = sc.Where(x => x.Card != null).OrderByDescending(x => x.Score).FirstOrDefault();
            if (picked == null) return null;
        }
        why = $"{SafeTitle(picked.Card)}（{picked.VerdictCn} 分{picked.Score:F0}" +
              (picked.Reasons.Count > 0 ? "｜" + string.Join("；", picked.Reasons.Take(2)) : "") + "）";
        return picked.Card;
    }

    /// <summary>
    /// 通用"按动作语义选卡"（升级/移除/变化/附魔/事件选牌）。供 ScreenRouter 网格选牌调用。
    /// 人工偏好 &gt; 大模型 &gt; Selector 本地评分。
    /// 返回 null 表示"交回本地评分"（调用方自行兜底）。
    /// </summary>
    public static CardModel ChooseCardForAction(IReadOnlyList<CardModel> options, string action, Player me)
    {
        if (options == null || options.Count == 0) return null;

        // 1) 人工偏好
        foreach (CardModel c in options)
            if (IsPreferred(c)) return c;

        // 2) 大模型裁定（仅 LLM 模式；失败静默回退本地）
        if (!UseLlm) return null;
        try
        {
            string system =
                StsKnowledge.CardPicking +
                "\n你现在要执行的动作是【" + action + "】。\n" +
                "规则：\n" +
                "1) 升级 → 选**升级后数值提升最大**的牌（优先核心输出/核心能力，别升级临时的打击防御）；\n" +
                "2) 移除/删除 → 优先诅咒/状态，其次选**牌组里最弱、最没协同**的牌；\n" +
                "3) 变化 → 选**最弱、最该被替换**的牌（同样优先诅咒/状态）；\n" +
                "4) 附魔 → 选**底子最好、被强化后收益最大**的牌；\n" +
                "5) 选卡 → 选最契合当前牌组流派、最能补短板的牌。\n" +
                "只输出 JSON：{\"pick\":选项序号(1开始),\"reason\":\"不超过20字的理由\"}";

            string user =
                "【当前牌组】" + GameState.DescribeDeck(me) + "\n" +
                $"【生命】{me?.Creature?.CurrentHp}/{me?.Creature?.MaxHp}  【金币】{me?.Gold}\n" +
                $"【动作】{action}\n" +
                "【候选卡（含本地预评分，供参考）】\n" +
                Selector.DescribeScoredOptions(options, me, false) + "\n" +
                "请选择最合适的一张。只输出 JSON。";

            string answer = LlmClient.AskAsync(system, user).GetAwaiter().GetResult();
            if (string.IsNullOrEmpty(answer)) return null;
            var dict = LlmClient.ParseJsonObject(answer);
            if (dict == null) return null;
            int? pick = LlmClient.GetInt(dict, "pick");
            if (pick == null || pick <= 0) return null;
            int idx = pick.Value - 1;
            if (idx < 0 || idx >= options.Count) return null;
            CardModel chosen = options[idx];
            if (IsBanned(chosen)) { Pilot.Warn($"{action}：LLM 选了禁选卡，回退本地评分"); return null; }
            Pilot.Log($"{action}：LLM 裁定 {SafeTitle(chosen)}（{LlmClient.GetStr(dict, "reason")}）");
            return chosen;
        }
        catch (Exception e)
        {
            Pilot.Warn($"{action} LLM 裁定异常，回退本地评分: {e.Message}");
            return null;
        }
    }

    /// <summary>问大模型该选哪张卡（含可跳过）。失败返回 null。</summary>
    private static CardModel LlmChooseCardReward(IReadOnlyList<CardModel> options, out string why)
    {
        why = "";
        try
        {
            Player me = Pilot.Me;
            string system =
                "你是《杀戮尖塔2》的顶尖卡组构筑AI。当前玩家刚打赢一场战斗，要在几张卡中选择一张加入牌组（也可以选择跳过）。\n" +
                StsKnowledge.CardPicking + "\n" +
                "只输出 JSON：{\"pick\":选项序号(1开始,0表示跳过),\"reason\":\"不超过20字的理由\"}";

            string banned = PilotConfig.Current.BannedCards.Count == 0
                ? "" : "\n【玩家禁选(绝不能选)】" + string.Join("、", PilotConfig.Current.BannedCards);
            string preferred = PilotConfig.Current.PreferredCards.Count == 0
                ? "" : "\n【玩家偏好(尽量优先)】" + string.Join("、", PilotConfig.Current.PreferredCards);

            // 真实对局统计（来自知识库）—— 给大模型一份可信度很高的先验
            string stats = "";
            string codexText = "";
            try
            {
                var ids = options?.Where(c => c != null).Select(c =>
                {
                    try { return c.Pool?.Id?.Entry?.Split('.').LastOrDefault(); } catch { return null; }
                }).Where(s => !string.IsNullOrEmpty(s)).ToList() ?? new List<string>();
                if (ids.Count > 0)
                {
                    var deckIds = new List<string>();
                    try
                    {
                        foreach (var c in me.Deck.Cards)
                        {
                            try { var s = c.Pool?.Id?.Entry?.Split('.').LastOrDefault(); if (!string.IsNullOrEmpty(s)) deckIds.Add(s); } catch { }
                        }
                    }
                    catch { }
                    stats = RunStats.PromptSummary(ids, CharIdOf(me), deckIds);
                    // 权威卡牌数据（spire-codex）：补上 RunStats 没有的"这牌到底是什么效果"
                    codexText = Codex.CardsPrompt(ids);
                }
            }
            catch { }

            string user =
                "【当前牌组】" + GameState.DescribeDeck(me) + "\n" +
                $"【生命】{me?.Creature?.CurrentHp}/{me?.Creature?.MaxHp}  【金币】{me?.Gold}\n" +
                banned + preferred + "\n" +
                codexText +
                stats +
                "【可选卡牌（含本地预评分与流派协同，供参考，你可自行判断）】\n" +
                Selector.DescribeScoredOptions(options, me, true) + "\n" +
                "请选择最该拿的一张（补短板/接流派优先），或跳过。只输出 JSON。";

            string answer = LlmClient.AskAsync(system, user).GetAwaiter().GetResult();
            if (string.IsNullOrEmpty(answer)) return null;

            var dict = LlmClient.ParseJsonObject(answer);
            if (dict == null) return null;

            int? pick = LlmClient.GetInt(dict, "pick");
            string reason = LlmClient.GetStr(dict, "reason") ?? "";
            if (pick == null || pick == 0)
            {
                why = "LLM 判断跳过" + (string.IsNullOrEmpty(reason) ? "" : $"（{reason}）");
                return null; // null = 跳过
            }
            int idx = pick.Value - 1;
            if (idx < 0 || idx >= options.Count) return null;

            CardModel chosen = options[idx];
            if (IsBanned(chosen)) { Pilot.Warn("LLM 选了禁选卡，覆盖为跳过"); why = "LLM 误选禁选卡→跳过"; return null; }
            why = $"LLM：{SafeTitle(chosen)}（{reason}）";
            return chosen;
        }
        catch (Exception e)
        {
            Pilot.Warn("LLM 选卡异常: " + e.Message);
            return null;
        }
    }

    /// <summary>卡牌对牌组的价值评分：补短板 + 过牌/删牌 + 能力牌 + 稀有度。</summary>
    private static double ScoreDeckCard(CardModel c, out string why)
    {
        double s = 0;
        var r = new List<string>();
        try
        {
            // 稀有度基线
            switch (c.Rarity)
            {
                case CardRarity.Rare: s += 40; r.Add("稀有"); break;
                case CardRarity.Uncommon: s += 25; r.Add("罕见"); break;
                case CardRarity.Common: s += 15; r.Add("普通"); break;
                case CardRarity.Ancient: s += 55; r.Add("远古"); break;
                default: s += 10; break;
            }

            // 牌组短板分析
            DeckProfile prof = AnalyzeDeck();
            switch (c.Type)
            {
                case CardType.Power:
                    s += 35; r.Add("能力牌滚雪球");
                    break;
                case CardType.Attack:
                    if (prof.AttackCount < prof.DeckSize * 0.35) { s += 25; r.Add("缺输出"); }
                    else s += 10;
                    if (prof.AvgAttackDamage < 9) { s += 15; r.Add("输出偏弱"); }
                    break;
                case CardType.Skill:
                    if (prof.BlockCount < prof.DeckSize * 0.30) { s += 25; r.Add("缺防御"); }
                    else s += 8;
                    break;
            }

            if (IsDrawOrEnergyCard(c)) { s += 30; r.Add("过牌/充能"); }

            // 高费牌在能量不足的牌组里降权
            try
            {
                int cost = c.EnergyCost.GetWithModifiers(CostModifiers.Local);
                if (cost >= 3 && prof.AvgEnergy <= 3) { s -= 12; r.Add("费用偏高"); }
            }
            catch { }

            // 牌组越臃肿，越优先过牌/精简类
            if (prof.DeckSize > 28 && IsDrawOrEnergyCard(c)) { s += 15; r.Add("牌组臃肿"); }

            // 云端胜率修正（可选）
            double cloud = CloudWinrate.GetBonus(c);
            if (cloud != 0)
            {
                s += cloud;
                r.Add($"云胜率{(cloud > 0 ? "+" : "")}{cloud:F0}");
            }
        }
        catch (Exception e)
        {
            Pilot.Warn("选卡评分出错: " + e.Message);
        }
        why = r.Count == 0 ? "常规" : string.Join("+", r);
        return s;
    }

    private static bool IsPreferred(CardModel c)
    {
        try
        {
            var prefs = PilotConfig.Current.PreferredCards;
            if (prefs == null || prefs.Count == 0) return false;
            string id = c.Id.Entry;
            string title = SafeTitle(c);
            return prefs.Any(p => !string.IsNullOrWhiteSpace(p) &&
                (id.Equals(p, StringComparison.OrdinalIgnoreCase) ||
                 title.Contains(p, StringComparison.OrdinalIgnoreCase)));
        }
        catch { return false; }
    }

    private struct DeckProfile
    {
        public int DeckSize;
        public int AttackCount;
        public int BlockCount;
        public int AvgAttackDamage;
        public int AvgEnergy;
    }

    private static DeckProfile AnalyzeDeck()
    {
        var prof = new DeckProfile { DeckSize = 10, AvgEnergy = 3 };
        try
        {
            Player me = Pilot.Me;
            var deck = me?.Deck?.Cards;
            if (deck == null || deck.Count == 0) return prof;
            prof.DeckSize = deck.Count;
            prof.AttackCount = deck.Count(c => c.Type == CardType.Attack);
            prof.BlockCount = deck.Count(c => c.Type == CardType.Skill);
            var atk = deck.Where(c => c.Type == CardType.Attack).ToList();
            if (atk.Count > 0)
            {
                int total = 0;
                foreach (var c in atk) total += EstimateCardDamage(c, me);
                prof.AvgAttackDamage = total / atk.Count;
            }
        }
        catch { }
        return prof;
    }

    // =====================================================================
    //  三、地图选路
    // =====================================================================

    /// <summary>从可走的下一层节点中挑一个。返回对应的 MapPoint（由 ScreenRouter 负责点击）。</summary>
    public static MegaCrit.Sts2.Core.Map.MapPoint ChooseMapPoint(
        IReadOnlyList<MegaCrit.Sts2.Core.Map.MapPoint> candidates, out string why)
    {
        why = "";
        if (candidates == null || candidates.Count == 0) return null;
        if (candidates.Count == 1) { why = "唯一可走"; return candidates[0]; }

        // 大模型选路优先
        if (UseLlm)
        {
            var llmPick = LlmChooseMapPoint(candidates, out string llmWhy);
            if (llmPick != null) { why = llmWhy; return llmPick; }
            if (!AllowFallback) return candidates[0];
        }

        Player me = Pilot.Me;
        int hp = me?.Creature?.CurrentHp ?? 1;
        int maxHp = me?.Creature?.MaxHp ?? 1;
        double hpRatio = maxHp > 0 ? (double)hp / maxHp : 1;
        int gold = me?.Gold ?? 0;
        RunState rs = Pilot.RunStateNow;
        int floor = rs?.TotalFloor ?? 1;
        int act = (rs?.CurrentActIndex ?? 0) + 1;

        var scored = new List<(MegaCrit.Sts2.Core.Map.MapPoint p, double s, string r)>();

        // v2.5.4：先做一次路线前瞻，把"走这个节点之后会遇到什么"计入评分。
        // 此前只评"眼前这一步"，导致 AI 会选一条"眼前是普通战斗、后面却是三连精英"的送死路线。
        Dictionary<MegaCrit.Sts2.Core.Map.MapPoint, MapRoute.CandidateEval> routeEval = new();
        try
        {
            var evals = MapRoute.Evaluate(candidates, depth: 6, maxPaths: 4);
            foreach (var ev in evals) if (ev?.Point != null) routeEval[ev.Point] = ev;
        }
        catch (Exception e) { Pilot.Warn("路线前瞻（本地）失败，退化为单层评估: " + e.Message); }

        foreach (var p in candidates)
        {
            double s = 0;
            var reasons = new List<string>();
            string type = p.PointType.ToString();

            switch (type)
            {
                case "RestSite":
                    // 血少必休息；血多时价值中等
                    if (hpRatio * 100 < PilotConfig.Current.LowHpRestPercent) { s += 100; reasons.Add("残血优先休息"); }
                    else { s += 35; reasons.Add("休息点"); }
                    break;

                case "Elite":
                    if (PilotConfig.Current.SeekEliteWhenHealthy && hpRatio * 100 >= PilotConfig.Current.EliteHpPercent)
                    {
                        s += 55; reasons.Add("健康可打精英(拿遗物)");
                    }
                    else
                    {
                        s += 5; reasons.Add("状态不佳避开精英");
                    }
                    break;

                case "Shop":
                    if (gold >= PilotConfig.Current.ShopMinGold) { s += 50; reasons.Add($"金币充足({gold})可购物"); }
                    else { s += 12; reasons.Add("金币不足，商店降权"); }
                    break;

                case "Treasure":
                    s += 45; reasons.Add("宝箱稳赚");
                    break;

                case "Event":
                    s += 38; reasons.Add("事件有惊喜");
                    break;

                case "Monster":
                    // 前期普通战斗性价比高（拿卡成长），后期降权
                    if (floor < 8) { s += 40; reasons.Add("前期打怪成长"); }
                    else { s += 25; reasons.Add("普通战斗"); }
                    break;

                case "Boss":
                    s += 60; reasons.Add("首领必打");
                    break;

                default:
                    s += 20; reasons.Add(type);
                    break;
            }

            // 幕越深，稳健路线价值越高
            if (act >= 2 && hpRatio < 0.6) { if (type == "Monster") s -= 10; }

            // v2.5.4：路径加成 —— 走这一步之后的分支质量（休息点兜底 / 精英连战 / 宝箱收益）
            if (routeEval.TryGetValue(p, out var ev) && ev != null)
            {
                double bonus = MapRoute.PathBonus(ev, hpRatio, floor);
                if (Math.Abs(bonus) >= 1)
                {
                    s += bonus;
                    var bits = new List<string>();
                    if (ev.RestCount > 0) bits.Add($"后续休息×{ev.RestCount}");
                    if (ev.EliteCount > 0) bits.Add($"后续精英×{ev.EliteCount}");
                    if (ev.TreasureCount > 0) bits.Add($"后续宝箱×{ev.TreasureCount}");
                    if (ev.MaxEliteStreak >= 2) bits.Add($"连战{ev.MaxEliteStreak}");
                    if (bits.Count > 0)
                        reasons.Add($"路径({string.Join(",", bits)}){(bonus >= 0 ? "+" : "")}{bonus:F0}");
                }
            }

            s += _rand.NextDouble() * 2;
            scored.Add((p, s, string.Join("+", reasons)));
        }

        scored.Sort((a, b) => b.s.CompareTo(a.s));
        var chosen = scored[0];
        why = $"{chosen.p.PointType}（{chosen.r}）";
        return chosen.p;
    }

    /// <summary>问大模型该走哪个节点。失败返回 null。</summary>
    private static MegaCrit.Sts2.Core.Map.MapPoint LlmChooseMapPoint(
        IReadOnlyList<MegaCrit.Sts2.Core.Map.MapPoint> candidates, out string why)
    {
        why = "";
        try
        {
            Player me = Pilot.Me;
            RunState rs = Pilot.RunStateNow;
            int floor = rs?.TotalFloor ?? 1;
            int act = (rs?.CurrentActIndex ?? 0) + 1;
            int hp = me?.Creature?.CurrentHp ?? 0;
            int maxHp = me?.Creature?.MaxHp ?? 1;
            double pct = maxHp > 0 ? hp * 100.0 / maxHp : 0;

            string system =
                "你是《杀戮尖塔2》的顶尖路线规划AI。玩家要在分叉路口选择下一个房间。\n" +
                StsKnowledge.Map +
                "【决策原则】\n" +
                "1) 血少(<55%)时优先休息点，避免精英；血多时可打精英拿遗物；\n" +
                "2) 前期普通战斗性价比高（拿卡成长）；\n" +
                "3) 金币充足(≥120)时商店价值高；\n" +
                "4) 宝箱稳赚不亏，事件有惊喜但也有风险；\n" +
                "5) 幕数越深越要稳健，别在残血时冒险。\n" +
                "6) **必须看『后续分支』而不是只看眼前这一步**：路线规划的价值在于——为了走到后面的休息点/宝箱，\n" +
                "   现在这一步值不值得。若某个选项之后紧接着连续精英而当前血量不高，应明确避开；\n" +
                "   若某个选项之后有休息点兜底，则即使当前这一步是精英也值得考虑。\n" +
                "只输出 JSON：{\"pick\":选项序号(1开始),\"reason\":\"不超过20字的理由\"}";

            // v2.5.4：路线前瞻 —— 让模型看到每个候选之后整条分支的走向（核心修复）
            string lookahead = "";
            try
            {
                var evals = MapRoute.Evaluate(candidates, depth: 6, maxPaths: 4);
                if (evals.Count > 0)
                    lookahead = "【后续分支走向（前瞻 6 层，这是选择的关键依据）】\n" +
                                MapRoute.Render(evals, pct / 100.0, floor) + "\n";
            }
            catch (Exception e) { Pilot.Warn("路线前瞻渲染失败（不影响选路）: " + e.Message); }

            string user =
                $"【状态】第 {act} 幕 · 第 {floor} 层   生命 {hp}/{maxHp}（{pct:F0}%）  金币 {me?.Gold}\n" +
                $"【牌组】{GameState.DescribeDeck(me)}\n" +
                "【可走节点】\n" + GameState.DescribeMapOptions(candidates, floor, act) + "\n" +
                lookahead +
                "请综合『眼前节点』与『后续分支』选择最佳去处。";

            string answer = LlmClient.AskAsync(system, user).GetAwaiter().GetResult();
            if (string.IsNullOrEmpty(answer)) return null;

            var dict = LlmClient.ParseJsonObject(answer);
            if (dict == null) return null;

            int? pick = LlmClient.GetInt(dict, "pick");
            string reason = LlmClient.GetStr(dict, "reason") ?? "";
            if (pick == null) return null;
            int idx = pick.Value - 1;
            if (idx < 0 || idx >= candidates.Count) return null;

            why = $"LLM：{GameState.PointCn(candidates[idx].PointType)}（{reason}）";
            return candidates[idx];
        }
        catch (Exception e)
        {
            Pilot.Warn("LLM 选路异常: " + e.Message);
            return null;
        }
    }

    // =====================================================================
    //  四、房间分发（由 Pilot 调用）
    // =====================================================================

    public static async Task HandleRoomAsync(RoomType roomType, CancellationToken ct)
    {
        await Pilot.WaitIfPausedAsync(ct);
        if (!Pilot.IsRunning) return;

        // 房间切换时清掉"已处理"标记（否则新商店会被当成处理过的旧商店）
        ScreenRouter.NotifyRoomChanged(roomType);

        switch (roomType)
        {
            case RoomType.Monster:
            case RoomType.Elite:
            case RoomType.Boss:
                await FightAsync(ct);
                break;
            case RoomType.Shop:
                // 商店库存浮层是 IScreenContext 而非 IOverlayScreen，不进 NOverlayStack，
                // 必须房间级驱动（HandleTopAsync 收不到它）。
                await MainThreadRunner.DelayAsync(200, ct);
                await ScreenRouter.HandleShopRoomAsync(
                    MegaCrit.Sts2.Core.Nodes.NRun.Instance?.MerchantRoom, ct);
                // 兜底：HandleShopRoomAsync 返回后若房间仍停着，确保推进
                await DrainAndAdvanceRoomAsync(roomType, ct);
                break;
            case RoomType.RestSite:
                // ⚠ NRestSiteRoom 同样是 IScreenContext（非 IOverlayScreen）→ 必须房间级驱动
                await MainThreadRunner.DelayAsync(300, ct);
                await ScreenRouter.HandleRestSiteRoomAsync(
                    MegaCrit.Sts2.Core.Nodes.NRun.Instance?.RestSiteRoom, ct);
                await DrainAndAdvanceRoomAsync(roomType, ct);
                break;
            case RoomType.Event:
                // ⚠ NEventRoom 同样是 IScreenContext（非 IOverlayScreen）→ 必须房间级驱动
                await MainThreadRunner.DelayAsync(300, ct);
                await ScreenRouter.HandleEventRoomAsync(
                    MegaCrit.Sts2.Core.Nodes.NRun.Instance?.EventRoom, ct);
                await DrainAndAdvanceRoomAsync(roomType, ct);
                break;
            case RoomType.Treasure:
                // ⚠ NTreasureRoom 同样是 IScreenContext（非 IOverlayScreen）→ 必须房间级驱动。
                //   v2.5.0 前这里落到 default 分支 → 从不开箱，只会点"继续"（用户反馈的 Bug #1）。
                await MainThreadRunner.DelayAsync(250, ct);
                await ScreenRouter.HandleTreasureRoomAsync(
                    MegaCrit.Sts2.Core.Nodes.NRun.Instance?.TreasureRoom, ct);
                await DrainAndAdvanceRoomAsync(roomType, ct);
                break;
            default:
                // 其它纯房间：浮层由 ScreenRouter 处理，再兜底推进
                await MainThreadRunner.DelayAsync(200, ct);
                await DrainAndAdvanceRoomAsync(roomType, ct);
                break;
        }
    }

    /// <summary>
    /// 非战斗房间的兜底推进：浮层界面由 <see cref="ScreenRouter"/> 处理；
    /// 若浮层已空但房间仍停着（选项选完、纯地图房间等），则点"继续"。
    /// 这道兜底是黑屏修复的另一半 —— 房间必须真正切出去，否则画面会停在旧场景。
    /// </summary>
    private static async Task DrainAndAdvanceRoomAsync(RoomType roomType, CancellationToken ct)
    {
        await Pilot.DrainScreensAsync(ct);
        if (ct.IsCancellationRequested) return;

        // 纯地图型房间（事件结束后返回地图）无需额外动作
        if (roomType == RoomType.Unassigned) return;

        // ⚠ 防竞争：若地图已打开（房间切换中），绝不再点任何"继续"，
        //   否则会和 TravelToMapCoord 抢时序 → NCombatRoom.OnCombatSetUp 空引用（黑屏）。
        try
        {
            var map = MegaCrit.Sts2.Core.Nodes.Screens.Map.NMapScreen.Instance;
            if (map != null && map.IsOpen) return;
        }
        catch { }

        // 战斗房正在创建中也别动
        try
        {
            var run = MegaCrit.Sts2.Core.Nodes.NRun.Instance;
            if (run != null && run.MapRoom != null) return;
        }
        catch { }

        Node room = CurrentRoomNode();
        if (room == null) return;

        try
        {
            var proceed = MegaCrit.Sts2.Core.AutoSlay.Helpers.UiHelper
                .FindFirst<MegaCrit.Sts2.Core.Nodes.CommonUi.NProceedButton>(room);
            if (proceed != null && proceed.IsEnabled && proceed.Visible)
            {
                Pilot.Log($"{Pilot.RoomTypeCn(roomType)}：点击继续，离开房间");
                await MegaCrit.Sts2.Core.AutoSlay.Helpers.UiHelper.Click(proceed);
                await MainThreadRunner.DelayAsync(900, ct);
            }
        }
        catch (Exception e) { Pilot.Warn("房间推进失败: " + e.Message); }
    }

    /// <summary>取当前房间的场景节点（按房间类型路由）。</summary>
    private static Node CurrentRoomNode()
    {
        try
        {
            var run = MegaCrit.Sts2.Core.Nodes.NRun.Instance;
            if (run == null) return null;
            var rs = Pilot.RunStateNow;
            switch (rs?.CurrentRoom?.RoomType)
            {
                case RoomType.RestSite: return run.RestSiteRoom;
                case RoomType.Shop: return run.MerchantRoom;
                case RoomType.Event: return run.EventRoom;
                case RoomType.Treasure: return run.TreasureRoom;
                default: return null;
            }
        }
        catch { return null; }
    }
}
