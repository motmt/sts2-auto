using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;

namespace AutoPilot;

/// <summary>
/// AutoPilot 配置。全部为可调策略参数，写在 mod 目录下的 AutoPilot_config.json。
/// 目标：让不懂代码的用户也能通过控制台命令 <c>ap set &lt;键&gt; &lt;值&gt;</c> 调整 AI 性格。
///
/// 用法：读 <c>PilotConfig.Current.XXX</c>；改 <c>PilotConfig.SetField(...)</c> 或直接赋值后 <c>Save()</c>。
/// </summary>
public class PilotConfig
{
    public static readonly string Path = System.IO.Path.Combine(
        System.IO.Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? ".",
        "AutoPilot_config.json");

    private static PilotConfig _instance;

    /// <summary>当前生效的配置（首次访问时自动从磁盘载入）。</summary>
    public static PilotConfig Current
    {
        get
        {
            if (_instance == null)
            {
                _instance = new PilotConfig();
                LoadInto(_instance);
            }
            return _instance;
        }
    }

    // ---------- 决策引擎 ----------
    /// <summary>
    /// 决策引擎模式：
    ///   "llm"   = 调用大模型（默认 DeepSeek）做决策，质量最高，需要 API Key 与联网；
    ///   "local" = 纯本地启发式规则，零依赖，但决策较粗糙；
    ///   "hybrid"= 优先问大模型，失败(超时/无 Key/报错)时自动回退本地规则。
    /// </summary>
    public string EngineMode { get; set; } = "llm";

    /// <summary>
    /// v2.8.0 战斗引擎：
    ///   "solver" = 战斗整体托管给 CombatSolver（原作者 Torch1230 的工业级求解器）。
    ///              若本机未安装 CombatSolver，自动降级为本地出牌。
    ///              这是**默认值**——本地前瞻是玩具，solver 是量级级的强。
    ///   "local"  = 用 AutoPilot 自己的出牌逻辑（LLM 计划 + 本地前瞻 + 评分）。
    /// </summary>
    public string CombatEngine { get; set; } = "solver";

    // ---------- 大模型（OpenAI 兼容协议） ----------
    /// <summary>对话补全端点（OpenAI 兼容）。默认 DeepSeek。</summary>
    public string LlmEndpoint { get; set; } = "https://api.deepseek.com/chat/completions";
    /// <summary>API Key。DeepSeek 必填；本地 Ollama 可留空。</summary>
    public string LlmApiKey { get; set; } = "";
    /// <summary>模型名。DeepSeek 对话模型为 deepseek-chat。</summary>
    public string LlmModel { get; set; } = "deepseek-chat";
    /// <summary>单次请求超时（毫秒）。超时即回退本地规则，避免卡住游戏。</summary>
    public int LlmTimeoutMs { get; set; } = 12000;
    /// <summary>回复最大 token 数（决策只需很短，够用即可，省时省钱）。</summary>
    public int LlmMaxTokens { get; set; } = 320;
    /// <summary>采样温度。决策类任务建议偏低，保证稳定。</summary>
    public double LlmTemperature { get; set; } = 0.2;
    /// <summary>是否在控制台打印每次问大模型的完整问答（排错用，默认关）。</summary>
    public bool LlmShowPrompt { get; set; } = false;

    // ---------- 战斗 ----------
    /// <summary>出牌前等待的“思考”毫秒数，让玩家能看清 AI 在做什么。</summary>
    public int ActionDelayMs { get; set; } = 550;
    /// <summary>回合开始前的额外停顿。</summary>
    public int TurnDelayMs { get; set; } = 350;
    /// <summary>是否自动使用药水。</summary>
    public bool AutoUsePotions { get; set; } = true;
    /// <summary>残血阈值(%)：血量低于此值时优先使用防御/治疗资源。</summary>
    public int DefensiveHpPercent { get; set; } = 45;
    /// <summary>是否允许 AI 主动攻击（关闭则只能防御，用于观战模式）。</summary>
    public bool AllowAttack { get; set; } = true;

    // ---------- 路线 ----------
    /// <summary>残血阈值(%)：低于此值优先走休息点。</summary>
    public int LowHpRestPercent { get; set; } = 55;
    /// <summary>金币低于此值时商店优先级下降。</summary>
    public int ShopMinGold { get; set; } = 120;
    /// <summary>是否在血量健康时主动挑战精英。</summary>
    public bool SeekEliteWhenHealthy { get; set; } = true;
    /// <summary>挑战精英所需的最低血量百分比。</summary>
    public int EliteHpPercent { get; set; } = 70;

    // ---------- 选卡 ----------
    /// <summary>是否自动跳过明显无用的卡牌奖励。</summary>
    public bool AllowCardSkip { get; set; } = true;
    /// <summary>AI 记忆：人工给出的选卡偏好（卡 ID 或中文名，优先选）。</summary>
    public List<string> PreferredCards { get; set; } = new();
    /// <summary>AI 记忆：人工标注的禁选卡（绝不选）。</summary>
    public List<string> BannedCards { get; set; } = new();

    // ---------- 记忆 ----------
    /// <summary>是否把每局的表现写入 memory 文件供 AI 复盘。</summary>
    public bool EnableMemory { get; set; } = true;
    /// <summary>记忆文件目录（相对 mod 目录）。</summary>
    public string MemoryDir { get; set; } = "memory";

    // ---------- 云端 ----------
    /// <summary>是否查询云端胜率（需配置 CloudEndpoint，未配置时自动跳过）。</summary>
    public bool EnableCloudWinrate { get; set; } = false;
    /// <summary>云端服务基址。</summary>
    public string CloudEndpoint { get; set; } = "";
    /// <summary>云端请求超时（毫秒）。</summary>
    public int CloudTimeoutMs { get; set; } = 2500;

    // ---------- 通用 ----------
    /// <summary>是否在控制台输出详细决策日志。</summary>
    public bool Verbose { get; set; } = true;

    /// <summary>从磁盘载入并覆盖当前单例的字段（不替换实例，避免引用失效）。</summary>
    public static void Load()
    {
        if (_instance == null) { _ = Current; return; }
        LoadInto(_instance);
    }

    private static void LoadInto(PilotConfig t)
    {
        try
        {
            if (!File.Exists(Path)) return;
            var opt = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            PilotConfig src = JsonSerializer.Deserialize<PilotConfig>(File.ReadAllText(Path), opt);
            if (src == null) return;
            t.EngineMode = NormalizeEngine(src.EngineMode);
            t.CombatEngine = NormalizeCombatEngine(src.CombatEngine);
            t.LlmEndpoint = string.IsNullOrWhiteSpace(src.LlmEndpoint) ? "https://api.deepseek.com/chat/completions" : src.LlmEndpoint;
            t.LlmApiKey = src.LlmApiKey ?? "";
            t.LlmModel = string.IsNullOrWhiteSpace(src.LlmModel) ? "deepseek-chat" : src.LlmModel;
            t.LlmTimeoutMs = src.LlmTimeoutMs <= 0 ? 12000 : src.LlmTimeoutMs;
            t.LlmMaxTokens = src.LlmMaxTokens <= 0 ? 320 : src.LlmMaxTokens;
            t.LlmTemperature = src.LlmTemperature < 0 ? 0.2 : src.LlmTemperature;
            t.LlmShowPrompt = src.LlmShowPrompt;
            t.ActionDelayMs = src.ActionDelayMs;
            t.TurnDelayMs = src.TurnDelayMs;
            t.AutoUsePotions = src.AutoUsePotions;
            t.DefensiveHpPercent = src.DefensiveHpPercent;
            t.AllowAttack = src.AllowAttack;
            t.LowHpRestPercent = src.LowHpRestPercent;
            t.ShopMinGold = src.ShopMinGold;
            t.SeekEliteWhenHealthy = src.SeekEliteWhenHealthy;
            t.EliteHpPercent = src.EliteHpPercent;
            t.AllowCardSkip = src.AllowCardSkip;
            t.PreferredCards = src.PreferredCards ?? new List<string>();
            t.BannedCards = src.BannedCards ?? new List<string>();
            t.EnableMemory = src.EnableMemory;
            t.MemoryDir = string.IsNullOrWhiteSpace(src.MemoryDir) ? "memory" : src.MemoryDir;
            t.EnableCloudWinrate = src.EnableCloudWinrate;
            t.CloudEndpoint = src.CloudEndpoint ?? "";
            t.CloudTimeoutMs = src.CloudTimeoutMs <= 0 ? 2500 : src.CloudTimeoutMs;
            t.Verbose = src.Verbose;
        }
        catch (Exception e)
        {
            ModEntry.Logger.Warn("AutoPilot config load failed: " + e);
        }
    }

    public static void Save()
    {
        try
        {
            var opt = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(Path, JsonSerializer.Serialize(Current, opt));
        }
        catch (Exception e)
        {
            ModEntry.Logger.Warn("AutoPilot config save failed: " + e);
        }
    }

    /// <summary>通过控制台设置某个字段（ap set 命令用）。返回人类可读结果。</summary>
    public static string SetField(string key, string value)
    {
        PilotConfig c = Current;
        string k = key.Trim().ToLowerInvariant();
        try
        {
            switch (k)
            {
                case "engine":
                case "模式":
                    c.EngineMode = NormalizeEngine(value);
                    break;
                case "combatengine":
                case "combat":
                case "战斗引擎":
                    c.CombatEngine = NormalizeCombatEngine(value);
                    break;
                case "llmkey":
                case "key":
                case "密钥":
                    c.LlmApiKey = (value ?? "").Trim();
                    break;
                case "llmendpoint":
                case "llmurl":
                    c.LlmEndpoint = string.IsNullOrWhiteSpace(value) ? "https://api.deepseek.com/chat/completions" : value.Trim();
                    break;
                case "llmmodel":
                case "model":
                case "模型":
                    c.LlmModel = string.IsNullOrWhiteSpace(value) ? "deepseek-chat" : value.Trim();
                    break;
                case "llmtimeout":
                    c.LlmTimeoutMs = Clamp(int.Parse(value), 1000, 120000);
                    break;
                case "llmmax":
                case "llmtokens":
                    c.LlmMaxTokens = Clamp(int.Parse(value), 32, 4096);
                    break;
                case "llmtemp":
                    c.LlmTemperature = Math.Clamp(double.Parse(value), 0, 2);
                    break;
                case "llmshow":
                    c.LlmShowPrompt = ParseBool(value);
                    break;
                case "delay": c.ActionDelayMs = Clamp(int.Parse(value), 0, 5000); break;
                case "turndelay": c.TurnDelayMs = Clamp(int.Parse(value), 0, 5000); break;
                case "potions": c.AutoUsePotions = ParseBool(value); break;
                case "defhp": c.DefensiveHpPercent = Clamp(int.Parse(value), 0, 100); break;
                case "attack": c.AllowAttack = ParseBool(value); break;
                case "resthp": c.LowHpRestPercent = Clamp(int.Parse(value), 0, 100); break;
                case "shopgold": c.ShopMinGold = Math.Max(0, int.Parse(value)); break;
                case "seekelite": c.SeekEliteWhenHealthy = ParseBool(value); break;
                case "elitehp": c.EliteHpPercent = Clamp(int.Parse(value), 0, 100); break;
                case "cardskip": c.AllowCardSkip = ParseBool(value); break;
                case "memory": c.EnableMemory = ParseBool(value); break;
                case "verbose": c.Verbose = ParseBool(value); break;
                case "cloud": c.EnableCloudWinrate = ParseBool(value); break;
                case "endpoint": c.CloudEndpoint = value.Trim(); break;
                default:
                    return $"未知配置项 '{key}'。可用: engine combatengine llmkey llmendpoint llmmodel llmtimeout llmmax llmtemp llmshow delay turndelay potions defhp attack resthp shopgold seekelite elitehp cardskip memory verbose cloud endpoint";
            }
            Save();
            return $"已设置 {k} = {value}";
        }
        catch (Exception e)
        {
            return $"设置失败: {e.Message}";
        }
    }

    /// <summary>把字符串规整为合法的引擎模式。</summary>
    public static string NormalizeEngine(string v)
    {
        string s = (v ?? "").Trim().ToLowerInvariant();
        return s switch
        {
            "llm" or "大模型" or "ai" or "api" or "apikey" or "deepseek" => "llm",
            "local" or "本地" or "规则" or "off" or "none" => "local",
            "hybrid" or "混合" or "auto" or "双" => "hybrid",
            _ => "llm",
        };
    }

    /// <summary>当前模式的中文描述。</summary>
    public static string EngineCn(string mode) => NormalizeEngine(mode) switch
    {
        "local" => "纯本地规则",
        "hybrid" => "大模型优先（失败回退本地）",
        _ => "大模型决策",
    };

    /// <summary>把字符串规整为合法的战斗引擎。</summary>
    public static string NormalizeCombatEngine(string v)
    {
        string s = (v ?? "").Trim().ToLowerInvariant();
        return s switch
        {
            "solver" or "combatsolver" or "求解器" or "托管" or "external" or "ext" => "solver",
            "local" or "own" or "自研" or "本地" or "内置" => "local",
            _ => "solver",
        };
    }

    /// <summary>战斗引擎的中文描述。</summary>
    public static string CombatEngineCn(string v) => NormalizeCombatEngine(v) switch
    {
        "local" => "AutoPilot 本地出牌",
        _ => "CombatSolver 引擎（未装则自动降级本地）",
    };

    /// <summary>是否已配置可用的 API Key（本地 endpoint 允许留空）。</summary>
    public static bool HasLlmKey()
    {
        try
        {
            var c = Current;
            if (!string.IsNullOrWhiteSpace(c.LlmApiKey)) return true;
            // 本地/自建端点（Ollama 等）允许空 Key
            string ep = c.LlmEndpoint ?? "";
            return ep.Contains("localhost") || ep.Contains("127.0.0.1") || ep.Contains("0.0.0.0");
        }
        catch { return false; }
    }

    private static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);

    private static bool ParseBool(string s)
    {
        s = (s ?? "").Trim().ToLowerInvariant();
        return s is "1" or "on" or "true" or "yes" or "开" or "是";
    }

    public static string Dump()
    {
        PilotConfig c = Current;
        return "AutoPilot 当前配置：\n" +
               $"  ⚙ 决策引擎 engine={c.EngineMode}（{EngineCn(c.EngineMode)}）\n" +
               $"  ⚔ 战斗引擎 combatengine={c.CombatEngine}（{CombatEngineCn(c.CombatEngine)}）\n" +
               $"  大模型 model={c.LlmModel}  endpoint={c.LlmEndpoint}\n" +
               $"  密钥 key={MaskKey(c.LlmApiKey)}  超时={c.LlmTimeoutMs}ms  max_tokens={c.LlmMaxTokens}  temp={c.LlmTemperature}\n" +
               $"  出牌间隔 delay={c.ActionDelayMs}ms  回合停顿 turndelay={c.TurnDelayMs}ms\n" +
               $"  自动药水 potions={OnOff(c.AutoUsePotions)}  残血线 defhp={c.DefensiveHpPercent}%  允许攻击 attack={OnOff(c.AllowAttack)}\n" +
               $"  休息线 resthp={c.LowHpRestPercent}%  商店最少金 shopgold={c.ShopMinGold}  主动精英 seekelite={OnOff(c.SeekEliteWhenHealthy)}({c.EliteHpPercent}%)\n" +
               $"  允许跳过卡牌 cardskip={OnOff(c.AllowCardSkip)}  记忆 memory={OnOff(c.EnableMemory)}\n" +
               $"  云端胜率 cloud={OnOff(c.EnableCloudWinrate)} endpoint={(string.IsNullOrEmpty(c.CloudEndpoint) ? "(未配置)" : c.CloudEndpoint)}\n" +
               $"  选卡偏好: {(c.PreferredCards.Count == 0 ? "(无)" : string.Join("、", c.PreferredCards))}\n" +
               $"  禁选卡牌: {(c.BannedCards.Count == 0 ? "(无)" : string.Join("、", c.BannedCards))}";
    }

    /// <summary>API Key 打码显示，避免日志泄露。</summary>
    public static string MaskKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return "(未设置)";
        string k = key.Trim();
        if (k.Length <= 8) return k[..1] + "***";
        return k[..4] + "***" + k[^4..];
    }

    private static string OnOff(bool b) => b ? "开" : "关";
}
