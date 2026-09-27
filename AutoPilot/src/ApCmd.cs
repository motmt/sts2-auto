using System;
using System.Linq;
using MegaCrit.Sts2.Core.DevConsole;
using MegaCrit.Sts2.Core.DevConsole.ConsoleCommands;
using MegaCrit.Sts2.Core.Entities.Players;

namespace AutoPilot;

/// <summary>
/// ap —— AutoPilot 唯一控制台入口。
///
/// 设计原则：命令要少、要中文、要当次可见。
///   全流程： ap start → 看 AI 打 → ap pause 插手 → ap resume 交还 → ap stop
///   调教：   ap config / ap set &lt;项&gt; &lt;值&gt;     调 AI 性格
///           ap like &lt;卡名&gt; / ap ban &lt;卡名&gt;   人工选牌建议
///           ap memory                          让 AI 总结本局
///           ap cloud                           云端胜率状态
/// </summary>
public class ApCmd : AbstractConsoleCmd
{
    public override string CmdName => "ap";
    public override string Args => "<子命令> [参数]";
    public override string Description => "AutoPilot：让 AI 自动游玩尖塔（可随时暂停插手）。";
    public override bool IsNetworked => false;
    public override bool DebugOnly => false;

    public override CmdResult Process(Player? issuingPlayer, string[] args)
    {
        if (args == null || args.Length == 0)
        {
            return new CmdResult(true, Menu());
        }

        string a0 = args[0].ToLowerInvariant();
        string rest = args.Length > 1 ? string.Join(" ", args.Skip(1)).Trim() : "";

        try
        {
            switch (a0)
            {
                case "start":
                case "go":
                    return new CmdResult(true, Pilot.Start());

                case "pause":
                case "p":
                    return new CmdResult(true, Pilot.Pause());

                case "resume":
                case "r":
                    return new CmdResult(true, Pilot.Resume("命令"));

                case "stop":
                    return new CmdResult(true, Pilot.Stop());

                case "status":
                case "s":
                    return new CmdResult(true, Pilot.StatusText());

                case "log":
                    int n = 20;
                    if (int.TryParse(rest, out int ln) && ln > 0) n = Math.Min(ln, 100);
                    return new CmdResult(true, $"最近 {n} 条决策日志：\n" + Pilot.RecentLogText(n));

                case "config":
                case "cfg":
                    return new CmdResult(true, PilotConfig.Dump());

                case "set":
                    string[] kv = rest.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                    if (kv.Length < 2) return new CmdResult(false, "用法：ap set <项> <值>，例：ap set delay 300（ap config 查看全部项）");
                    return new CmdResult(true, PilotConfig.SetField(kv[0], kv[1]));

                case "like":
                case "好":
                    return new CmdResult(true, Memory.AddPreference(rest));

                case "unlike":
                    return new CmdResult(true, Memory.RemovePreference(rest));

                case "ban":
                case "禁":
                    return new CmdResult(true, Memory.AddBan(rest));

                case "unban":
                    return new CmdResult(true, Memory.RemoveBan(rest));

                case "rules":
                case "记忆":
                    return new CmdResult(true, Memory.ListRules());

                case "memory":
                case "sum":
                case "总结":
                    return new CmdResult(true, Memory.SummarizeCurrentRun());

                case "cloud":
                case "云":
                    if (!string.IsNullOrWhiteSpace(rest))
                    {
                        return new CmdResult(true, PilotConfig.SetField("endpoint", rest) + "\n" + CloudWinrate.Status());
                    }
                    return new CmdResult(true, CloudWinrate.Status());

                case "llm":
                case "ai":
                case "模型":
                    // ap llm <端点> —— 直接设置大模型端点
                    if (!string.IsNullOrWhiteSpace(rest))
                    {
                        return new CmdResult(true, PilotConfig.SetField("llmendpoint", rest) + "\n" + LlmClient.Status());
                    }
                    return new CmdResult(true, LlmClient.Status());

                case "key":
                case "密钥":
                    if (string.IsNullOrWhiteSpace(rest))
                        return new CmdResult(false, "用法：ap key <你的APIKey>，例：ap key sk-xxxxxxxx\n（DeepSeek Key 在 platform.deepseek.com 获取）");
                    return new CmdResult(true, PilotConfig.SetField("llmkey", rest) + "\n" + LlmClient.Status());

                case "engine":
                case "引擎":
                    if (string.IsNullOrWhiteSpace(rest))
                        return new CmdResult(true, "当前引擎：" + PilotConfig.EngineCn(PilotConfig.Current.EngineMode) +
                                                  "\n用法：ap engine llm（大模型） / ap engine local（纯本地） / ap engine hybrid（大模型优先+回退）");
                    return new CmdResult(true, PilotConfig.SetField("engine", rest) + "\n" + LlmClient.Status());

                case "model":
                case "模型名":
                    if (string.IsNullOrWhiteSpace(rest))
                        return new CmdResult(true, $"当前模型：{PilotConfig.Current.LlmModel}\n用法：ap model <模型名>，例：ap model deepseek-chat");
                    return new CmdResult(true, PilotConfig.SetField("llmmodel", rest));

                case "combatengine":
                case "战斗引擎":
                    if (string.IsNullOrWhiteSpace(rest))
                        return new CmdResult(true, "当前战斗引擎：" + PilotConfig.CombatEngineCn(PilotConfig.Current.CombatEngine) +
                                                  "\n" + SolverBridge.Describe() +
                                                  "\n用法：ap combatengine solver（交给 CombatSolver） / ap combatengine local（AutoPilot 本地出牌）");
                    return new CmdResult(true, PilotConfig.SetField("combatengine", rest) + "\n" + SolverBridge.Describe());

                case "solver":
                case "求解器":
                    return new CmdResult(true, SolverBridge.Describe());

                case "battle":
                case "b":
                case "战况":
                case "战损":
                    if (rest.Equals("reset", StringComparison.OrdinalIgnoreCase) ||
                        rest == "清零")
                        return new CmdResult(true, BattleStats.ResetTotals());
                    return new CmdResult(true, BattleStats.ReportOnMainThread(
                        includeTotals: rest.Equals("total", StringComparison.OrdinalIgnoreCase) || rest == "累计"));

                case "test":
                case "试":
                    return new CmdResult(true, LlmSelfTest.Run());

                case "save":
                    PilotConfig.Save();
                    Pilot.WriteMemory("手动保存记忆");
                    return new CmdResult(true, "配置与记忆已保存：" + PilotConfig.Path);

                case "help":
                case "?":
                    return new CmdResult(true, Menu());

                default:
                    return new CmdResult(false, $"ap 没有子命令 '{a0}'，输入 ap 查看菜单");
            }
        }
        catch (Exception e)
        {
            ModEntry.Logger.Warn("ap error: " + e);
            return new CmdResult(false, "出错(已记日志): " + e.Message);
        }
    }

    public static string Menu()
    {
        return
            "╔══ AutoPilot · AI 自动游玩 ══╗\n" +
            "  控制\n" +
            "    ap start          让 AI 接管当前对局（全自动战斗/选路/选卡/事件）\n" +
            "    ap pause          暂停，玩家手动操作（随时插手）\n" +
            "    ap resume         交还给 AI 继续\n" +
            "    ap stop           停止，控制权完全交回\n" +
            "    ap status         看 AI 在做什么\n" +
            "    ap log [条数]      看决策日志（AI 每一步的理由）\n" +
            "  战斗引擎\n" +
            "    ap battle         看战损与搜索耗时（本场已掉血/预计战损/用时秒数）\n" +
            "    ap battle total   追加看跨场累计（已结算场数/总掉血/总耗时）\n" +
            "    ap battle reset   清零跨场累计\n" +
            "    ap solver         查看 CombatSolver 桥接状态（装没装/在搜没搜）\n" +
            "    ap combatengine <solver|local>  战斗用谁算（默认 solver）\n" +
            "  大模型（LLM）\n" +
            "    ap key <Key>      设置 API Key（默认 DeepSeek，必填）\n" +
            "    ap engine <模式>   引擎: llm(大模型) / local(本地规则) / hybrid(优先大模型+回退)\n" +
            "    ap model <名>      设置模型名（默认 deepseek-chat）\n" +
            "    ap llm [端点]      查看/设置大模型端点\n" +
            "    ap test           测试大模型连接是否可用\n" +
            "  调教 AI\n" +
            "    ap config         看全部可调项\n" +
            "    ap set <项> <值>   例: ap set delay 300 / ap set attack off\n" +
            "    ap like <卡名>     告诉 AI：这张卡优先选\n" +
            "    ap ban <卡名>      告诉 AI：这张卡永不选\n" +
            "    ap rules          看当前人工规则\n" +
            "  记忆与云端\n" +
            "    ap memory         让 AI 总结本局（进展/健康度/决策依据）\n" +
            "    ap cloud [地址]    云端胜率状态 / 设置服务地址\n" +
            "╚═════════════════════════════╝";
    }
}
