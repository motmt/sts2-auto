using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.DevConsole;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Gold;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.Entities.RestSite;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes.Debug;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Runs.History;

namespace StealthKit;

/// <summary>
/// 潜行助手核心。
///
/// 原理（基于 v0.111 反编译）：
/// 商店买卖使用的 RewardObtainedMessage(ShouldBroadcast) 广播携带完整卡/遗物/药水/金币数据，
/// 任意端发出 -> 主机中继给其它客机 -> 每台机器执行原生 AddCard/RelicCmd.Obtain/TryToProcure/GainGold。
/// 因此“只有自己装模组、队友纯原版”也能让全队状态一致，绝不触发 StateDivergence。
/// 注意：必须在非战斗时刻发送（官方 SyncLocal* 在战斗中会直接抛异常，收端也只会缓冲到战斗后）。
/// </summary>
public static class Core
{
    // ============ 运行时状态 ============
    private static bool _combatSubscribed;
    private static bool _runSubscribed;
    private static string _runBuffs = ""; // 本关增益：只对“当前这一场战斗”有效，战斗结束自动清空
    private static readonly Dictionary<string, int> _appliedThisCombat = new(); // 本场已生效的增益层数(键→已加层数)
    private static readonly List<string> _log = new();

    // ============ 辅助：本机玩家 ============
    public static Player Me => LocalContext.GetMe(RunManager.Instance.DebugOnlyGetState());

    public static bool IsInRealMultiplayerRun
    {
        get
        {
            RunManager rm = RunManager.Instance;
            if (rm == null || !rm.IsInProgress || rm.NetService == null) return false;
            return rm.NetService.Type is NetGameType.Host or NetGameType.Client && !rm.IsSingleplayerOrFakeMultiplayer;
        }
    }

    public static bool InCombat => CombatManager.Instance != null && CombatManager.Instance.IsInProgress;

    private static RewardSynchronizer Sync => RunManager.Instance?.RewardSynchronizer;

    // ============ 配置 ============
    public static class Config
    {
        public static readonly string Path = System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? ".",
            "StealthKit_config.json");

        /// <summary>愿望卡：一场战斗结束后自动送达（一次性）。</summary>
        public static string WishCardId = "";

        /// <summary>进休息点时自动执行一次“回复(Heal)”。</summary>
        public static bool AutoRestHeal = false;

        // 说明：开局增益(sk 6)不再持久化 —— 它只对“当前这一局”有效，存内存，开新局自动清空。

        public static void Load()
        {
            try
            {
                if (!File.Exists(Path)) return;
                using var doc = JsonDocument.Parse(File.ReadAllText(Path));
                if (doc.RootElement.TryGetProperty("wishCardId", out var w)) WishCardId = w.GetString() ?? "";
                if (doc.RootElement.TryGetProperty("autoRestHeal", out var a)) AutoRestHeal = a.GetBoolean();
                // 旧版可能残留 startingPowers，这里有意忽略：开局增益只当局有效。
            }
            catch (Exception e)
            {
                ModEntry.Logger.Warn("config load failed: " + e);
            }
        }

        public static void Save()
        {
            try
            {
                var obj = new { wishCardId = WishCardId ?? "", autoRestHeal = AutoRestHeal };
                File.WriteAllText(Path, JsonSerializer.Serialize(obj, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception e)
            {
                ModEntry.Logger.Warn("config save failed: " + e);
            }
        }
    }

    // ============ 模型查找（与控制台一致） ============
    public static CardModel FindCard(string id)
    {
        string u = id.ToUpperInvariant();
        return ModelDb.AllCards.FirstOrDefault(c => c.Id.Entry == u)
            ?? ModelDb.AllCards.FirstOrDefault(c => c.Id.Entry.StartsWith(u, StringComparison.Ordinal));
    }

    public static RelicModel FindRelic(string id)
    {
        string u = id.ToUpperInvariant();
        var list = ModelDb.AllRelics.Where(r => r.Id.Entry.Contains(u, StringComparison.Ordinal)).ToList();
        return list.FirstOrDefault(r => r.Id.Entry == u)
            ?? list.FirstOrDefault(r => r.Id.Entry.StartsWith(u, StringComparison.Ordinal))
            ?? list.FirstOrDefault();
    }

    public static PotionModel FindPotion(string id)
    {
        string u = id.ToUpperInvariant();
        return ModelDb.AllPotions.FirstOrDefault(p => p.Id.Entry == u);
    }

    // ============ 潜行指令核心 ============

    public static async Task<string> GiveCardAsync(string id)
    {
        if (RunManager.Instance == null || !RunManager.Instance.IsInProgress) return "错误：没有进行中的对局";
        Player me = Me;
        RunState state = RunManager.Instance.DebugOnlyGetState();
        if (me == null || state == null) return "错误：找不到本机玩家";
        if (InCombat) return "战斗中不能发放（官方同步通道限制），请离开战斗后再试";
        CardModel template = FindCard(id);
        if (template == null) return "找不到卡牌 '" + id + "'（可用原版卡 ID，如 OFFERING/STRIKE_RED…）";

        CardModel card = state.CreateCard(template, me);
        await CardPileCmd.Add(card, PileType.Deck);
        Sync?.SyncLocalObtainedCard(card);
        Note("card", card.Id.Entry);
        ArmSkipBeautify(card); // 若本层随后出现“三张全跳过”的卡牌奖励，把本地历史改写成拿到了这张卡
        return $"已悄悄把 {card.Id.Entry} 加入牌组（队友无需装模组，状态已全队同步）";
    }

    /// <summary>
    /// “付费购买”伪装：完整复刻商店买卡流程（加牌→扣钱(记花销)→广播花销→广播获得），
    /// 这样队友机器上你的该层记录=花销X+获得该卡，与真实商店购买完全一致。
    /// 建议在商店层使用；价格可显式传入，缺省按稀有度取近似商店价。
    /// </summary>
    public static async Task<string> BuyCardAsync(string id, int? explicitPrice)
    {
        if (RunManager.Instance == null || !RunManager.Instance.IsInProgress) return "错误：没有进行中的对局";
        Player me = Me;
        RunState state = RunManager.Instance.DebugOnlyGetState();
        if (me == null || state == null) return "错误：找不到本机玩家";
        if (InCombat) return "战斗中不能购买伪装，请离开战斗后再试";
        CardModel template = FindCard(id);
        if (template == null) return "找不到卡牌 '" + id + "'";
        int price = explicitPrice ?? GuessShopPrice(template);
        if (price <= 0) price = 50;
        int curGold = me.Gold;
        if (curGold < price)
        {
            return $"金币不足（当前 {curGold}，需要 {price}）。先 stealth give gold {price - curGold + 20} 补上再买。";
        }
        CardModel card = state.CreateCard(template, me);
        // 与 MerchantCardEntry.OnTryPurchase 完全一致的动作顺序
        await CardPileCmd.Add(card, PileType.Deck);
        await PlayerCmd.LoseGold(price, me, MegaCrit.Sts2.Core.Entities.Gold.GoldLossType.Spent);
        Sync?.SyncLocalGoldLost(price);
        Sync?.SyncLocalObtainedCard(card);
        Note("buy", card.Id.Entry + " x" + price);
        return $"已按商店流程‘购买’{card.Id.Entry}（花费 {price}，全队记录=花销+获得，与真实购买一致）";
    }

    /// <summary>按稀有度取一个接近商店价的默认价（供 buy 伪装用）。</summary>
    public static int GuessShopPrice(CardModel card)
    {
        try
        {
            return card.Rarity switch
            {
                MegaCrit.Sts2.Core.Entities.Cards.CardRarity.Common => 52,
                MegaCrit.Sts2.Core.Entities.Cards.CardRarity.Uncommon => 78,
                MegaCrit.Sts2.Core.Entities.Cards.CardRarity.Rare => 145,
                _ => 75,
            };
        }
        catch (Exception)
        {
            return 75;
        }
    }

    public static async Task<string> GiveRelicAsync(string id)
    {
        if (RunManager.Instance == null || !RunManager.Instance.IsInProgress) return "错误：没有进行中的对局";
        Player me = Me;
        if (me == null) return "错误：找不到本机玩家";
        if (InCombat) return "战斗中不能发放，请离开战斗后再试";
        RelicModel template = FindRelic(id);
        if (template == null) return "找不到遗物 '" + id + "'";
        RelicModel relic = template.ToMutable();
        await RelicCmd.Obtain(relic, me);
        Sync?.SyncLocalObtainedRelic(relic);
        Note("relic", relic.Id.Entry);
        return $"已悄悄获得遗物 {relic.Id.Entry}";
    }

    public static async Task<string> GivePotionAsync(string id)
    {
        if (RunManager.Instance == null || !RunManager.Instance.IsInProgress) return "错误：没有进行中的对局";
        Player me = Me;
        if (me == null) return "错误：找不到本机玩家";
        if (InCombat) return "战斗中不能发放，请离开战斗后再试";
        PotionModel template = FindPotion(id);
        if (template == null) return "找不到药水 '" + id + "'";
        PotionModel potion = template.ToMutable();
        await PotionCmd.TryToProcure(potion, me);
        Sync?.SyncLocalObtainedPotion(potion);
        Note("potion", potion.Id.Entry);
        return $"已悄悄获得药水 {potion.Id.Entry}";
    }

    public static async Task<string> GiveGoldAsync(int amount)
    {
        if (RunManager.Instance == null || !RunManager.Instance.IsInProgress) return "错误：没有进行中的对局";
        Player me = Me;
        if (me == null) return "错误：找不到本机玩家";
        if (InCombat) return "战斗中不能发放，请离开战斗后再试";
        if (amount == 0) return "金额不能为 0";
        if (amount > 0)
        {
            await PlayerCmd.GainGold(amount, me);
            Sync?.SyncLocalObtainedGold(amount);
        }
        else
        {
            await PlayerCmd.LoseGold(-amount, me, MegaCrit.Sts2.Core.Entities.Gold.GoldLossType.Spent);
        }
        Note("gold", amount.ToString());
        return $"金币 {(amount > 0 ? "+" : "")}{amount} 已同步";
    }

    /// <summary>在休息点执行一次“回复(Heal)”——等价于按下休息按钮，全队原生同步。</summary>
    public static async Task<string> RestHealAsync()
    {
        RunManager rm = RunManager.Instance;
        if (rm == null || !rm.IsInProgress) return "错误：没有进行中的对局";
        RestSiteSynchronizer sync = rm.RestSiteSynchronizer;
        if (sync == null) return "错误：当前不在休息点";
        var options = sync.GetLocalOptions();
        int idx = -1;
        for (int i = 0; i < options.Count; i++)
        {
            if (options[i].OptionId == "HEAL") { idx = i; break; }
        }
        if (idx < 0) return "休息点没有可用的‘回复’选项";
        await sync.ChooseLocalOption(idx);
        Note("restHeal", idx.ToString());
        return "已在休息点执行回复（全队已同步）";
    }

    /// <summary>设置愿望卡：下一场战斗结束后自动送达（一次性）。</summary>
    public static string SetWishCard(string id)
    {
        Config.WishCardId = string.IsNullOrWhiteSpace(id) ? "" : id.ToUpperInvariant();
        Config.Save();
        EnsureCombatHook();
        return Config.WishCardId == "" ? "愿望卡已清除" : $"愿望卡已设置：{Config.WishCardId}（下一场战斗结束后自动悄悄加入牌组）";
    }

    public static void EnsureCombatHook()
    {
        try
        {
            bool needRetry = false;
            if (!_combatSubscribed)
            {
                if (CombatManager.Instance == null)
                {
                    needRetry = true;
                }
                else
                {
                    CombatManager.Instance.CombatEnded += OnCombatEnded;
                    CombatManager.Instance.TurnStarted += OnTurnStarted;
                    _combatSubscribed = true;
                }
            }
            if (!_runSubscribed)
            {
                var rm = RunManager.Instance;
                if (rm == null)
                {
                    needRetry = true;
                }
                else
                {
                    rm.RunStarted += OnRunStarted; // 开新局 → 清空本局增益
                    _runSubscribed = true;
                }
            }
            if (needRetry)
            {
                TaskHelper.RunSafely(RetryCombatHookAsync());
            }
        }
        catch (Exception e)
        {
            ModEntry.Logger.Warn("combat hook subscribe failed: " + e);
        }
    }

    /// <summary>新的一局开始：本局增益自动清空（“过了就消失”）。</summary>
    private static void OnRunStarted(RunState _)
    {
        if (_runBuffs.Length > 0)
        {
            ModEntry.Logger.Info("New run started: cleared start buffs (" + _runBuffs + ")");
            _runBuffs = "";
        }
    }

    private static async Task RetryCombatHookAsync()
    {
        for (int i = 0; i < 40 && !(_combatSubscribed && _runSubscribed); i++)
        {
            await Task.Delay(1500);
            if (_combatSubscribed && _runSubscribed) return;
            try
            {
                EnsureCombatHook();
                if (_combatSubscribed && _runSubscribed) return;
            }
            catch (Exception)
            {
                // 忽略，继续重试
            }
        }
    }

    // ============ 每场战斗“自动贪污”金币（队友端一致、数值合并在节点GoldGained里） ============
    private static int _lootPerFight;

    /// <summary>设置每场战斗自动加的金币(≤0 关闭)。数额建议贴近该层正常奖励区间更不显眼。</summary>
    public static string SetLootPerFight(int amount)
    {
        _lootPerFight = Math.Max(0, amount);
        if (_lootPerFight > 0)
        {
            EnsureCombatHook();
            Note("lootMode", _lootPerFight.ToString());
        }
        return _lootPerFight > 0 ? $"每场战斗自动 +{_lootPerFight} 金币（全队同步；叠加在该层GoldGained合计中）" : "每场自动加金已关闭";
    }

    // ============ “自动自然落地”：想要的卡等下次进商店/事件节点再悄悄获得 ============
    // 队友视角三层全干净的玩法：游戏内无播报；商店/事件节点的“获得/花销”清单与真实形态一致，
    // 且该节点没有三选一 → hover 核对也挑不出毛病。商店落地=按价购买记录；事件落地=事件获得记录。
    private static string _naturalCardId = "";
    private static bool _roomSubscribed;

    public static string SetNaturalWish(string cardIdOrOff)
    {
        _naturalCardId = string.IsNullOrWhiteSpace(cardIdOrOff) ? "" : cardIdOrOff.ToUpperInvariant();
        if (_naturalCardId.Length > 0)
        {
            SubscribeRoomHook();
            Note("naturalWish", _naturalCardId);
        }
        return _naturalCardId.Length == 0
            ? "自动自然落地已关闭"
            : $"已设自然愿望：{_naturalCardId}（下一次进【商店】按价购买记录、或进【事件】按事件获得记录自动落地；全程无三选一/无破绽）";
    }

    private static void SubscribeRoomHook()
    {
        try
        {
            if (_roomSubscribed) return;
            if (RunManager.Instance == null) return;
            RunManager.Instance.RoomEntered += OnRoomEntered;
            _roomSubscribed = true;
        }
        catch (Exception e)
        {
            ModEntry.Logger.Warn("room hook subscribe failed: " + e);
        }
    }

    private static void OnRoomEntered()
    {
        string id = _naturalCardId;
        if (id.Length == 0) return;
        try
        {
            RunManager rm = RunManager.Instance;
            IRunState st = rm?.DebugOnlyGetState();
            AbstractRoom room = st?.CurrentRoom;
            if (room == null) return;
            MegaCrit.Sts2.Core.Rooms.RoomType t = room.RoomType;
            if (t != MegaCrit.Sts2.Core.Rooms.RoomType.Shop && t != MegaCrit.Sts2.Core.Rooms.RoomType.Event) return;
            _naturalCardId = ""; // 先取走，避免重复触发；失败可重设
            TaskHelper.RunSafely(LandNaturalAsync(id, t));
        }
        catch (Exception e)
        {
            ModEntry.Logger.Warn("natural landing trigger error: " + e);
        }
    }

    private static async Task LandNaturalAsync(string cardId, MegaCrit.Sts2.Core.Rooms.RoomType roomType)
    {
        try
        {
            // 等房间完全就绪、且不在战斗中
            for (int i = 0; i < 80; i++)
            {
                if (!InCombat && RunManager.Instance?.DebugOnlyGetState()?.CurrentRoom != null) break;
                await Task.Delay(60);
            }
            if (InCombat)
            {
                _naturalCardId = cardId; // 回到待命
                return;
            }
            if (roomType == MegaCrit.Sts2.Core.Rooms.RoomType.Shop)
            {
                CardModel tpl = FindCard(cardId);
                int price = tpl == null ? 50 : GuessShopPrice(tpl);
                Player me = Me;
                if (me == null || me.Gold < price)
                {
                    _naturalCardId = cardId; // 钱不够，等下个商店/事件
                    PrintToConsole(false, $"自然落地：金币不足（需约 {price}），愿望保留，等下次有钱的商店/事件");
                    return;
                }
                string r = await BuyCardAsync(cardId, price);
                ModEntry.Logger.Info("natural land (shop): " + r);
                PrintToConsole(true, r);
            }
            else
            {
                string r = await GiveCardAsync(cardId);
                ModEntry.Logger.Info("natural land (event): " + r);
                PrintToConsole(true, r);
            }
        }
        catch (Exception e)
        {
            _naturalCardId = cardId;
            ModEntry.Logger.Warn("natural landing error: " + e);
        }
    }

    private static void OnCombatEnded(MegaCrit.Sts2.Core.Rooms.CombatRoom _)
    {
        _appliedThisCombat.Clear();
        if (_runBuffs.Length > 0)
        {
            ModEntry.Logger.Info("Combat ended: cleared start buffs (" + _runBuffs + ") — single-room scope");
            _runBuffs = ""; // 单关卡有效：这场打完就失效，不带进下一关
        }
        int loot = _lootPerFight;
        if (loot > 0)
        {
            TaskHelper.RunSafely(DeliverLootAfterCombatAsync(loot));
        }
        string wish = Config.WishCardId;
        if (string.IsNullOrEmpty(wish)) return;
        TaskHelper.RunSafely(DeliverWishAfterCombatAsync(wish));
    }

    // ============ 开局增益：每场战斗第一回合开始时，给“自己”上 buff ============
    // 走原版网络化命令 power（IsNetworked=true）：联机时入同步队列全端一致执行，绝不掉线；
    // 单机/伪多人则本地直接执行同款命令。注意：力量/敏捷等增益图标在战斗中全员可见，属“明面增强”。
    // 常见正面增益的中文/英文别名 → 标准名（类名去 Power 后缀的大写形式）
    private static readonly Dictionary<string, string> PowerAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["力量"] = "STRENGTH", ["力气"] = "STRENGTH", ["str"] = "STRENGTH", ["strength"] = "STRENGTH",
        ["敏捷"] = "DEXTERITY", ["dex"] = "DEXTERITY", ["dexterity"] = "DEXTERITY",
        ["集中"] = "FOCUS", ["focus"] = "FOCUS",
        ["缓冲"] = "BUFFER", ["buffer"] = "BUFFER",
        ["仪式"] = "RITUAL", ["ritual"] = "RITUAL",
        ["再生"] = "REGEN", ["regen"] = "REGEN", ["regeneration"] = "REGEN",
        ["荆棘"] = "THORNS", ["thorns"] = "THORNS",
        ["人工制品"] = "ARTIFACT", ["artifact"] = "ARTIFACT",
        ["灵体"] = "INTANGIBLE", ["无实体"] = "INTANGIBLE", ["intangible"] = "INTANGIBLE",
        ["活力"] = "VIGOR", ["vigor"] = "VIGOR",
    };

    private static string PowerAliasToId(string raw)
    {
        string key = raw.Trim();
        if (PowerAliases.TryGetValue(key, out string? id) && !string.IsNullOrEmpty(id))
        {
            return id;
        }
        return key.ToUpperInvariant();
    }

    /// <summary>按 ID(如 STRENGTH / STRENGTH_POWER) 找对应 PowerModel 类型。游戏真实 Id.Entry 形如 STRENGTH_POWER。</summary>
    private static Type? FindPowerType(string id)
    {
        string u = id.Trim().ToUpperInvariant();
        if (u.EndsWith("_POWER", StringComparison.Ordinal))
        {
            u = u[..^"_POWER".Length];
        }
        if (u.Length == 0) return null;
        try
        {
            return ModelDb.AllAbstractModelSubtypes.FirstOrDefault(t =>
                t.IsSubclassOf(typeof(PowerModel)) &&
                (t.Name.Equals(u + "Power", StringComparison.OrdinalIgnoreCase) ||
                 t.Name.Equals(u, StringComparison.OrdinalIgnoreCase)));
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>取某 power 类型在游戏里的真实模型 ID（多人命令字符串用它，保证两端一致可解析）。</summary>
    private static string RealPowerId(string normalizedId)
    {
        Type? t = FindPowerType(normalizedId);
        if (t == null) return normalizedId;
        try
        {
            string entry = ModelDb.DebugPower(t).Id.Entry;
            if (!string.IsNullOrWhiteSpace(entry)) return entry;
        }
        catch (Exception)
        {
            // 忽略，退回原 ID
        }
        return normalizedId;
    }

    public static string SetStartBuff(string alias, int amount)
    {
        if (amount <= 0) return "增益层数须为正整数，如：sk 6 力量 4";
        string id = PowerAliasToId(alias);
        if (id.Length == 0) return "无效增益名（可输 力量/敏捷，或英文 ID 如 RITUAL）";
        if (FindPowerType(id) == null)
        {
            return $"找不到增益 '{alias}'（可输 力量/敏捷，或英文名如 RITUAL/METALLICIZE）";
        }
        id = RealPowerId(id); // 规范化成游戏真实 ID（形如 STRENGTH_POWER）
        var list = ParseStartBuffs();
        list.RemoveAll(x => PowerTypeKey(x.Id) == PowerTypeKey(id));
        list.Add(new StartBuff { Id = id, Amount = amount });
        _runBuffs = string.Join(",", list.Select(x => $"{x.Id}:{x.Amount}"));
        EnsureCombatHook();
        Note("startBuff", $"{id}x{amount}");
        if (InCombat)
        {
            RequestApplyUnappliedBuffs("set-in-combat"); // 战斗中设置 → 本场立即补上（同种增益按增量叠加）
        }
        string tail = InCombat ? "（本场已立即生效）" : "（下一场战斗开始时生效）";
        return $"[仅本关] 已设：{id} +{amount} 层{tail}\n" +
               "单关卡有效：这场战斗结束就自动清空，不会带到下一关；sk 6 可叠加，sk 6 off 清空";
    }

    public static string ClearStartBuffs()
    {
        _runBuffs = "";
        return "本局增益已清空";
    }

    public static string StartBuffStatus()
    {
        var list = ParseStartBuffs();
        return list.Count == 0 ? "(无)" : string.Join("、", list.Select(x => $"{x.Id} +{x.Amount}"));    }

    private static List<StartBuff> ParseStartBuffs()
    {
        var r = new List<StartBuff>();
        foreach (string seg in (_runBuffs ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] p = seg.Split(':');
            if (p.Length == 2 && int.TryParse(p[1], out int n) && n > 0 && p[0].Trim().Length > 0)
            {
                r.Add(new StartBuff { Id = p[0].Trim().ToUpperInvariant(), Amount = n });
            }
        }
        return r;
    }

    /// <summary>同一增益的统一键（STRENGTH 与 STRENGTH_POWER 视为同一种）。</summary>
    private static string PowerTypeKey(string s)
    {
        string u = s.Trim().ToUpperInvariant();
        return u.EndsWith("_POWER", StringComparison.Ordinal) ? u : u + "_POWER";
    }

    private sealed class StartBuff
    {
        public string Id = "";
        public int Amount;
    }

    private static void OnTurnStarted(MegaCrit.Sts2.Core.Combat.CombatState state)
    {
        RequestApplyUnappliedBuffs("turn-start");
    }

    /// <summary>
    /// 把“本局配置里尚未在本场生效”的增益应用一次（幂等，可反复调用：回合开始、或战斗中刚设置时立即补上）。
    /// </summary>
    public static void RequestApplyUnappliedBuffs(string reason)
    {
        try
        {
            if (!InCombat) return;
            var all = ParseStartBuffs();
            if (all.Count == 0) return;
            var todo = new List<(string Id, int Amount)>();
            foreach (StartBuff b in all)
            {
                string key = PowerTypeKey(b.Id);
                int done = _appliedThisCombat.TryGetValue(key, out int d) ? d : 0;
                int delta = b.Amount - done;
                if (delta > 0) todo.Add((b.Id, delta)); // 未生效则全量；已生效则只补增量（便于本场调数值）
            }
            if (todo.Count == 0) return;
            Player me = Me;
            if (me == null) return;
            var state = CombatManager.Instance?.DebugOnlyGetState();
            int idx = FindMyCreatureIndex(me, state);
            if (idx < 0) return;
            TaskHelper.RunSafely(ApplyBuffsWhenReadyAsync(me, idx, todo, reason));
        }
        catch (Exception e)
        {
            ModEntry.Logger.Warn("RequestApplyUnappliedBuffs error: " + e);
        }
    }

    private static int FindMyCreatureIndex(Player me, MegaCrit.Sts2.Core.Combat.CombatState? state)
    {
        if (state?.Creatures == null) return -1;
        for (int i = 0; i < state.Creatures.Count; i++)
        {
            var c = state.Creatures[i];
            if (c.IsPlayer && c.Player == me) return i;
        }
        return -1;
    }

    /// <summary>
    /// 等到真正的“玩家行动窗口”(PlayPhase)再施放 buff：
    /// TurnStarted 在敌人回合开始也会触发，那时动作系统未解锁，直接施放会挂起；玩家回合开始时同步器已切到 PlayPhase。
    /// </summary>
    private static async Task ApplyBuffsWhenReadyAsync(Player me, int targetIndex, List<(string Id, int Amount)> todo, string reason)
    {
        try
        {
            RunManager rm = RunManager.Instance;
            for (int i = 0; i < 150; i++)
            {
                if (!InCombat) return;
                if (rm?.ActionQueueSynchronizer?.CombatState == MegaCrit.Sts2.Core.Entities.Multiplayer.ActionSynchronizerCombatState.PlayPhase)
                {
                    break;
                }
                await Task.Delay(100);
            }
            if (!InCombat) return;
            if (rm?.ActionQueueSynchronizer?.CombatState != MegaCrit.Sts2.Core.Entities.Multiplayer.ActionSynchronizerCombatState.PlayPhase)
            {
                ModEntry.Logger.Warn("start buff: never reached PlayPhase, skip (" + reason + ")");
                return;
            }
            bool realMp = IsInRealMultiplayerRun;
            foreach (var b in todo)
            {
                if (realMp)
                {
                    if (rm?.ActionQueueSynchronizer == null) continue;
                    rm.ActionQueueSynchronizer.RequestEnqueue(new ConsoleCmdGameAction(me, $"power {b.Id} {b.Amount} {targetIndex}", true));
                }
                else
                {
                    await ApplyStartBuffSingleAsync(b.Id, b.Amount, targetIndex);
                }
                string key = PowerTypeKey(b.Id);
                int done = _appliedThisCombat.TryGetValue(key, out int d) ? d : 0;
                _appliedThisCombat[key] = done + b.Amount;
            }
            ModEntry.Logger.Info($"Start-of-combat buffs applied ({reason}): " + string.Join(",", todo.Select(x => x.Id)));
            // 验证：打印本机玩家身上已生效的增益层数（用于排查是否真正加上）
            try
            {
                var st = CombatManager.Instance?.DebugOnlyGetState();
                if (st != null)
                {
                    foreach (var c in st.Creatures)
                    {
                        if (!c.IsPlayer || c.Player != me) continue;
                        foreach (var pm in c.Powers)
                        {
                            ModEntry.Logger.Info($"[sk6] me has power {pm.Id.Entry} amount={pm.Amount}");
                        }
                    }
                }
            }
            catch (Exception e)
            {
                ModEntry.Logger.Warn("[sk6] verify powers read failed: " + e);
            }
        }
        catch (Exception e)
        {
            ModEntry.Logger.Warn("apply start buffs failed: " + e);
        }
    }

    /// <summary>单机/伪多人：直接执行同款 power 命令并等待其任务完成（联机走同步队列由框架等待）。</summary>
    private static async Task ApplyStartBuffSingleAsync(string id, int amount, int targetIndex)
    {
        try
        {
            var st = CombatManager.Instance?.DebugOnlyGetState();
            if (st == null) return;
            var creatures = st.Creatures;
            if (targetIndex < 0 || targetIndex >= creatures.Count)
            {
                ModEntry.Logger.Warn("[sk6] bad target index " + targetIndex);
                return;
            }
            var creature = creatures[targetIndex];
            Type? t = FindPowerType(id);
            if (t == null)
            {
                ModEntry.Logger.Warn("[sk6] no power type for " + id);
                return;
            }
            PowerModel template = ModelDb.DebugPower(t);
            ModEntry.Logger.Info($"[sk6] apply {id} realId={template.Id.Entry} x{amount} -> creature idx {targetIndex} isPlayer={creature.IsPlayer}");
            var ctx = new BlockingPlayerChoiceContext();
            var existing = creature.Powers.FirstOrDefault(p => p.GetType() == t);
            Task task = (template.InstanceType == PowerInstanceType.None && existing != null)
                ? PowerCmd.ModifyAmount(ctx, existing, amount, null, null)
                : PowerCmd.Apply(ctx, template.ToMutable(), creature, amount, null, null);
            if (task != null)
            {
                await task;
            }
            var after = creature.Powers.Where(p => p.GetType() == t).ToList();
            ModEntry.Logger.Info("[sk6] result: matches=" + after.Count + " amounts=" + string.Join(",", after.Select(p => p.Amount)));
        }
        catch (Exception e)
        {
            ModEntry.Logger.Warn("[sk6] direct apply error: " + e);
        }
    }

    private static async Task DeliverLootAfterCombatAsync(int amount)
    {
        // 等彻底离开战斗（官方限制：战斗中不能发加金消息）
        for (int i = 0; i < 60 && InCombat; i++)
        {
            await Task.Delay(50);
        }
        if (InCombat) return;
        try
        {
            string r = await GiveGoldAsync(amount);
            ModEntry.Logger.Info("Loot per fight: " + r);
        }
        catch (Exception e)
        {
            ModEntry.Logger.Warn("loot per fight failed: " + e);
        }
    }

    private static async Task DeliverWishAfterCombatAsync(string wish)
    {
        // 等彻底离开战斗（官方限制：战斗中不能发）
        for (int i = 0; i < 40 && InCombat; i++)
        {
            await Task.Delay(50);
        }
        if (InCombat) return;
        try
        {
            string result = await GiveCardAsync(wish);
            if (result.StartsWith("错误")) return; // 保留愿望卡，之后可手动 stealth give
            Config.WishCardId = "";
            Config.Save();
            ModEntry.Logger.Info("Wish card delivered after combat: " + wish);
        }
        catch (Exception e)
        {
            ModEntry.Logger.Warn("wish delivery failed: " + e);
        }
    }

    private static void Note(string kind, string what)
    {
        _log.Add($"[{DateTime.Now:HH:mm:ss}] {kind} {what}");
        if (_log.Count > 30) _log.RemoveAt(0);
    }

    public static string Status()
    {
        RunManager rm = RunManager.Instance;
        string net = rm?.NetService == null ? "无对局" : rm.NetService.Type.ToString();
        string wish = "";
        try { wish = GetInjectWish(Me?.NetId ?? 0); } catch (Exception) { }
        return "StealthKit 状态\n" +
               "  网络: " + net + " | 对局中: " + (rm?.IsInProgress == true) + "\n" +
               "  奖励愿望(三选一必含): " + (wish == "" ? "(无)" : wish) + "\n" +
               "  每场自动加金(loot): " + (_lootPerFight > 0 ? "+" + _lootPerFight : "关") + "\n" +
               "  火堆自动回复: " + (Config.AutoRestHeal ? "开" : "关") + "\n" +
               "  最近动作: " + (_log.Count == 0 ? "(无)" : string.Join(" | ", _log.TakeLast(5)));
    }

    public static string Help()
    {
        return "StealthKit 用法（不用记 ID！支持中文/英文名搜索）\n" +
               "  stealth give card|relic|potion <名称>   搜到唯一→直接悄悄拿；多条→列编号\n" +
               "  stealth give gold <数额>               直接加/扣金币\n" +
               "  stealth buy <卡名> [价格]              商店式‘购买’伪装：真扣钱+记录花销，\n" +
               "                                          队友端记录=花销+获得，与真实购买一致(建议商店层用)\n" +
               "  stealth loot <每场金币数|off>          每场战斗后自动同步加金，数额合并进该层\n" +
               "                                          GoldGained 合计，队友看不出其中多少是贪污的\n" +
               "  stealth wishcard <卡名>                把下一层卡牌奖励的“第三张”换成这张卡\n" +
               "                                          （提前把随机变成必然，队友画面/历史天然一致）\n" +
               "  stealth wishcard off                   取消（恢复随机）\n" +
               "  stealth find card|relic|potion <关键字> 只搜索列编号（也可以直接 give 搜）\n" +
               "  stealth take <编号>                    领取列表里的第 N 项\n" +
               "  stealth rest                          休息点立刻回复一次(等同按休息按钮)\n" +
               "  stealth autoheal on|off                进休息点自动回复\n" +
               "  stealth status | stealth help\n" +
               "举例: stealth wishcard 献祭   /   stealth give card 死亡收割   /   stealth give gold 999\n" +
               "重要: 愿望注入(把奖励第三张换成指定卡)需要【全体联机者都启用 StealthKit】且版本一致；\n" +
               "      give(直接送卡/遗物/金币)则只需要你自己装。";
    }

    // ============ 注入式愿望（v0.4）：把“下一层三选一”的随机变成必然 ============
    // 原理：每位玩家每层的卡牌奖励由全队各端用同一随机种子生成；
    // 本模组在全队每端都执行同一“替换最后一格”逻辑，所以换牌在各端一致、不会不同步。
    // 前提：全队都启用 StealthKit v0.4，否则没装的端不会换牌 → 选卡按序号会不同 → 掉线。
    private static readonly Dictionary<ulong, string> _injectWishes = new();
    private static FieldInfo _cardRewardCardsField;

    public static string GetInjectWish(ulong playerNetId)
        => playerNetId != 0 && _injectWishes.TryGetValue(playerNetId, out var v) ? v : "";

    /// <summary>本机设置/清除某玩家的注入愿望（单机时直接调用；联机时由 wishset 命令在每台机器上各自调用）。</summary>
    public static void SetInjectWish(ulong playerNetId, string cardIdOrEmpty)
    {
        if (string.IsNullOrWhiteSpace(cardIdOrEmpty))
        {
            _injectWishes.Remove(playerNetId);
        }
        else
        {
            _injectWishes[playerNetId] = cardIdOrEmpty.ToUpperInvariant();
        }
    }

    /// <summary>请求把愿望写入全队：联机走官方控制台命令队列广播，单机直接写入。</summary>
    public static string RequestWish(string cardId, bool isClear)
    {
        if (RunManager.Instance == null || !RunManager.Instance.IsInProgress) return "错误：没有进行中的对局";
        Player me = Me;
        if (me == null) return "错误：找不到本机玩家";
        string netCmd = isClear ? "wishset off" : ("wishset " + cardId.ToUpperInvariant());
        if (RunManager.Instance.IsSingleplayerOrFakeMultiplayer)
        {
            SetInjectWish(me.NetId, isClear ? "" : cardId);
            return isClear ? "愿望已清除（本局三选一恢复随机）" : $"已设置：本局接下来的卡牌奖励会包含 {cardId}";
        }
        RunManager rm = RunManager.Instance;
        if (rm.ActionQueueSynchronizer == null) return "错误：动作同步器不可用";
        rm.ActionQueueSynchronizer.RequestEnqueue(new ConsoleCmdGameAction(me, netCmd, CombatManager.Instance.IsInProgress));
        return isClear
            ? "已向全队广播清除愿望"
            : $"已向全队广播愿望 {cardId}。\n[注意] 三选一换牌是全队同步执行的，所有联机者都必须启用 StealthKit v0.4，否则会数据不同步。";
    }

    /// <summary>由 CardReward.Populate 之后调用：把该玩家奖励牌的最后一格换成愿望卡（全队各端一致执行）。</summary>
    public static void InjectWishIntoReward(CardReward reward)
    {
        try
        {
            if (reward == null || reward.Player == null) return;
            string wishId = GetInjectWish(reward.Player.NetId);
            if (wishId.Length == 0) return;
            _cardRewardCardsField ??= typeof(CardReward).GetField("_cards", BindingFlags.Instance | BindingFlags.NonPublic);
            if (_cardRewardCardsField?.GetValue(reward) is not System.Collections.IList cards) return;
            int n = cards.Count;
            if (n < 3) return;
            var last = cards[n - 1] as CardCreationResult;
            if (last == null) return;
            if (last.Card != null && last.Card.Id.Entry == wishId) return; // 已替换过
            CardModel template = FindCard(wishId);
            if (template == null) return;
            RunState state = RunManager.Instance?.DebugOnlyGetState();
            if (state == null) return;
            CardModel created = state.CreateCard(template, reward.Player);
            last.ModifyCard(created);
            ModEntry.Logger.Info($"Injected wish card {wishId} into reward slot {n - 1} for player {reward.Player.NetId}");
        }
        catch (Exception e)
        {
            ModEntry.Logger.Warn("inject wish error: " + e);
        }
    }

    // ============ 历史美化：把“本层跳过3张”改写成“获得愿望卡+跳过2张”（仅本机记录） ============
    private static CardModel _pendingSkipCard;
    private static object _pendingSkipNode;

    /// <summary>成功给本地玩家加卡后登记：若随后本层出现“三张全跳过”的卡牌奖励，就把本地历史改写成拿到了这张卡。</summary>
    public static void ArmSkipBeautify(CardModel card)
    {
        try
        {
            if (card == null) return;
            _pendingSkipCard = card;
            _pendingSkipNode = Me?.RunState?.CurrentMapPointHistoryEntry;
        }
        catch (Exception)
        {
            _pendingSkipCard = null;
        }
    }

    /// <summary>由 Harmony 在 CardReward.OnSkipped 之后调用（仅处理本机玩家被跳过的卡牌奖励）。</summary>
    public static void BeautifySkippedReward(CardReward reward)
    {
        try
        {
            if (_pendingSkipCard == null) return;
            Player me = Me;
            if (me == null || reward == null || reward.Player != me) return;
            IRunState state = me.RunState;
            if (state?.CurrentMapPointHistoryEntry == null) return;
            if (_pendingSkipNode != null && !ReferenceEquals(_pendingSkipNode, state.CurrentMapPointHistoryEntry)) return;

            PlayerMapPointHistoryEntry entry = state.CurrentMapPointHistoryEntry.GetEntry(me.NetId);
            if (entry?.CardChoices == null) return;
            List<CardChoiceHistoryEntry> choices = entry.CardChoices;
            if (choices.Count != 3 || choices.Any(c => c.wasPicked)) return; // 只处理“三张全跳过”

            // 改写成“获得了愿望卡 + 跳过另外两张”，与本层真实三选一的数量一致
            var keep = choices.Take(2).ToList();
            choices.Clear();
            choices.Add(new CardChoiceHistoryEntry(_pendingSkipCard, wasPicked: true));
            choices.AddRange(keep);
            _pendingSkipCard = null;
            _pendingSkipNode = null;
            ModEntry.Logger.Info("Local history beautified: wished card shown as picked on this floor.");
        }
        catch (Exception e)
        {
            ModEntry.Logger.Warn("beautify skip reward error: " + e);
        }
    }

    // ============ 火堆自动回复 ============
    private static bool _autoRestScheduled;

    public static void ScheduleAutoRest()
    {
        if (!Config.AutoRestHeal) return;
        if (_autoRestScheduled) return;
        _autoRestScheduled = true;
        TaskHelper.RunSafely(AutoRestAsync());
    }

    private static async Task AutoRestAsync()
    {
        try
        {
            await Task.Delay(900);
            _autoRestScheduled = false;
            if (RunManager.Instance == null || !RunManager.Instance.IsInProgress) return;
            if (InCombat) return;
            Player me = Me;
            if (me == null) return;
            var sync = RunManager.Instance.RestSiteSynchronizer;
            if (sync == null) return;
            var options = sync.GetLocalOptions();
            int idx = -1;
            for (int i = 0; i < options.Count; i++)
            {
                if (options[i].OptionId == "HEAL") { idx = i; break; }
            }
            if (idx < 0) return;
            ModEntry.Logger.Info("StealthKit auto rest-heal triggered.");
            await sync.ChooseLocalOption(idx);
            Note("autoRestHeal", "HEAL");
        }
        catch (Exception e)
        {
            ModEntry.Logger.Warn("auto rest heal error: " + e);
        }
    }

    // ============ 免记 ID：中文/关键字搜索 + 编号选择 ============
    public enum ItemKind { Card, Relic, Potion }

    public static string KindLabel(ItemKind k) => k switch
    {
        ItemKind.Card => "卡牌",
        ItemKind.Relic => "遗物",
        _ => "药水",
    };

    /// <summary>候选结果缓存：stealth find 后可用 stealth take &lt;n&gt; 直接领取/设为愿望。</summary>
    public static List<ItemCandidate> Pending = new();
    public static string PendingMode = ""; // "give" | "wishcard"

    public class ItemCandidate
    {
        public ItemKind Kind;
        public string Id;
        public string Title;
        public override string ToString() => $"{Title} ({Id})";
    }

    private static string TitleOf(ItemKind kind, AbstractModel m)
    {
        try
        {
            return kind switch
            {
                ItemKind.Card => ((CardModel)m).Title,
                ItemKind.Relic => ((RelicModel)m).Title.GetFormattedText(),
                _ => ((PotionModel)m).Title.GetFormattedText(),
            };
        }
        catch (Exception)
        {
            return m.Id.Entry;
        }
    }

    /// <summary>按 中文标题/英文ID 模糊搜索（≤10 条，按匹配度排序）。</summary>
    public static List<ItemCandidate> Search(ItemKind kind, string text)
    {
        string q = (text ?? "").Trim();
        string qu = q.ToUpperInvariant();
        List<AbstractModel> all = kind switch
        {
            ItemKind.Card => ModelDb.AllCards.Cast<AbstractModel>().ToList(),
            ItemKind.Relic => ModelDb.AllRelics.Cast<AbstractModel>().ToList(),
            _ => ModelDb.AllPotions.Cast<AbstractModel>().ToList(),
        };
        var scored = new List<(int score, ItemCandidate c)>();
        foreach (AbstractModel m in all)
        {
            string id = m.Id.Entry;
            string title = TitleOf(kind, m);
            string idU = id.ToUpperInvariant();
            string titleL = title.ToLowerInvariant();
            string ql = q.ToLowerInvariant();
            int score = -1;
            if (qu.Length > 0 && idU == qu) score = 100;
            else if (qu.Length > 0 && idU.StartsWith(qu, StringComparison.Ordinal)) score = 90;
            else if (q.Length > 0 && titleL == ql) score = 80;
            else if (q.Length > 0 && titleL.Contains(ql)) score = 60 + Math.Min(ql.Length * 5, 20);
            else if (qu.Length > 0 && idU.Contains(qu)) score = 40;
            if (score >= 0 && !string.IsNullOrEmpty(id))
            {
                scored.Add((score, new ItemCandidate { Kind = kind, Id = id, Title = title }));
            }
        }
        scored.Sort((a, b) => b.Item1.CompareTo(a.Item1));
        return scored.Take(10).Select(s => s.Item2).ToList();
    }

    public static void ShowCandidates(string header, List<ItemCandidate> list)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(header);
        int i = 1;
        foreach (ItemCandidate c in list)
        {
            sb.AppendLine($"  {i}. {c.Title}  [{c.Id}]");
            i++;
        }
        sb.Append("输入 stealth take <编号> 即可" + (PendingMode == "wish" ? "设为愿望(奖励必含)" : "直接获得"));
        LastConsoleMessage = sb.ToString();
    }

    /// <summary>最近一条要展示给控制台的消息（异步结果用）。</summary>
    public static string LastConsoleMessage = "";

    /// <summary>把一行文字追加到游戏控制台输出框（任务在主线完成时调用，安全）。</summary>
    public static void PrintToConsole(bool success, string text)
    {
        try
        {
            NDevConsole console = NDevConsole.Instance;
            FieldInfo f = typeof(NDevConsole).GetField("_outputBuffer", BindingFlags.Instance | BindingFlags.NonPublic);
            object rtl = f?.GetValue(console);
            if (rtl == null) return;
            var prop = rtl.GetType().GetProperty("Text");
            if (prop == null) return;
            string cur = (string)prop.GetValue(rtl) ?? "";
            string line = success ? ("[StealthKit] " + text) : $"[color=#ff5555][StealthKit] {text}[/color]";
            prop.SetValue(rtl, cur + line + "\n");
        }
        catch (Exception)
        {
            // 控制台未创建/已关闭时静默（结果仍会进游戏日志）
        }
    }

    /// <summary>异步执行一条会返回结果文本的任务；完成后把真实结果打进控制台与日志。</summary>
    public static void RunReported(Task<string> op, string doing)
    {
        TaskHelper.RunSafely(ReportAsync(op, doing));
    }

    private static async Task ReportAsync(Task<string> op, string doing)
    {
        try
        {
            string result = await op;
            ModEntry.Logger.Info(result);
            PrintToConsole(true, result);
        }
        catch (Exception e)
        {
            ModEntry.Logger.Warn(doing + " 失败: " + e);
            PrintToConsole(false, doing + " 失败: " + e.Message);
        }
    }

    /// <summary>把“搜索/选择/直发”统一入口：text 命 0 条报错；1 条直接执行；多条列出等 take。</summary>
    public static string Resolve(ItemKind kind, string text, string mode)
    {
        var hits = Search(kind, text);
        if (hits.Count == 0)
        {
            return $"没找到{KindLabel(kind)}：{text}（可只输一部分，如 献/格/血）";
        }
        if (hits.Count == 1)
        {
            return ExecuteOne(kind, hits[0].Id, mode);
        }
        Pending = hits;
        PendingMode = mode;
        return ListResult(hits, mode);
    }

    /// <summary>sk 4：跨 卡/遗物/药水 一次搜索，命中唯一直接给；多个就列编号。</summary>
    public static string ResolveAll(string text)
    {
        var hits = new List<ItemCandidate>();
        foreach (ItemKind k in new[] { ItemKind.Card, ItemKind.Relic, ItemKind.Potion })
        {
            hits.AddRange(Search(k, text));
        }
        var dedup = hits.GroupBy(h => h.Kind + "|" + h.Id).Select(g => g.First()).ToList();
        if (dedup.Count == 0) return $"没找到：{text}（可只输一部分，如 献/血/火）";
        if (dedup.Count == 1) return ExecuteOne(dedup[0].Kind, dedup[0].Id, "give");
        Pending = dedup;
        PendingMode = "give";
        return ListResult(dedup, "give");
    }

    private static string ListResult(List<ItemCandidate> hits, string mode)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"找到 {hits.Count} 个：");
        int idx = 1;
        foreach (ItemCandidate c in hits)
        {
            sb.AppendLine($"  {idx}. [{KindLabel(c.Kind)}] {c.Title}  ({c.Id})");
            idx++;
        }
        sb.Append("stealth take <编号>" + (mode == "natural" ? " 设为自然愿望" : mode == "wish" ? " 设为三选一愿望" : " 直接获得"));
        return sb.ToString();
    }

    /// <summary>执行单条：mode=wish 把该卡变成“下一层三选一必然包含”（全队注入）；mode=give 立即给。</summary>
    public static string ExecuteOne(ItemKind kind, string id, string mode)
    {
        if (mode == "wish")
        {
            if (kind != ItemKind.Card)
            {
                return "愿望注入只支持卡牌（遗物/药水请用 give 直接送）";
            }
            return RequestWish(id, isClear: false);
        }
        if (mode == "buy")
        {
            if (kind != ItemKind.Card)
            {
                return "buy 伪装只支持卡牌";
            }
            RunReported(BuyCardAsync(id, null), "商店购买伪装");
            return $"正在按商店流程‘购买’{id}…（完成后结果会自动显示在下方）";
        }
        if (mode == "natural")
        {
            if (kind != ItemKind.Card) return "自然落地只支持卡牌";
            return SetNaturalWish(id);
        }
        // give 走异步：完成后真实结果会自动追加到控制台
        switch (kind)
        {
            case ItemKind.Card:
                RunReported(GiveCardAsync(id), "送卡");
                return $"正在把卡牌 {id} 悄悄加入牌组…（完成后会在下方显示结果）";
            case ItemKind.Relic:
                RunReported(GiveRelicAsync(id), "送遗物");
                return $"正在获得遗物 {id}…（完成后会在下方显示结果）";
            default:
                RunReported(GivePotionAsync(id), "送药水");
                return $"正在获得药水 {id}…（完成后会在下方显示结果）";
        }
    }
}
