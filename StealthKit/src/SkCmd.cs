using System;
using System.Linq;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.DevConsole;
using MegaCrit.Sts2.Core.DevConsole.ConsoleCommands;
using MegaCrit.Sts2.Core.Entities.Players;

namespace StealthKit;

/// <summary>
/// sk —— 唯一入口（收敛版）。日常只记几件事：
///   1 加钱 / 2 每场自动加金 / 3 自然拿卡(等商店或事件) / 4 立刻给(卡/遗物/药水都认) / 5 休息回复
/// 进阶/旧的完整命令仍在 stealth 里，但不用记。
/// </summary>
public class SkCmd : AbstractConsoleCmd
{
    public override string CmdName => "sk";
    public override string Args => "<编号> [参数]";
    public override string Description => "StealthKit 快速菜单（唯一入口）。";
    public override bool IsNetworked => false;
    public override bool DebugOnly => false;

    public override CmdResult Process(Player? issuingPlayer, string[] args)
    {
        if (args.Length == 0)
        {
            return new CmdResult(true, Menu());
        }
        string a0 = args[0].ToLowerInvariant();
        string rest = string.Join(" ", args.Skip(1));
        try
        {
            switch (a0)
            {
                case "1": // 加钱
                    return int.TryParse(rest.Trim(), out int g1) && g1 != 0
                        ? RunGold(g1)
                        : new CmdResult(false, "sk 1 <数额>，例：sk 1 500");

                case "2": // 每场自动加金
                    if (rest.Equals("off", StringComparison.OrdinalIgnoreCase)) return new CmdResult(true, Core.SetLootPerFight(0));
                    return int.TryParse(rest.Trim(), out int g2) && g2 > 0
                        ? new CmdResult(true, Core.SetLootPerFight(g2))
                        : new CmdResult(false, "sk 2 <数额> 或 sk 2 off，例：sk 2 50");

                case "3": // 自然拿卡
                    if (rest.Equals("off", StringComparison.OrdinalIgnoreCase)) return new CmdResult(true, Core.SetNaturalWish(""));
                    return string.IsNullOrWhiteSpace(rest)
                        ? new CmdResult(true, "sk 3 <卡名>，例：sk 3 献祭（自动等商店/事件，记录真实）")
                        : new CmdResult(true, Core.Resolve(Core.ItemKind.Card, rest, "natural"));

                case "4": // 立刻给(卡/遗物/药水)
                    return string.IsNullOrWhiteSpace(rest)
                        ? new CmdResult(true, "sk 4 <名字>，例：sk 4 死亡收割（卡/遗物/药水都认）")
                        : new CmdResult(true, Core.ResolveAll(rest));

                case "5": // 休息回复
                    return RunReported(Core.RestHealAsync(), "休息点回复");

                case "6": // 开局增益
                    return HandleStartBuff(rest);

                case "off": // 一键全关
                    Core.Config.AutoRestHeal = false;
                    Core.SetLootPerFight(0);
                    Core.SetNaturalWish("");
                    Core.ClearStartBuffs();
                    return new CmdResult(true, "已全部关闭。\n" + Core.Status());

                case "0":
                case "status":
                    return new CmdResult(true, Core.Status());

                case "help":
                    return new CmdResult(true, Menu() + "\n高级命令：stealth help");

                default:
                    return new CmdResult(false, "sk 菜单没有 '" + a0 + "'，输入 sk 查看");
            }
        }
        catch (Exception e)
        {
            ModEntry.Logger.Warn("sk error: " + e);
            return new CmdResult(false, "出错(已记日志): " + e.Message);
        }
    }

    private static CmdResult RunGold(int amount)
    {
        Core.RunReported(Core.GiveGoldAsync(amount), "加金");
        return new CmdResult(true, amount > 0 ? $"加 {amount} 金…（结果自动显示在下面）" : $"扣 {-amount} 金…");
    }

    private static CmdResult HandleStartBuff(string rest)
    {
        if (string.IsNullOrWhiteSpace(rest))
        {
            return new CmdResult(true, "当前本关增益：" + Core.StartBuffStatus() + "\n仅本关有效（这场战斗结束即自动清空）。用法：sk 6 力量 4 / sk 6 敏捷 2 / sk 6 <英文ID> <层数>（可叠加）；sk 6 off 清空");
        }
        if (rest.Equals("off", StringComparison.OrdinalIgnoreCase))
        {
            return new CmdResult(true, Core.ClearStartBuffs());
        }
        string[] parts = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !int.TryParse(parts[^1], out int n))
        {
            return new CmdResult(false, "sk 6 <力量/敏捷/增益ID> <层数>，例：sk 6 力量 4（仅本关；可叠加）；sk 6 off 清空；sk 6 查看");
        }
        string alias = string.Join(" ", parts.Take(parts.Length - 1));
        return new CmdResult(true, Core.SetStartBuff(alias, n));
    }

    private static CmdResult RunReported(Task<string> task, string doing)
    {
        Core.RunReported(task, doing);
        return new CmdResult(true, doing + "…");
    }

    public static string Menu()
    {
        return "[StealthKit] sk 快速菜单\n" +
               "  1 <数额>   加/扣金币      例: sk 1 500\n" +
               "  2 <数额>   每场自动加金   例: sk 2 50 (off 关)\n" +
               "  3 <卡名>   想要的卡：自动在下次商店/事件自然到手（推荐）\n" +
               "  4 <名字>   立刻给：卡/遗物/药水都能认（中文搜）\n" +
               "  5          休息点回复一次\n" +
               "  6 <增益> <层数>   仅本关生效(打完即清)  例: sk 6 力量 4 (off 清空)\n" +
               "  0 / off    状态 / 一键全关\n" +
               "搜出多个时用：stealth take <编号>";
    }
}
