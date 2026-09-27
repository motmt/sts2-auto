using System.Text;
using MegaCrit.Sts2.Core.DevConsole;
using MegaCrit.Sts2.Core.DevConsole.ConsoleCommands;
using MegaCrit.Sts2.Core.Entities.Players;

namespace ConsoleSyncGuard;

/// <summary>mpstatus：查看当前网络会话与控制台指令同步属性。</summary>
public class MpStatusCmd : AbstractConsoleCmd
{
    public override string CmdName => "mpstatus";
    public override string Args => "";
    public override string Description => "ConsoleSyncGuard: show net session info and console command sync flags.";
    public override bool IsNetworked => false;
    public override bool DebugOnly => false;

    public override CmdResult Process(Player issuingPlayer, string[] args)
    {
        var sb = new StringBuilder();
        sb.AppendLine("[ConsoleSyncGuard] ----- 联机控制台状态 -----");
        sb.AppendLine("网络角色: " + Guard.NetTypeName());
        sb.AppendLine("真实多人对局进行中: " + (Guard.IsInRealMultiplayerRun() ? "是" : "否"));
        sb.AppendLine("本机为客机(Client): " + (Guard.IsLocalClient() ? "是" : "否"));
        sb.AppendLine("本机为主机(Host): " + (Guard.IsLocalHost() ? "是" : "否"));
        sb.AppendLine("守卫模式: hostOnly=" + Guard.FormatBool(Guard.HostOnly) + " loud=" + Guard.FormatBool(Guard.Loud));

        DevConsole console = Guard.LatestConsole;
        if (console == null)
        {
            sb.AppendLine("(控制台实例不可用，无法枚举命令表)");
            return new CmdResult(true, sb.ToString());
        }

        var cmds = Guard.EnumerateCommands(console);
        var net = new List<string>();
        var local = new List<string>();
        foreach (var kv in cmds)
        {
            (kv.Value ? net : local).Add(kv.Key);
        }
        sb.AppendLine();
        sb.AppendLine("联网同步指令(可多人使用, " + net.Count + "): " + string.Join(" ", net));
        sb.AppendLine();
        sb.AppendLine("本地/调试指令(仅本机, " + local.Count + "): " + string.Join(" ", local));
        sb.AppendLine("------------------------------------");
        return new CmdResult(true, sb.ToString());
    }
}

/// <summary>mpmode：切换守卫模式（默认主机权威，适合“绝对不出不同步”）。</summary>
public class MpModeCmd : AbstractConsoleCmd
{
    public override string CmdName => "mpmode";
    public override string Args => "[hostonly on|off] [loud on|off]";
    public override string Description => "ConsoleSyncGuard: switch guard mode. hostonly=on blocks state commands on clients (ask the HOST to type them).";
    public override bool IsNetworked => false;
    public override bool DebugOnly => false;

    public override CmdResult Process(Player issuingPlayer, string[] args)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i].ToLowerInvariant();
            if (a == "hostonly" && i + 1 < args.Length)
            {
                Guard.HostOnly = ParseOnOff(args[i + 1]);
                i++;
                sb.AppendLine("hostOnly = " + Guard.FormatBool(Guard.HostOnly));
            }
            else if (a == "loud" && i + 1 < args.Length)
            {
                Guard.Loud = ParseOnOff(args[i + 1]);
                i++;
                sb.AppendLine("loud = " + Guard.FormatBool(Guard.Loud));
            }
            else if (a == "help")
            {
                sb.AppendLine("用法: mpmode hostonly on|off | mpmode loud on|off");
            }
        }
        if (sb.Length == 0)
        {
            sb.AppendLine("hostOnly = " + Guard.FormatBool(Guard.HostOnly) + "  loud = " + Guard.FormatBool(Guard.Loud));
            sb.AppendLine("  hostonly=on : 客机改状态指令被拦截,请房主输入(默认,最稳,不出不同步)");
            sb.AppendLine("  hostonly=off: 客机也可直接输入,按官方同步队列转发(与游戏原版一致,已测大部分指令可同步)");
        }
        return new CmdResult(true, sb.ToString());
    }

    private static bool ParseOnOff(string v) => v.Equals("on", StringComparison.OrdinalIgnoreCase) || v.Equals("1", StringComparison.OrdinalIgnoreCase) || v.Equals("true", StringComparison.OrdinalIgnoreCase);
}

/// <summary>mphelp：快速帮助。</summary>
public class MpHelpCmd : AbstractConsoleCmd
{
    public override string CmdName => "mphelp";
    public override string Args => "";
    public override string Description => "ConsoleSyncGuard: usage help.";
    public override bool IsNetworked => false;
    public override bool DebugOnly => false;

    public override CmdResult Process(Player issuingPlayer, string[] args)
    {
        return new CmdResult(true,
            "[ConsoleSyncGuard] 联机控制台防不同步\n" +
            "  为什么不同步: 客机若绕过同步队列直接改状态(或使用外部修改器/仅本地指令), 与主机的状态校验和(GameSync)不一致, 主机判定 StateDivergence 后断开该玩家。\n" +
            "  本模组默认(hostonly=on): 客机输入会改状态的指令 → 本地拦截, 由房主输入后自动同步给所有人。\n" +
            "  房主使用方法: 照常按~输入 gold/heal/card/relic/power 等即可, 会同步给所有队友。\n" +
            "  常用指令: mpstatus(查看状态) / mpmode hostonly off(允许客机直接输入) / mpmode loud on(详细日志)\n" +
            "  注意: 若你们开过风灵月影等外部修改器, 任何模组都无法阻止其造成的不同步, 请联机时关闭修改器。");
    }
}
