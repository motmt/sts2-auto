using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AutoPilot;

/// <summary>
/// 大模型客户端（OpenAI 兼容 Chat Completions 协议）。
///
/// 默认对接 DeepSeek（https://api.deepseek.com/chat/completions，模型 deepseek-chat），
/// 任何实现了 OpenAI 兼容协议的服务都能直接用：OpenAI、Kimi、智谱、通义、本地 Ollama / vLLM 等，
/// 只要在配置里改 <c>llmendpoint</c> / <c>llmmodel</c> / <c>llmkey</c> 即可。
///
/// 设计要点（保证不拖垮游戏）：
///   · 所有请求都在后台线程完成，主线程只 await 结果（带超时）。
///   · 任何失败（网络/超时/Key 无效/返回无法解析）都不抛给游戏，而是返回 null，
///     由调用方回退到本地启发式规则 —— 即“hybrid”模式的核心保障。
///   · 只请求短回复（max_tokens 默认 320），决策延迟可控。
/// </summary>
public static class LlmClient
{
    private static readonly HttpClient _http = new(new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
    })
    {
        // 超时由每次请求的 CancellationToken 控制，这里给一个宽松上限
        Timeout = TimeSpan.FromMilliseconds(120000),
    };

    /// <summary>累计统计，供 ap llm 查看。</summary>
    public static int OkCount;
    public static int FailCount;
    public static string LastError = "";
    public static string LastAnswer = "";
    public static long LastLatencyMs;

    // v2.4.1：token 用量累计（从响应 usage 字段解析），供你在游戏内核对消耗
    public static long PromptTokens;
    public static long CompletionTokens;
    public static long TotalTokens;
    public static long CachedTokens;

    /// <summary>是否有可用的大模型（模式 + Key 双满足）。</summary>
    public static bool Ready
    {
        get
        {
            try
            {
                string mode = PilotConfig.NormalizeEngine(PilotConfig.Current.EngineMode);
                if (mode == "local") return false;
                return PilotConfig.HasLlmKey();
            }
            catch { return false; }
        }
    }

    /// <summary>
    /// 发起一次对话补全。成功返回模型文本（已 trim），失败返回 null（调用方回退本地）。
    /// </summary>
    /// <param name="system">系统提示：定义 AI 的角色与输出格式。</param>
    /// <param name="user">用户提示：当前局面。</param>
    public static async Task<string> AskAsync(string system, string user, CancellationToken ct = default)
    {
        if (!Ready) { LastError = "未配置 API Key 或引擎为本地模式"; return null; }

        PilotConfig c = PilotConfig.Current;
        try
        {
            string body = BuildRequestBody(c, system, user);
            using var req = new HttpRequestMessage(HttpMethod.Post, c.LlmEndpoint);
            req.Content = new StringContent(body, Encoding.UTF8, "application/json");
            if (!string.IsNullOrWhiteSpace(c.LlmApiKey))
                req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + c.LlmApiKey.Trim());
            req.Headers.TryAddWithoutValidation("Accept", "application/json");

            if (c.LlmShowPrompt)
                Pilot.Log($"[LLM问] {Truncate(user, 600)}");

            var sw = System.Diagnostics.Stopwatch.StartNew();

            // 单次请求超时 + 外部取消
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(Math.Max(1000, c.LlmTimeoutMs));

            using HttpResponseMessage resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, timeoutCts.Token)
                .ConfigureAwait(false);
            string text = await resp.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
            sw.Stop();
            LastLatencyMs = sw.ElapsedMilliseconds;

        if (!resp.IsSuccessStatusCode)
        {
            FailCount++;
            LastError = $"HTTP {(int)resp.StatusCode} {resp.StatusCode}: {Truncate(text, 240)}";
            Pilot.Warn("[LLM] " + LastError);
            return null;
        }

        // v2.4.1：记录 token 用量（有 usage 字段就累加；没有也不影响）
        TrackUsage(text);

            string answer = ExtractContent(text);
            if (string.IsNullOrWhiteSpace(answer))
            {
                FailCount++;
                LastError = "返回为空或无法解析: " + Truncate(text, 240);
                Pilot.Warn("[LLM] " + LastError);
                return null;
            }

            OkCount++;
            LastAnswer = answer.Trim();
            LastError = "";
            if (c.LlmShowPrompt)
                Pilot.Log($"[LLM答 {LastLatencyMs}ms] {Truncate(LastAnswer, 300)}");
            return LastAnswer;
        }
        catch (OperationCanceledException)
        {
            FailCount++;
            LastError = ct.IsCancellationRequested ? "已取消" : $"超时(>{PilotConfig.Current.LlmTimeoutMs}ms)";
            if (!ct.IsCancellationRequested) Pilot.Warn("[LLM] " + LastError + "，回退本地规则");
            return null;
        }
        catch (Exception e)
        {
            FailCount++;
            LastError = e.GetType().Name + ": " + e.Message;
            Pilot.Warn("[LLM] 请求失败（回退本地规则）: " + LastError);
            return null;
        }
    }

    /// <summary>从响应正文累加 usage.token（OpenAI 兼容格式）。</summary>
    private static void TrackUsage(string json)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("usage", out JsonElement u)) return;
            if (u.TryGetProperty("prompt_tokens", out var pt) && pt.TryGetInt64(out long p)) PromptTokens += p;
            if (u.TryGetProperty("completion_tokens", out var ct2) && ct2.TryGetInt64(out long c2)) CompletionTokens += c2;
            if (u.TryGetProperty("total_tokens", out var tt) && tt.TryGetInt64(out long t)) TotalTokens += t;
            if (u.TryGetProperty("prompt_cache_hit_tokens", out var ch) && ch.TryGetInt64(out long chv)) CachedTokens += chv;
        }
        catch { }
    }

    private static string BuildRequestBody(PilotConfig c, string system, string user)
    {
        var msgs = new List<object>(2);
        if (!string.IsNullOrWhiteSpace(system))
            msgs.Add(new { role = "system", content = system });
        msgs.Add(new { role = "user", content = user });

        var payload = new Dictionary<string, object>
        {
            ["model"] = string.IsNullOrWhiteSpace(c.LlmModel) ? "deepseek-chat" : c.LlmModel,
            ["messages"] = msgs,
            ["temperature"] = c.LlmTemperature,
            ["max_tokens"] = c.LlmMaxTokens,
            ["stream"] = false,
            // DeepSeek 支持 JSON 输出；开启后模型被约束为合法 JSON，解析更稳
            ["response_format"] = new { type = "json_object" },
        };
        return JsonSerializer.Serialize(payload);
    }

    /// <summary>从 OpenAI 兼容响应里取 choices[0].message.content。</summary>
    private static string ExtractContent(string json)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            if (!root.TryGetProperty("choices", out JsonElement choices) ||
                choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
                return null;
            JsonElement first = choices[0];
            if (!first.TryGetProperty("message", out JsonElement msg)) return null;
            if (msg.TryGetProperty("content", out JsonElement content) && content.ValueKind == JsonValueKind.String)
                return content.GetString();
            if (msg.TryGetProperty("reasoning_content", out JsonElement rc) && rc.ValueKind == JsonValueKind.String)
                return rc.GetString();
            return null;
        }
        catch { return null; }
    }

    /// <summary>
    /// 从模型回复里提取第一个 JSON 对象并反序列化为字典。
    /// 兼容模型偶尔用 ```json 包裹、或前后带解释文字的情况。
    /// </summary>
    public static Dictionary<string, JsonElement> ParseJsonObject(string answer)
    {
        if (string.IsNullOrWhiteSpace(answer)) return null;
        try
        {
            string s = answer.Trim();
            // 去掉 ```json ... ``` 包裹
            int fence = s.IndexOf("```", StringComparison.Ordinal);
            if (fence >= 0)
            {
                int nl = s.IndexOf('\n', fence);
                int end = s.LastIndexOf("```", StringComparison.Ordinal);
                if (nl > 0 && end > nl) s = s.Substring(nl + 1, end - nl - 1).Trim();
            }
            // 截取第一个 { 到最后一个 }
            int l = s.IndexOf('{');
            int r = s.LastIndexOf('}');
            if (l < 0 || r <= l) return null;
            s = s.Substring(l, r - l + 1);

            using JsonDocument doc = JsonDocument.Parse(s);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            var dict = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
            foreach (JsonProperty p in doc.RootElement.EnumerateObject())
                dict[p.Name] = p.Value.Clone();
            return dict;
        }
        catch (Exception e)
        {
            LastError = "JSON 解析失败: " + e.Message;
            return null;
        }
    }

    /// <summary>取字典里的字符串字段（容错大小写）。</summary>
    public static string GetStr(Dictionary<string, JsonElement> d, string key)
    {
        if (d == null || !d.TryGetValue(key, out JsonElement v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.ToString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => v.ToString(),
        };
    }

    /// <summary>取字典里的整数字段。</summary>
    public static int? GetInt(Dictionary<string, JsonElement> d, string key)
    {
        if (d == null || !d.TryGetValue(key, out JsonElement v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n)) return n;
        if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out int m)) return m;
        return null;
    }

    private static string Truncate(string s, int n)
        => string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s : s[..n] + "…");

    public static string Status()
    {
        var c = PilotConfig.Current;
        // v2.4.1：按当前价估算花费（DeepSeek Flash：输入 miss $0.15 / 命中 $0.003 / 输出 $0.6 每 1M tok，取非高峰价）
        double estCost = (PromptTokens - CachedTokens) * 0.15 / 1e6
                       + CachedTokens * 0.003 / 1e6
                       + CompletionTokens * 0.6 / 1e6;
        return "大模型（LLM）状态\n" +
               $"  引擎模式: {c.EngineMode}（{PilotConfig.EngineCn(c.EngineMode)}）\n" +
               $"  就绪: {(Ready ? "✅ 可以调用" : "❌ 不可用")}\n" +
               $"  端点: {c.LlmEndpoint}\n" +
               $"  模型: {c.LlmModel}\n" +
               $"  密钥: {PilotConfig.MaskKey(c.LlmApiKey)}\n" +
               $"  超时: {c.LlmTimeoutMs}ms   max_tokens: {c.LlmMaxTokens}   temp: {c.LlmTemperature}\n" +
               $"  调用统计: 成功 {OkCount} / 失败 {FailCount}   最近耗时 {LastLatencyMs}ms\n" +
               $"  本次运行 token: 输入 {PromptTokens:#,0}（缓存命中 {CachedTokens:#,0}） 输出 {CompletionTokens:#,0} 合计 {TotalTokens:#,0}\n" +
               $"  估算花费: ≈ ${estCost:F4}（≈¥{estCost * 7.2:F3}）\n" +
               (string.IsNullOrEmpty(LastError) ? "" : $"  最近错误: {LastError}\n") +
               (Ready ? "" : "  ⚠ 用 ap key <你的Key> 设置密钥；或 ap engine local 切回本地规则。\n");
    }
}

/// <summary>大模型自检：发一条最小请求，验证 Key / 端点 / 网络是否可用。</summary>
public static class LlmSelfTest
{
    public static string Run()
    {
        try
        {
            var c = PilotConfig.Current;
            if (!LlmClient.Ready)
                return "❌ 大模型不可用：未配置 API Key 或引擎为 local。\n" +
                       "   · 设置密钥：ap key <你的Key>\n" +
                       "   · 切换引擎：ap engine llm";

            string system = "你是一个测试助手。只输出 JSON。";
            string user = "请回复：{\"ok\":true,\"msg\":\"连通正常\"}";

            // 自检给足时间，用较长超时
            var prev = c.LlmTimeoutMs;
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                string answer = LlmClient.AskAsync(system, user).GetAwaiter().GetResult();
                sw.Stop();
                if (string.IsNullOrEmpty(answer))
                    return $"❌ 大模型调用失败（{sw.ElapsedMilliseconds}ms）：{LlmClient.LastError}\n" +
                           "   请检查：① API Key 是否正确 ② 网络能否访问端点 ③ 账户是否有余额。";
                return $"✅ 大模型连通正常（{sw.ElapsedMilliseconds}ms）\n" +
                       $"   端点: {c.LlmEndpoint}\n" +
                       $"   模型: {c.LlmModel}\n" +
                       $"   返回: {Truncate(answer, 300)}";
            }
            finally { c.LlmTimeoutMs = prev; }
        }
        catch (Exception e)
        {
            return "❌ 自检异常: " + e.Message;
        }
    }

    private static string Truncate(string s, int n)
        => string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s : s[..n] + "…");
}
