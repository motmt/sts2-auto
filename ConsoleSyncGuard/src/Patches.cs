using System;
using HarmonyLib;
using MegaCrit.Sts2.Core.DevConsole;

namespace ConsoleSyncGuard;

/// <summary>
/// 对本机玩家在控制台回车(DevConsole.ProcessCommand(string))做前置拦截。
/// 拦截后返回 false 表示“已处理,不要再走原版逻辑”。
/// </summary>
[HarmonyPatch(typeof(DevConsole), "ProcessCommand", new Type[] { typeof(string) })]
public static class Patch_DevConsoleProcessCommand
{
    public static bool Prefix(DevConsole __instance, string inputValue, ref CmdResult __result)
    {
        Guard.Decision decision = Guard.OnLocalCommand(__instance, inputValue, out CmdResult handled);
        if (decision == Guard.Decision.Blocked)
        {
            __result = handled;
            return false;
        }
        return true;
    }
}
