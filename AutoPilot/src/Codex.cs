using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace AutoPilot;

/// <summary>
/// 权威游戏数据知识库（编译自 <c>ptrlrd/spire-codex</c>，版本 v0.111.0 中文数据）。
///
/// 与 <see cref="RunStats"/> 的分工：
///   · <c>RunStats</c>（sts2_kb.json）= **593 局真实对局统计** → 回答"这牌**好不好**"（价值/选择率/协同）
///   · <c>Codex</c>（sts2_codex.json，本类）= **游戏本体权威数据** → 回答"这牌/这怪/这事件**是什么**"
///
/// 核心用途：
///   ① **怪物招式伤害表** —— 精确的每招伤害/格挡数值，供战损计算交叉校验（意图 API 读不到时兜底、可读时对照）
///   ② **事件选项表** —— 事件房每个选项的官方描述，直接喂给大模型做事件决策，不再靠模型凭记忆瞎猜
///   ③ **卡牌权威数值/升级增量** —— 选牌评分与"是否值得升级"判断的权威依据
///   ④ **遗物/药水描述** —— 提示词里给出准确效果文本，减少大模型幻觉
///
/// 数据体积约 330KB（紧凑数组编码），随 mod 一起分发在 <c>kb/sts2_codex.json</c>。
/// </summary>
public static class Codex
{
    private static bool _loaded;
    private static bool _loadTried;

    // ---- 卡牌：[名称, 费用, 类型, 稀有度, 职业, 描述, 升级描述, vars, 关键词, 标签, 段数, 抽牌, 得能量, 目标, 升级增量, 施加状态, 生成卡, X费, 联机限定] ----
    private static readonly Dictionary<string, JsonElement> _cards = new(StringComparer.OrdinalIgnoreCase);

    // ---- 怪物：[名称, 类型, minHp, maxHp, minHpAsc, maxHpAsc, moves[], notes] ----
    private static readonly Dictionary<string, JsonElement> _monsters = new(StringComparer.OrdinalIgnoreCase);

    // ---- 事件：[名称, 幕, 类型, 描述, options[]] ----
    private static readonly Dictionary<string, JsonElement> _events = new(StringComparer.OrdinalIgnoreCase);

    // ---- 遗物：[名称, 稀有度, 描述, 商店价, 池] ----
    private static readonly Dictionary<string, JsonElement> _relics = new(StringComparer.OrdinalIgnoreCase);

    // ---- 药水：[名称, 稀有度, 描述] ----
    private static readonly Dictionary<string, JsonElement> _potions = new(StringComparer.OrdinalIgnoreCase);

    // ---- 能力：[名称, 类型, 描述] ----
    private static readonly Dictionary<string, JsonElement> _powers = new(StringComparer.OrdinalIgnoreCase);

    // ---- 遭遇：[名称, 房间类型, 幕, 怪物 ids] ----
    private static readonly Dictionary<string, JsonElement> _encounters = new(StringComparer.OrdinalIgnoreCase);

    public static bool Ready => _loaded;
    public static int CardCount => _cards.Count;
    public static int MonsterCount => _monsters.Count;
    public static int EventCount => _events.Count;
    public static int RelicCount => _relics.Count;
    public static int PotionCount => _potions.Count;
    public static int PowerCount => _powers.Count;
    public static int EncounterCount => _encounters.Count;

    // =====================================================================
    //  加载
    // =====================================================================
    public static void EnsureLoaded()
    {
        if (_loaded || _loadTried) return;
        _loadTried = true;
        try
        {
            string path = FindPath();
            if (path == null)
            {
                Pilot.Log("[Codex] 未找到 sts2_codex.json，跳过权威数据。");
                return;
            }
            using var doc = JsonDocument.Parse(File.ReadAllText(path, System.Text.Encoding.UTF8));
            var root = doc.RootElement;
            // ⚠ JsonDocument 释放后 JsonElement 失效 → 克隆根元素常驻内存。
            //   这里改为手动 Clone 每个条目（JsonElement.Clone() 返回脱离文档的副本）。
            Fill(root, "cards", _cards);
            Fill(root, "monsters", _monsters);
            Fill(root, "events", _events);
            Fill(root, "relics", _relics);
            Fill(root, "potions", _potions);
            Fill(root, "powers", _powers);
            Fill(root, "encounters", _encounters);
            _loaded = true;
            Pilot.Log($"[Codex] 已载入权威数据：{CardCount} 卡 / {MonsterCount} 怪 / {EventCount} 事件 / {RelicCount} 遗物 / {PotionCount} 药水 / {PowerCount} 能力 / {EncounterCount} 遭遇");
        }
        catch (Exception ex)
        {
            Pilot.Log($"[Codex] 载入失败（不影响游戏）：{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void Fill(JsonElement root, string key, Dictionary<string, JsonElement> dst)
    {
        if (!root.TryGetProperty(key, out var sec) || sec.ValueKind != JsonValueKind.Object) return;
        foreach (var kv in sec.EnumerateObject())
        {
            try { dst[kv.Name] = kv.Value.Clone(); } catch { }
        }
    }

    private static string FindPath()
    {
        var cands = new List<string>();
        try
        {
            string asmDir = Path.GetDirectoryName(typeof(Codex).Assembly.Location) ?? "";
            if (asmDir.Length > 0)
            {
                cands.Add(Path.Combine(asmDir, "kb", "sts2_codex.json"));
                cands.Add(Path.Combine(asmDir, "sts2_codex.json"));
            }
            cands.Add(Path.Combine("mods", "AutoPilot", "kb", "sts2_codex.json"));
        }
        catch { }
        foreach (var c in cands)
        {
            try { if (File.Exists(c)) return c; } catch { }
        }
        return null;
    }

    // =====================================================================
    //  一、卡牌
    // =====================================================================
    public sealed class CardInfo
    {
        public string Id = "";
        public string Name = "";
        public int Cost;
        public string Type = "";      // 攻击 / 技能 / 能力 / 诅咒 / 状态 / 任务
        public string Rarity = "";    // 基础 / 普通 / 罕见 / 稀有 / 先古 / 事件 / 衍生 …
        public string Color = "";     // ironclad / silent / defect / watcher / colorless …
        public string Description = "";
        public string UpgradeDescription = "";
        public string Vars = "";      // "Damage=8; VulnerablePower=2"
        public string Keywords = "";
        public string Tags = "";
        public int Hits;
        public int Draw;
        public int EnergyGain;
        public string Target = "";
        public string UpgradeDelta = "";
        public string PowersApplied = "";
        public bool XCost;
    }

    /// <summary>按卡牌 id（如 <c>BASH</c> / <c>STRIKE_IRONCLAD</c>）查权威资料。</summary>
    public static CardInfo Card(string cardId)
    {
        EnsureLoaded();
        if (string.IsNullOrEmpty(cardId)) return null;
        string id = Norm(cardId);
        if (!_cards.TryGetValue(id, out var e)) return null;
        try
        {
            var info = new CardInfo
            {
                Id = id,
                Name = Str(e, 0),
                Cost = Int(e, 1),
                Type = Str(e, 2),
                Rarity = Str(e, 3),
                Color = Str(e, 4),
                Description = Str(e, 5),
                UpgradeDescription = Str(e, 6),
                Hits = Int(e, 10),
                Draw = Int(e, 11),
                EnergyGain = Int(e, 12),
                Target = Str(e, 13),
                XCost = Bool(e, 17),
            };
            info.Vars = FlatVars(e, 7);
            info.Keywords = FlatArr(e, 8);
            info.Tags = FlatArr(e, 9);
            info.UpgradeDelta = FlatVars(e, 14);
            info.PowersApplied = FlatPowers(e, 15);
            return info;
        }
        catch { return null; }
    }

    /// <summary>给提示词用的单卡权威文本（无数据返回 null）。</summary>
    public static string CardText(string cardId, bool upgraded = false)
    {
        var c = Card(cardId);
        if (c == null) return null;
        var sb = new System.Text.StringBuilder();
        sb.Append(c.Name).Append('（').Append(c.Type).Append('/').Append(c.Rarity);
        if (c.Cost >= 0) sb.Append('/').Append(c.Cost).Append("费");
        else if (c.XCost) sb.Append("/X费");
        sb.Append("）");
        string desc = upgraded && !string.IsNullOrEmpty(c.UpgradeDescription) ? c.UpgradeDescription : c.Description;
        if (!string.IsNullOrEmpty(desc)) sb.Append("：").Append(desc);
        if (upgraded && !string.IsNullOrEmpty(c.UpgradeDelta)) sb.Append("〔升级增益 ").Append(c.UpgradeDelta).Append("〕");
        return sb.ToString();
    }

    // =====================================================================
    //  二、怪物招式 —— 战损计算的核心
    // =====================================================================
    public sealed class MoveInfo
    {
        public string Name = "";
        public string Intent = "";
        public string Damage = "";     // "22" / "22/26" / "11x2"
        public int Block;
        public string Description = "";
        public bool IsAttack => !string.IsNullOrEmpty(Damage) && Damage != "0";
    }

    public sealed class MonsterInfo
    {
        public string Id = "";
        public string Name = "";
        public string Type = "";       // Boss / Elite / Normal / …
        public int MinHp, MaxHp;
        public int MinHpAsc, MaxHpAsc;
        public List<MoveInfo> Moves = new();
        public string Notes = "";
    }

    public static MonsterInfo Monster(string monsterId)
    {
        EnsureLoaded();
        if (string.IsNullOrEmpty(monsterId)) return null;
        string id = Norm(monsterId);
        if (!_monsters.TryGetValue(id, out var e)) return null;
        try
        {
            var m = new MonsterInfo
            {
                Id = id,
                Name = Str(e, 0),
                Type = Str(e, 1),
                MinHp = Int(e, 2),
                MaxHp = Int(e, 3),
                MinHpAsc = Int(e, 4),
                MaxHpAsc = Int(e, 5),
                Notes = Str(e, 7),
            };
            if (e.ValueKind == JsonValueKind.Array && e.GetArrayLength() > 6 &&
                e[6].ValueKind == JsonValueKind.Array)
            {
                foreach (var mv in e[6].EnumerateArray())
                {
                    m.Moves.Add(new MoveInfo
                    {
                        Name = Str(mv, 0),
                        Intent = Str(mv, 1),
                        Damage = Str(mv, 2),
                        Block = Int(mv, 3),
                        Description = Str(mv, 4),
                    });
                }
            }
            return m;
        }
        catch { return null; }
    }

    /// <summary>怪物血量的权威文本（"512" 或 "512-540" 或 "512/535（进阶）"）。</summary>
    public static string MonsterHpText(string monsterId)
    {
        var m = Monster(monsterId);
        if (m == null) return null;
        string s;
        if (m.MaxHp > 0 && m.MaxHp != m.MinHp) s = $"{m.MinHp}-{m.MaxHp}";
        else s = $"{m.MinHp}";
        if (m.MinHpAsc > 0 && m.MinHpAsc != m.MinHp)
        {
            s += m.MaxHpAsc > 0 && m.MaxHpAsc != m.MinHpAsc ? $"/{m.MinHpAsc}-{m.MaxHpAsc}（进阶）" : $"/{m.MinHpAsc}（进阶）";
        }
        return s;
    }

    /// <summary>怪物的全部招式摘要（供提示词给出"它可能出什么招"）。</summary>
    public static string MonsterMovesText(string monsterId, int max = 6)
    {
        var m = Monster(monsterId);
        if (m == null || m.Moves.Count == 0) return null;
        var parts = m.Moves.Take(max).Select(mv =>
        {
            string d = "";
            if (!string.IsNullOrEmpty(mv.Damage)) d = $" {mv.Damage}伤害";
            if (mv.Block > 0) d += $" {mv.Block}格挡";
            return $"{mv.Name}({mv.Intent}{d})";
        });
        return string.Join(" / ", parts);
    }

    // =====================================================================
    //  三、事件选项 —— 事件房决策的直接依据
    // =====================================================================
    public sealed class EventOpt
    {
        public string Title = "";
        public string Description = "";
    }

    public sealed class EventInfo
    {
        public string Id = "";
        public string Name = "";
        public string Act = "";
        public string Type = "";
        public string Description = "";
        public List<EventOpt> Options = new();
    }

    public static EventInfo Event(string eventId)
    {
        EnsureLoaded();
        if (string.IsNullOrEmpty(eventId)) return null;
        string id = Norm(eventId);
        if (!_events.TryGetValue(id, out var e)) return null;
        try
        {
            var ev = new EventInfo
            {
                Id = id,
                Name = Str(e, 0),
                Act = Str(e, 1),
                Type = Str(e, 2),
                Description = Str(e, 3),
            };
            if (e.ValueKind == JsonValueKind.Array && e.GetArrayLength() > 4 && e[4].ValueKind == JsonValueKind.Array)
                foreach (var o in e[4].EnumerateArray())
                    ev.Options.Add(new EventOpt { Title = Str(o, 0), Description = Str(o, 1) });
            return ev;
        }
        catch { return null; }
    }

    /// <summary>事件的选项文本（"投身其中：获得2点最大生命值。受到3点伤害。" 逐行）。</summary>
    public static string EventOptionsText(string eventId)
    {
        var ev = Event(eventId);
        if (ev == null || ev.Options.Count == 0) return null;
        return string.Join("\n", ev.Options.Select(o =>
            string.IsNullOrEmpty(o.Description) ? $"· {o.Title}" : $"· {o.Title}：{o.Description}"));
    }

    // =====================================================================
    //  四、遗物 / 药水 / 能力 / 遭遇
    // =====================================================================
    public static string RelicText(string relicId)
    {
        EnsureLoaded();
        var e = Get(_relics, relicId);
        if (!e.HasValue) return null;
        var r = e.Value;
        string name = Str(r, 0), rarity = Str(r, 1), desc = Str(r, 2);
        int price = Int(r, 3);
        var sb = new System.Text.StringBuilder(name);
        if (!string.IsNullOrEmpty(rarity)) sb.Append("（").Append(rarity).Append('）');
        if (!string.IsNullOrEmpty(desc)) sb.Append("：").Append(desc);
        if (price > 0) sb.Append("〔商店价 ").Append(price).Append('〕');
        return sb.ToString();
    }

    public static string PotionText(string potionId)
    {
        EnsureLoaded();
        var e = Get(_potions, potionId);
        if (!e.HasValue) return null;
        var p = e.Value;
        string name = Str(p, 0), rarity = Str(p, 1), desc = Str(p, 2);
        return string.IsNullOrEmpty(desc) ? name : $"{name}（{rarity}）：{desc}";
    }

    public static string PowerText(string powerId)
    {
        EnsureLoaded();
        var e = Get(_powers, powerId);
        if (!e.HasValue) return null;
        var p = e.Value;
        string name = Str(p, 0), type = Str(p, 1), desc = Str(p, 2);
        return string.IsNullOrEmpty(desc) ? name : $"{name}（{type}）：{desc}";
    }

    /// <summary>该遭遇在场的怪物 id 列表。</summary>
    public static List<string> EncounterMonsters(string encounterId)
    {
        EnsureLoaded();
        var e = Get(_encounters, encounterId);
        if (!e.HasValue) return null;
        var r = e.Value;
        var list = new List<string>();
        try
        {
            if (r.ValueKind == JsonValueKind.Array && r.GetArrayLength() > 3 && r[3].ValueKind == JsonValueKind.Array)
                foreach (var x in r[3].EnumerateArray()) { var s = x.GetString(); if (!string.IsNullOrEmpty(s)) list.Add(s); }
        }
        catch { }
        return list;
    }

    // =====================================================================
    //  五、给大模型的批量提示词片段
    // =====================================================================

    /// <summary>为一批卡牌 id 生成权威描述段落（用于卡牌奖励三选一）。</summary>
    public static string CardsPrompt(IEnumerable<string> cardIds)
    {
        EnsureLoaded();
        if (cardIds == null) return "";
        var sb = new System.Text.StringBuilder();
        foreach (var id in cardIds)
        {
            string t = CardText(id);
            if (t != null) sb.Append("  · ").Append(t).Append('\n');
        }
        return sb.Length == 0 ? "" : "【卡牌官方数据】\n" + sb;
    }

    /// <summary>战斗提示词用的敌方招式与血量情报。</summary>
    public static string EnemiesPrompt(IEnumerable<string> monsterIds)
    {
        EnsureLoaded();
        if (monsterIds == null) return "";
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sb = new System.Text.StringBuilder();
        foreach (var id in monsterIds)
        {
            if (string.IsNullOrEmpty(id) || !seen.Add(Norm(id))) continue;
            var m = Monster(id);
            if (m == null) continue;
            sb.Append("  · ").Append(m.Name);
            if (!string.IsNullOrEmpty(m.Type)) sb.Append('[').Append(m.Type).Append(']');
            string hp = MonsterHpText(id);
            if (!string.IsNullOrEmpty(hp)) sb.Append(" HP ").Append(hp);
            string mv = MonsterMovesText(id);
            if (!string.IsNullOrEmpty(mv)) sb.Append(" 招式：").Append(mv);
            sb.Append('\n');
        }
        return sb.Length == 0 ? "" : "【敌方官方数据】\n" + sb;
    }

    // =====================================================================
    //  工具
    // =====================================================================
    private static JsonElement? Get(Dictionary<string, JsonElement> map, string id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        return map.TryGetValue(Norm(id), out var e) ? e : (JsonElement?)null;
    }

    private static string Norm(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        int i = s.LastIndexOf('.');
        if (i >= 0) s = s.Substring(i + 1);
        int j = s.IndexOf('+');
        if (j > 0) s = s.Substring(0, j);
        return s.Trim().ToUpperInvariant();
    }

    private static string Str(JsonElement e, int i)
    {
        try
        {
            if (e.ValueKind != JsonValueKind.Array || e.GetArrayLength() <= i) return "";
            var v = e[i];
            return v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "") : "";
        }
        catch { return ""; }
    }

    private static int Int(JsonElement e, int i)
    {
        try
        {
            if (e.ValueKind != JsonValueKind.Array || e.GetArrayLength() <= i) return 0;
            var v = e[i];
            if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n)) return n;
            if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out int n2)) return n2;
            return 0;
        }
        catch { return 0; }
    }

    private static bool Bool(JsonElement e, int i)
    {
        try
        {
            if (e.ValueKind != JsonValueKind.Array || e.GetArrayLength() <= i) return false;
            var v = e[i];
            return v.ValueKind == JsonValueKind.True;
        }
        catch { return false; }
    }

    private static string FlatVars(JsonElement e, int i)
    {
        try
        {
            if (e.ValueKind != JsonValueKind.Array || e.GetArrayLength() <= i) return "";
            var v = e[i];
            if (v.ValueKind != JsonValueKind.Object) return "";
            var parts = new List<string>();
            foreach (var kv in v.EnumerateObject())
            {
                if (kv.Value.ValueKind == JsonValueKind.Number && kv.Value.TryGetDouble(out double d))
                    parts.Add($"{kv.Name}={d:0.##}");
            }
            return string.Join("; ", parts);
        }
        catch { return ""; }
    }

    private static string FlatArr(JsonElement e, int i)
    {
        try
        {
            if (e.ValueKind != JsonValueKind.Array || e.GetArrayLength() <= i) return "";
            var v = e[i];
            if (v.ValueKind != JsonValueKind.Array) return "";
            return string.Join("/", v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()));
        }
        catch { return ""; }
    }

    private static string FlatPowers(JsonElement e, int i)
    {
        try
        {
            if (e.ValueKind != JsonValueKind.Array || e.GetArrayLength() <= i) return "";
            var v = e[i];
            if (v.ValueKind != JsonValueKind.Array) return "";
            var parts = new List<string>();
            foreach (var p in v.EnumerateArray())
            {
                string name = "";
                int amt = 0;
                try
                {
                    if (p.TryGetProperty("power", out var pn) && pn.ValueKind == JsonValueKind.String) name = pn.GetString();
                    if (p.TryGetProperty("amount", out var pa) && pa.ValueKind == JsonValueKind.Number && pa.TryGetInt32(out int a)) amt = a;
                }
                catch { }
                if (!string.IsNullOrEmpty(name)) parts.Add(amt != 0 ? $"{name}×{amt}" : name);
            }
            return string.Join("; ", parts);
        }
        catch { return ""; }
    }
}
