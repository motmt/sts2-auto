using MegaCrit.Sts2.Core.DevConsole;
using MegaCrit.Sts2.Core.DevConsole.ConsoleCommands;
using MegaCrit.Sts2.Core.Entities.Players;

namespace StealthKit;

/// <summary>
/// wishset —— 内部同步命令（不要手动输入）。
/// 联机时由 stealth wishcard 通过官方 ConsoleCmdGameAction 广播到全队，
/// 每台机器各自执行本命令把“该玩家下一层三选一应包含的卡”写入本地表；
/// 之后 CardReward.Populate 的注入补丁会让全队三选一一致地出现这张卡。
/// </summary>
public class WishSetCmd : AbstractConsoleCmd
{
    public override string CmdName => "wishset";
    public override string Args => "<卡ID|off>";
    public override string Description => "(StealthKit internal) set/unset injected wish card for a player, synced across the party.";
    public override bool IsNetworked => false;
    public override bool DebugOnly => false;

    public override CmdResult Process(Player? issuingPlayer, string[] args)
    {
        if (issuingPlayer == null)
        {
            return new CmdResult(false, "wishset 需要在对局中由玩家发起");
        }
        if (args.Length == 0)
        {
            return new CmdResult(false, "用法：wishset <卡ID|off>");
        }
        bool clear = args[0].Equals("off", System.StringComparison.OrdinalIgnoreCase);
        string id = clear ? "" : args[0].ToUpperInvariant();
        if (!clear && Core.FindCard(id) == null)
        {
            return new CmdResult(false, "找不到卡牌 '" + id + "'");
        }
        Core.SetInjectWish(issuingPlayer.NetId, id);
        ModEntry.Logger.Info($"wish for player {issuingPlayer.NetId} -> {(clear ? "off" : id)}");
        return new CmdResult(true, clear ? "愿望已清除" : $"愿望已设置：{id}");
    }
}
