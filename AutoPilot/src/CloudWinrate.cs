using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Models;

namespace AutoPilot;

/// <summary>
/// 云端胜率查询。
///
/// 设计说明：杀戮尖塔 2 官方没有公开的卡牌胜率 API，社区数据也不稳定，
/// 因此这里实现为一个**可插拔的 HTTP 客户端**：
///   · 未配置 endpoint → 完全静默，不影响任何决策（纯本地运行）
///   · 配置了 endpoint → 按 {endpoint}/card_winrate?ids=A,B,C 请求，期望返回
///     {"CARD_ID": 0.62, ...}（0~1 的胜率），据此给选卡加分
/// 结果按 session 缓存，并在后台线程请求，绝不阻塞游戏主线程。
/// </summary>
public static class CloudWinrate
{
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromMilliseconds(2500) };
    private static readonly Dictionary<string, double> _cache = new();
    private static readonly HashSet<string> _inflight = new();
    private static DateTime _lastAttempt = DateTime.MinValue;

    /// <summary>查询一批卡的胜率（异步预热缓存）。</summary>
    public static void Prefetch(IEnumerable<string> cardIds)
    {
        if (!PilotConfig.Current.EnableCloudWinrate) return;
        string endpoint = PilotConfig.Current.CloudEndpoint;
        if (string.IsNullOrWhiteSpace(endpoint)) return;

        var need = cardIds?.Where(id => !string.IsNullOrEmpty(id)
                                       && !_cache.ContainsKey(id)
                                       && !_inflight.Contains(id)).ToList();
        if (need == null || need.Count == 0) return;
        if ((DateTime.Now - _lastAttempt).TotalSeconds < 3) return; // 限流
        _lastAttempt = DateTime.Now;

        foreach (string id in need) _inflight.Add(id);
        string url = endpoint.TrimEnd('/') + "/card_winrate?ids=" + Uri.EscapeDataString(string.Join(",", need));

        _ = Task.Run(async () =>
        {
            try
            {
                _http.Timeout = TimeSpan.FromMilliseconds(Math.Max(500, PilotConfig.Current.CloudTimeoutMs));
                string json = await _http.GetStringAsync(url);
                using JsonDocument doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (JsonProperty prop in doc.RootElement.EnumerateObject())
                    {
                        if (prop.Value.ValueKind == JsonValueKind.Number)
                        {
                            double v = prop.Value.GetDouble();
                            if (v > 1.0) v /= 100.0; // 兼容百分数
                            _cache[prop.Name] = v;
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Pilot.Warn("云端胜率查询失败(已忽略): " + e.Message);
            }
            finally
            {
                foreach (string id in need) _inflight.Remove(id);
            }
        });
    }

    /// <summary>给某张卡的选卡评分加一个修正值。</summary>
    public static double GetBonus(CardModel card)
    {
        try
        {
            if (!PilotConfig.Current.EnableCloudWinrate) return 0;
            if (_cache.Count == 0) return 0;
            string id = card.Id.Entry;
            if (!_cache.TryGetValue(id, out double wr)) return 0;
            // 以 50% 胜率为基准，每高 1% 加 1 分，上限 ±25
            double bonus = (wr - 0.5) * 100;
            return Math.Clamp(bonus, -25, 25);
        }
        catch { return 0; }
    }

    public static string Status()
    {
        var c = PilotConfig.Current;
        return "云端胜率：\n" +
               $"  开关: {(c.EnableCloudWinrate ? "开" : "关")}\n" +
               $"  端点: {(string.IsNullOrWhiteSpace(c.CloudEndpoint) ? "(未配置)" : c.CloudEndpoint)}\n" +
               $"  已缓存卡牌: {_cache.Count} 张\n" +
               (c.EnableCloudWinrate && string.IsNullOrWhiteSpace(c.CloudEndpoint)
                   ? "  ⚠ 已开启但未配置端点。用 ap cloud endpoint <地址> 设置。"
                   : "");
    }
}
