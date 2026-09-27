using System;
using MegaCrit.Sts2.Core.DevConsole;
using MegaCrit.Sts2.Core.DevConsole.ConsoleCommands;
using MegaCrit.Sts2.Core.Entities.Players;

namespace ClearConscience;

/// <summary>问心无愧控制台命令：cc / cc on / cc off。</summary>
public class CcCmd : AbstractConsoleCmd
{
    public override string CmdName => "cc";
    public override string Args => "[on|off]";
    public override string Description => "问心无愧：战败时保留存档（可回主菜单继续重打）。";
    public override bool IsNetworked => false;
    public override bool DebugOnly => false;

    public override CmdResult Process(Player issuingPlayer, string[] args)
    {
        try
        {
            if (args.Length == 0)
            {
                return new CmdResult(true, Guard.Status());
            }
            string a = args[0].ToLowerInvariant();
            if (a == "on" || a == "开" || a == "开启")
            {
                Guard.Enabled = true;
                Guard.Save();
                return new CmdResult(true, "问心无愧：已开启（战败保留存档）。");
            }
            if (a == "off" || a == "关" || a == "关闭")
            {
                Guard.Enabled = false;
                Guard.Save();
                return new CmdResult(true, "问心无愧：已关闭（战败正常清档）。");
            }
            return new CmdResult(false, "用法：cc | cc on | cc off");
        }
        catch (Exception e)
        {
            ModEntry.Logger.Warn("cc error: " + e);
            return new CmdResult(false, "出错(已记日志): " + e.Message);
        }
    }
}
