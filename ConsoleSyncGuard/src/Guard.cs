using System.Reflection;
using MegaCrit.Sts2.Core.DevConsole;
using MegaCrit.Sts2.Core.DevConsole.ConsoleCommands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Runs;

namespace ConsoleSyncGuard;

/// <summary>
/// 联机控制台守卫核心逻辑。
///
/// 背景(基于 v0.111 反编译结论)：
///  - 杀戮尖塔2 多人是“主机权威 + 确定性动作队列”：任何改状态的控制台指令必须以
///    ConsoleCmdGameAction 经 ActionQueueSynchronizer 由主机排序并广播，全体玩家按相同
///    顺序各执行一次，ChecksumTracker 校验才不会失配。
///  - 官方仅对 IsNetworked==true 的指令做该转发；且官方逻辑对“客机输入”也允许直接请求。
///    为把“数据不同步”概率降到最低，本模组默认采用主机权威模式：
///    客机上凡是会改对局状态的指令一律本地拦截，提示由房主来输入。
///
/// 配置可随时用控制台指令 mpmode 调整（见 Commands.cs）。
/// </summary>
public static class Guard
{
    // ============ 可调配置（默认值） ============
    /// <summary>主机权威模式：true 时客机输入的“改状态指令”被拦截，提示交给房主。</summary>
    public static volatile bool HostOnly = true;

    /// <summary>详细日志：true 时把每次控制台指令在联机中的处理路径打到日志。</summary>
    public static volatile bool Loud = false;

    // ============ 静态缓存 ============
    private static FieldInfo _commandsField;

    /// <summary>最近一次收到本机输入的控制台实例（供 mpstatus 等命令枚举命令表）。</summary>
    public static DevConsole LatestConsole;

    public enum Decision
    {
        RunOriginal, // 交给原版逻辑（联网指令由原版走同步队列；单机/本地指令正常执行）
        Blocked,     // 已本地拦截，__result 需要给出提示
    }

    // ============ 主入口 ============
    /// <summary>在 DevConsole.ProcessCommand（本机玩家在控制台回车）之前调用。</summary>
    public static Decision OnLocalCommand(DevConsole console, string inputValue, out CmdResult result)
    {
        result = default;
        try
        {
            LatestConsole = console;
            if (string.IsNullOrWhiteSpace(inputValue)) return Decision.RunOriginal;
            string[] tokens = inputValue.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            string cmdName = tokens[0].ToLowerInvariant();
            if (cmdName == "clear" || cmdName == "exit") return Decision.RunOriginal;

            // 只有“真实多人对局进行中”才需要管。单机/伪多人/大厅一律放行。
            if (!IsInRealMultiplayerRun()) return Decision.RunOriginal;

            // 找到命令对象，判断是否联网指令
            AbstractConsoleCmd cmd = FindCommand(console, cmdName);
            bool isNetworked = cmd != null && cmd.IsNetworked;

            if (HostOnly && IsLocalClient())
            {
                // —— 主机权威模式 + 本机是客机 ——
                if (isNetworked)
                {
                    // 改状态且官方会联网广播的指令：拦下来，防止“客机直接改自己”导致校验分叉
                    result = new CmdResult(false,
                        "[ConsoleSyncGuard] 联机安全模式(主机权威)：指令 '" + tokens[0] + "' 已被本机拦截，未执行，避免数据不同步。\n" +
                        "  请让【房主(主机)】在自己的控制台输入同一指令；主机执行后会同步给所有人。\n" +
                        "  若坚持让客机直接输入(按官方同步队列，风险自负)，请输入： mpmode hostonly off");
                    Log(console, "BLOCKED on client (hostOnly): " + inputValue);
                    return Decision.Blocked;
                }
                // 本地指令（不改共享状态）→ 放行
                Log(console, "client local-only command allowed: " + inputValue);
                return Decision.RunOriginal;
            }

            // 主机 / 单机 / 宽松模式：交给原版（联网指令原版自动走同步队列）
            if (Loud && isNetworked && IsLocalClient())
            {
                Log(console, "client command enqueued through official sync queue: " + inputValue);
            }
            return Decision.RunOriginal;
        }
        catch (System.Exception e)
        {
            // 任何异常都放行原版，绝不让守卫成为新的崩点
            ModEntry.Logger.Warn("Guard exception (falling through to vanilla): " + e);
            return Decision.RunOriginal;
        }
    }

    // ============ 辅助 ============

    private static void Log(DevConsole console, string msg)
    {
        if (Loud)
        {
            ModEntry.SyncLogger.Info(msg);
        }
    }

    /// <summary>是否处于“真实多人对局中”（Host 或 Client 且对局进行中，非单机/伪多人）。</summary>
    public static bool IsInRealMultiplayerRun()
    {
        RunManager rm = RunManager.Instance;
        if (rm == null) return false;
        if (!rm.IsInProgress) return false;
        if (rm.IsSingleplayerOrFakeMultiplayer) return false;
        return rm.NetService != null && (rm.NetService.Type == NetGameType.Host || rm.NetService.Type == NetGameType.Client);
    }

    /// <summary>本机是否多人客机（有网络会话且为 Client）。无会话/大厅时返回 false。</summary>
    public static bool IsLocalClient()
    {
        RunManager rm = RunManager.Instance;
        if (rm == null || rm.NetService == null) return false;
        return rm.NetService.Type == NetGameType.Client;
    }

    public static bool IsLocalHost()
    {
        RunManager rm = RunManager.Instance;
        if (rm == null || rm.NetService == null) return false;
        return rm.NetService.Type == NetGameType.Host;
    }

    public static string NetTypeName()
    {
        RunManager rm = RunManager.Instance;
        if (rm == null || rm.NetService == null) return "None(未开始对局)";
        return rm.NetService.Type.ToString();
    }

    /// <summary>通过反射读取 DevConsole 私有 _commands 字典（避免依赖 publicizer）。</summary>
    public static AbstractConsoleCmd FindCommand(DevConsole console, string cmdNameLower)
    {
        try
        {
            _commandsField ??= typeof(DevConsole).GetField("_commands", BindingFlags.Instance | BindingFlags.NonPublic);
            if (_commandsField?.GetValue(console) is System.Collections.IDictionary dict && dict.Contains(cmdNameLower))
            {
                return dict[cmdNameLower] as AbstractConsoleCmd;
            }
        }
        catch (System.Exception)
        {
            // ignore
        }
        return null;
    }

    /// <summary>枚举当前控制台已注册命令（含其它 mod 注册的），供 mpstatus 展示。</summary>
    public static List<KeyValuePair<string, bool>> EnumerateCommands(DevConsole console)
    {
        var list = new List<KeyValuePair<string, bool>>();
        try
        {
            _commandsField ??= typeof(DevConsole).GetField("_commands", BindingFlags.Instance | BindingFlags.NonPublic);
            if (_commandsField?.GetValue(console) is System.Collections.IDictionary dict)
            {
                foreach (System.Collections.DictionaryEntry e in dict)
                {
                    bool net = e.Value is AbstractConsoleCmd c && c.IsNetworked;
                    list.Add(new KeyValuePair<string, bool>(e.Key as string ?? "?", net));
                }
            }
        }
        catch (System.Exception)
        {
        }
        list.Sort((a, b) => string.Compare(a.Key, b.Key, StringComparison.Ordinal));
        return list;
    }

    public static string FormatBool(bool v) => v ? "ON" : "OFF";
}
