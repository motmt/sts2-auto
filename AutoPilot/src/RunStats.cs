using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace AutoPilot;

/// <summary>
/// 真实对局统计知识库（由 rundata 流水线从 593 局真实存档训练而来）。
///
/// 数据来源：
///   · saito23490/sts2-run-data issue#1 —— 5 个 history.zip，292 局
///   · 本机 %APPDATA%\SlayTheSpire2\...\history —— 163 局（schema 10，含 card_choices 标注）
///
/// 每条卡牌记录：
///   v    = 综合价值（角色内 z-score，约 -1.5 ~ +2.0）
///   pick = 真实玩家选择率（该牌出现在三选一候选时被选中的比例）
///   off  = 候选出现次数（样本量，越大越可信）
///   r    = 稀有度档（basic/common/uncommon/rare，按出现频次推定）
///   t    = 价值分档 S/A/B/C/D
///
/// 价值合成（详见 rundata/stage2b_value.py）：
///   0.45×生存增益(分层配对) + 0.35×选择率提升(稀有度分层基线) + 0.20×删牌回避
///
/// 注意：KB 是**统计先验**，不是绝对真理。用法是与本地机制评分融合，
/// 并在样本量 off 太小时自动降低权重（见 Blend）。
/// </summary>
public static class RunStats
{
    private static bool _loaded;
    private static bool _loadTried;

    private sealed class CardRec
    {
        public double V;
        public double Pick;
        public int Off;
        public string Rarity = "?";
        public string Tier = "?";
    }

    private static readonly Dictionary<string, CardRec> _cards = new(StringComparer.OrdinalIgnoreCase);      // 全局表
    private static readonly Dictionary<string, Dictionary<string, CardRec>> _byChar = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, double> _relicLift = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, int> _relicOff = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, List<(string Id, double Pmi, int N)>> _synergy = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> _removeHint = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, double> _upgradeHint = new(StringComparer.OrdinalIgnoreCase);

    public static int CardCount => _cards.Count;
    public static int RelicCount => _relicLift.Count;
    public static int CleanRuns { get; private set; }

    // ---------------------------------------------------------------------
    //  加载
    // ---------------------------------------------------------------------
    public static void EnsureLoaded()
    {
        if (_loaded || _loadTried) return;
        _loadTried = true;
        try
        {
            string path = FindKbPath();
            if (path == null)
            {
                Pilot.Log("[KB] 未找到 sts2_kb.json，跳过真实统计数据。");
                return;
            }
            string json = File.ReadAllText(path, System.Text.Encoding.UTF8);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("meta", out var meta) &&
                meta.TryGetProperty("clean_runs", out var cr) && cr.TryGetInt32(out int n))
                CleanRuns = n;

            // 全局表
            if (root.TryGetProperty("cards_global", out var cg) && cg.ValueKind == JsonValueKind.Object)
                foreach (var kv in cg.EnumerateObject())
                    _cards[kv.Name] = ParseCard(kv.Value);

            // 分角色表
            if (root.TryGetProperty("cards", out var cs) && cs.ValueKind == JsonValueKind.Object)
            {
                foreach (var ch in cs.EnumerateObject())
                {
                    if (ch.Value.ValueKind != JsonValueKind.Object) continue;
                    var map = new Dictionary<string, CardRec>(StringComparer.OrdinalIgnoreCase);
                    foreach (var kv in ch.Value.EnumerateObject())
                        map[kv.Name] = ParseCard(kv.Value);
                    _byChar[ch.Name] = map;
                }
            }

            // 遗物
            if (root.TryGetProperty("relics", out var rl) && rl.ValueKind == JsonValueKind.Object)
                foreach (var kv in rl.EnumerateObject())
                {
                    try
                    {
                        if (kv.Value.TryGetProperty("lift", out var lf) && lf.TryGetDouble(out double l)) _relicLift[kv.Name] = l;
                        if (kv.Value.TryGetProperty("off", out var of) && of.TryGetInt32(out int o)) _relicOff[kv.Name] = o;
                    }
                    catch { }
                }

            // 协同
            if (root.TryGetProperty("synergy", out var sy) && sy.ValueKind == JsonValueKind.Object)
                foreach (var kv in sy.EnumerateObject())
                {
                    if (kv.Value.ValueKind != JsonValueKind.Array) continue;
                    var list = new List<(string, double, int)>();
                    foreach (var e in kv.Value.EnumerateArray())
                    {
                        try
                        {
                            if (e.ValueKind == JsonValueKind.Array && e.GetArrayLength() >= 3)
                            {
                                string id = e[0].GetString() ?? "";
                                double pmi = e[1].GetDouble();
                                int nn = e[2].GetInt32();
                                if (id.Length > 0) list.Add((id, pmi, nn));
                            }
                        }
                        catch { }
                    }
                    if (list.Count > 0) _synergy[kv.Name] = list;
                }

            // 建议删除
            if (root.TryGetProperty("remove_hint", out var rh) && rh.ValueKind == JsonValueKind.Array)
                foreach (var e in rh.EnumerateArray())
                {
                    try { if (e.ValueKind == JsonValueKind.Array && e.GetArrayLength() >= 1) _removeHint.Add(e[0].GetString() ?? ""); }
                    catch { }
                }

            // 优先升级
            if (root.TryGetProperty("upgrade_hint", out var uh) && uh.ValueKind == JsonValueKind.Array)
                foreach (var e in uh.EnumerateArray())
                {
                    try
                    {
                        if (e.ValueKind == JsonValueKind.Array && e.GetArrayLength() >= 2)
                            _upgradeHint[e[0].GetString() ?? ""] = e[1].GetDouble();
                    }
                    catch { }
                }

            _loaded = true;
            Pilot.Log($"[KB] 已载入真实统计：{CleanRuns} 局 / {_cards.Count} 张全局卡 / {_byChar.Count} 个角色 / {_relicLift.Count} 件遗物 / {_synergy.Count} 张有协同对");
        }
        catch (Exception ex)
        {
            Pilot.Log($"[KB] 载入失败（不影响游戏）：{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static CardRec ParseCard(JsonElement e)
    {
        var r = new CardRec();
        try
        {
            if (e.TryGetProperty("v", out var v) && v.TryGetDouble(out double dv)) r.V = dv;
            if (e.TryGetProperty("pick", out var p) && p.TryGetDouble(out double dp)) r.Pick = dp;
            if (e.TryGetProperty("off", out var o) && o.TryGetInt32(out int io)) r.Off = io;
            if (e.TryGetProperty("r", out var rr)) r.Rarity = rr.GetString() ?? "?";
            if (e.TryGetProperty("t", out var t)) r.Tier = t.GetString() ?? "?";
        }
        catch { }
        return r;
    }

    private static string FindKbPath()
    {
        var cands = new List<string>();
        try
        {
            // mods/AutoPilot/kb/sts2_kb.json（与 dll 同目录下的 kb 子目录）
            string asmDir = Path.GetDirectoryName(typeof(RunStats).Assembly.Location) ?? "";
            if (asmDir.Length > 0)
            {
                cands.Add(Path.Combine(asmDir, "kb", "sts2_kb.json"));
                cands.Add(Path.Combine(asmDir, "sts2_kb.json"));
            }
            // 相对 mods 目录
            cands.Add(Path.Combine("mods", "AutoPilot", "kb", "sts2_kb.json"));
        }
        catch { }
        foreach (var c in cands)
        {
            try { if (File.Exists(c)) return c; } catch { }
        }
        return null;
    }

    // ---------------------------------------------------------------------
    //  查询
    // ---------------------------------------------------------------------
    public sealed class CardStat
    {
        public string Id = "";
        public double Value;
        public double PickRate;
        public int Sample;
        public string Tier = "?";
        public string Rarity = "?";
        public bool FromData;

        public double Confidence => Math.Min(1.0, Sample / 25.0);   // 样本 25+ 视为完全可信
    }

    /// <summary>查某张卡的真实统计。先按角色卡池，再回落全局表。</summary>
    public static CardStat Lookup(string cardId, string charId)
    {
        EnsureLoaded();
        if (string.IsNullOrEmpty(cardId)) return null;
        string id = Norm(cardId);

        CardRec rec = null;
        if (!string.IsNullOrEmpty(charId) && _byChar.TryGetValue(Norm(charId), out var map) && map.TryGetValue(id, out var r1))
            rec = r1;
        else if (_cards.TryGetValue(id, out var r2))
            rec = r2;

        if (rec == null) return null;
        return new CardStat
        {
            Id = id, Value = rec.V, PickRate = rec.Pick, Sample = rec.Off,
            Tier = rec.Tier, Rarity = rec.Rarity, FromData = true,
        };
    }

    /// <summary>分数修正：把真实统计价值折算成选牌分增量（约 -45 ~ +45）。</summary>
    public static double ScoreDelta(string cardId, string charId, out string note)
    {
        note = null;
        var st = Lookup(cardId, charId);
        if (st == null) return 0;
        // 按样本量缩放：样本少则保守
        double conf = 0.45 + 0.55 * st.Confidence;
        double delta = st.Value * 16.0 * conf;
        delta = Math.Clamp(delta, -45, 45);
        if (Math.Abs(delta) >= 8)
        {
            string dir = delta > 0 ? "高" : "低";
            note = $"真实统计：{st.Tier}档 价值{st.Value:+0.00;-0.00}（{st.Sample}次候选，选择率{st.PickRate:P0}，{dir}价值）";
        }
        return delta;
    }

    /// <summary>某张牌与"牌组已有牌"的真实协同强度（PMI 加权）。</summary>
    public static double SynergyWith(IEnumerable<string> deckIds, string cardId)
    {
        EnsureLoaded();
        if (!_synergy.TryGetValue(Norm(cardId), out var partners)) return 0;
        var deck = new HashSet<string>(deckIds?.Select(Norm) ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        double score = 0;
        foreach (var (id, pmi, n) in partners)
        {
            if (!deck.Contains(id)) continue;
            score += pmi * Math.Min(1.0, n / 12.0);
        }
        return score;
    }

    /// <summary>某张牌的真实协同伙伴（用于给 LLM 写理由）。</summary>
    public static string DescribeSynergy(IEnumerable<string> deckIds, string cardId, int max = 3)
    {
        EnsureLoaded();
        if (!_synergy.TryGetValue(Norm(cardId), out var partners)) return null;
        var deck = new HashSet<string>(deckIds?.Select(Norm) ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var hits = partners.Where(p => deck.Contains(p.Id)).Take(max).ToList();
        if (hits.Count == 0) return null;
        return "真实协同: " + string.Join(" / ", hits.Select(h => $"{h.Id}(PMI{h.Pmi:0.0})"));
    }

    public static double RelicValue(string relicId)
    {
        EnsureLoaded();
        if (string.IsNullOrEmpty(relicId)) return 0;
        string id = Norm(relicId);
        if (_relicLift.TryGetValue(id, out double l))
        {
            int off = _relicOff.TryGetValue(id, out var o) ? o : 0;
            double conf = 0.4 + 0.6 * Math.Min(1.0, off / 20.0);
            return l * conf;
        }
        return 0;
    }

    public static bool IsRemoveHint(string cardId) { EnsureLoaded(); return _removeHint.Contains(Norm(cardId)); }

    public static double UpgradeValue(string cardId)
    {
        EnsureLoaded();
        return _upgradeHint.TryGetValue(Norm(cardId), out var v) ? v : 0;
    }

    /// <summary>给 LLM 提示词用的紧凑知识段落。</summary>
    public static string PromptSummary(IReadOnlyList<string> optionIds, string charId, IEnumerable<string> deckIds)
    {
        EnsureLoaded();
        if (_cards.Count == 0 || optionIds == null) return "";
        var sb = new System.Text.StringBuilder();
        sb.Append("【真实对局统计参考（来自 ").Append(CleanRuns).Append(" 局真实存档）】\n");
        foreach (var id in optionIds)
        {
            var st = Lookup(id, charId);
            if (st == null) continue;
            sb.Append("  · ").Append(st.Id).Append("：").Append(st.Tier).Append("档，价值")
              .Append(st.Value.ToString("+0.00;-0.00")).Append("，玩家选择率")
              .Append((st.PickRate * 100).ToString("0")).Append("%（样本 ").Append(st.Sample).Append("）");
            string syn = DescribeSynergy(deckIds, st.Id, 2);
            if (syn != null) sb.Append("，").Append(syn);
            sb.Append('\n');
        }
        return sb.ToString();
    }

    private static string Norm(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        int i = s.LastIndexOf('.');
        return i >= 0 ? s.Substring(i + 1) : s;
    }
}
