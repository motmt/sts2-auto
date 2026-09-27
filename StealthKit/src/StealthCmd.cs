using System;
using System.Linq;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.DevConsole;
using MegaCrit.Sts2.Core.DevConsole.ConsoleCommands;
using MegaCrit.Sts2.Core.Entities.Players;

namespace StealthKit;

/// <summary>
/// stealth —— 潜行助手主命令（免记 ID 版）。
/// 支持用中文/英文关键字搜索：stealth find/give/wishcard，命中多条时用 stealth take &lt;编号&gt;。
/// 本命令为“本地指令”(IsNetworked=false)，只在本机触发官方同步通道。
/// </summary>
public class StealthCmd : AbstractConsoleCmd
{
    public override string CmdName => "stealth";
    public override string Args => "<give|find|take|wishcard|rest|autoheal|status|help> ...";
    public override string Description => "StealthKit: covert single-user cheats with Chinese-name search (cards/relics/potions/gold/rest-heal).";
    public override bool IsNetworked => false;
    public override bool DebugOnly => false;

    public override CmdResult Process(Player? issuingPlayer, string[] args)
    {
        if (args.Length == 0 || args[0] == "help")
        {
            return new CmdResult(true, Core.Help());
        }
        string sub = args[0].ToLowerInvariant();
        try
        {
            switch (sub)
            {
                case "status":
                    return new CmdResult(true, Core.Status());

                case "give":
                    return HandleGive(args);

                case "find":
                    return HandleFind(args);

                case "take":
                    return HandleTake(args);

                case "buy":
                    return HandleBuy(args);

                case "loot":
                    if (args.Length < 2)
                    {
                        return new CmdResult(false, "用法：stealth loot <每场金币数|off>（每场战斗自动同步加金；建议贴近正常掉落范围）");
                    }
                    if (args[1].Equals("off", StringComparison.OrdinalIgnoreCase))
                    {
                        return new CmdResult(true, Core.SetLootPerFight(0));
                    }
                    if (!int.TryParse(args[1], out int amt) || amt <= 0)
                    {
                        return new CmdResult(false, "请输入正整数（如 stealth loot 60），或 loot off 关闭");
                    }
                    return new CmdResult(true, Core.SetLootPerFight(amt));

                case "wishcard":
                    return HandleWish(args);

                case "rest":
                    return RunReported(Core.RestHealAsync(), "休息点回复");

                case "autoheal":
                    if (args.Length >= 2 && args[1].Equals("on", StringComparison.OrdinalIgnoreCase))
                    {
                        Core.Config.AutoRestHeal = true;
                        Core.Config.Save();
                        return new CmdResult(true, "火堆自动回复：开（下次进休息点自动休息一次）");
                    }
                    if (args.Length >= 2 && args[1].Equals("off", StringComparison.OrdinalIgnoreCase))
                    {
                        Core.Config.AutoRestHeal = false;
                        Core.Config.Save();
                        return new CmdResult(true, "火堆自动回复：关");
                    }
                    return new CmdResult(true, "火堆自动回复：" + (Core.Config.AutoRestHeal ? "开" : "关") + "（stealth autoheal on/off 切换）");

                default:
                    return new CmdResult(false, "未知子命令 '" + sub + "'，输入 stealth help 查看用法");
            }
        }
        catch (Exception e)
        {
            ModEntry.Logger.Warn("stealth command error: " + e);
            return new CmdResult(false, "执行出错(已记日志): " + e.Message);
        }
    }

    // stealth give <card|relic|potion|gold> <关键字|数量>
    private CmdResult HandleGive(string[] args)
    {
        if (args.Length < 3) return new CmdResult(false, "用法：stealth give <card|relic|potion|gold> <名称/关键字/数量>\n  例：stealth give card 献祭   或   stealth give gold 999");
        string kind = args[1].ToLowerInvariant();
        string query = string.Join(" ", args.Skip(2));
        switch (kind)
        {
            case "card": return new CmdResult(true, Core.Resolve(Core.ItemKind.Card, query, "give"));
            case "relic": return new CmdResult(true, Core.Resolve(Core.ItemKind.Relic, query, "give"));
            case "potion": return new CmdResult(true, Core.Resolve(Core.ItemKind.Potion, query, "give"));
            case "gold":
                if (!int.TryParse(query.Trim(), out int g)) return new CmdResult(false, "gold 需要整数数额，如 stealth give gold 500");
                if (g == 0) return new CmdResult(false, "数额不能为 0");
                // 金币很轻量，任务完成(通常瞬间)后会把真实结果直接打进控制台
                Core.RunReported(Core.GiveGoldAsync(g), "调整金币");
                return new CmdResult(true, "正在" + (g > 0 ? "增加" : "扣除") + "金币 " + Math.Abs(g) + "…（结果会自动显示在下方）");
            default: return new CmdResult(false, "未知类型 '" + kind + "'（可选 card/relic/potion/gold）");
        }
    }

    // stealth buy <卡名> [价格] —— 付费购买伪装（默认按稀有度定价；建议在商店层用）
    private CmdResult HandleBuy(string[] args)
    {
        if (args.Length < 2) return new CmdResult(false, "用法：stealth buy <卡名> [价格]（照商店流程扣钱+记录花销，队友端记录与真实购买一致）");
        string query = string.Join(" ", args.Skip(1));
        return new CmdResult(true, Core.Resolve(Core.ItemKind.Card, query, "buy"));
    }

    // stealth find <card|relic|potion> <关键字>
    private CmdResult HandleFind(string[] args)
    {
        if (args.Length < 3) return new CmdResult(false, "用法：stealth find <card|relic|potion> <关键字>（中文/英文都行）");
        string kind = args[1].ToLowerInvariant();
        string query = string.Join(" ", args.Skip(2));
        Core.ItemKind k = kind switch
        {
            "card" => Core.ItemKind.Card,
            "relic" => Core.ItemKind.Relic,
            "potion" => Core.ItemKind.Potion,
            _ => throw new ArgumentException("未知类型 '" + kind + "'"),
        };
        var hits = Core.Search(k, query);
        if (hits.Count == 0) return new CmdResult(true, $"没有找到匹配的{Core.KindLabel(k)}：{query}");
        Core.Pending = hits;
        Core.PendingMode = "give";
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"找到 {hits.Count} 个{Core.KindLabel(k)}，输入编号直接获得：");
        int i = 1;
        foreach (var c in hits)
        {
            sb.AppendLine($"  {i}. {c.Title}  [{c.Id}]");
            i++;
        }
        sb.Append("stealth take <编号>");
        return new CmdResult(true, sb.ToString());
    }

    // stealth take <编号>
    private CmdResult HandleTake(string[] args)
    {
        if (Core.Pending.Count == 0) return new CmdResult(false, "当前没有候选列表（先 stealth find / give 搜索）");
        if (args.Length < 2 || !int.TryParse(args[1], out int n) || n < 1 || n > Core.Pending.Count)
        {
            return new CmdResult(false, $"请输入 1-{Core.Pending.Count} 的编号");
        }
        var c = Core.Pending[n - 1];
        string msg = Core.ExecuteOne(c.Kind, c.Id, Core.PendingMode);
        Core.Pending.Clear();
        Core.PendingMode = "";
        return new CmdResult(true, msg);
    }

    // stealth wishcard [card] <名称/关键字>
    private CmdResult HandleWish(string[] args)
    {
        if (args.Length < 2) return new CmdResult(false, "用法：stealth wishcard <卡牌名>（把下一层卡牌奖励变成必然包含它）；stealth wishcard off 取消");
        string off = args[1].ToLowerInvariant();
        if (off == "off")
        {
            return new CmdResult(true, Core.RequestWish("", isClear: true));
        }
        Core.ItemKind kind = Core.ItemKind.Card;
        int textStart = 1;
        string k0 = args[1].ToLowerInvariant();
        if (k0 is "card" or "relic" or "potion")
        {
            kind = k0 switch { "card" => Core.ItemKind.Card, "relic" => Core.ItemKind.Relic, _ => Core.ItemKind.Potion };
            textStart = 2;
        }
        if (kind != Core.ItemKind.Card)
        {
            return new CmdResult(false, "愿望注入只支持卡牌；遗物/药水请用 stealth give 直接送");
        }
        if (args.Length <= textStart) return new CmdResult(false, "请输入想设为愿望的卡牌名称关键字（如：献祭）");
        string query = string.Join(" ", args.Skip(textStart));
        return new CmdResult(true, Core.Resolve(Core.ItemKind.Card, query, "wish"));
    }

    /// <summary>启动异步操作并把真实结果打到控制台；本行先给个简短占位反馈。</summary>
    private static CmdResult RunReported(Task<string> task, string doing)
    {
        Core.RunReported(task, doing);
        return new CmdResult(true, doing + "已开始…（真实结果会自动显示在下方）");
    }
}
