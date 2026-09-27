# -*- coding: utf-8 -*-
"""给 SolverOverlay 的所有 public 入口方法插入 UiSuppressed 短路。
做法：定位每个目标 public static 方法的签名起始行，向下扫描到方法体左花括号 '{'
（跳过参数续行），紧接着插入短路块。
"""
import io, re

path = r'D:\尖塔模组\AutoPilot\third_party\CombatSolver\src\UI\SolverOverlay.cs'
src = io.open(path, encoding='utf-8-sig').read()
nl = '\r\n' if '\r\n' in src else '\n'
lines = src.split(nl)

TARGET = {
    'Show', 'ShowDisabled', 'ShowSearchStopped', 'ShowManualCalculationReady',
    'ShowRetainedRoute', 'ShowPotionRewardSettingChanged', 'ShowProgress',
    'ShowSearching', 'ShowResult', 'ShowDeploying', 'ShowDeploymentStep',
    'ShowEndTurnDeploymentStep', 'ShowDeploymentComplete',
    'ShowWaitingForNextTurnPlan', 'ShowFullAutoStoppedAtCombatEnd',
    'ShowFullAutoStoppedAtDeathTurn', 'ShowFullAutoStoppedAfterWorseRecalculation',
    'ShowFullAutoStoppedAtLiveRisk', 'RefreshControls', 'RefreshBossHpStrategyHint',
    'RefreshGuidanceHints', 'ApplyOverlayOpacity', 'ApplyConfiguredTheme',
    'Hide', 'ToggleVisibilityFromShortcut',
}

MARK = '// 【AutoPilot 融合改造】UI 已剔除：入口短路，仅保留引擎调用语义。'
sig_re = re.compile(r'^    public static\s+([\w\.<>\[\],\?]+)\s+([A-Za-z_]\w*)\s*\(')

out = []
i = 0
count = 0
inserted = []
while i < len(lines):
    ln = lines[i]
    out.append(ln)
    m = sig_re.match(ln)
    if m:
        ret, name = m.group(1), m.group(2)
        if name in TARGET:
            # 定位方法体 '{'：从签名行开始，找到第一个 strip()=='{' 的行
            j = i
            brace = -1
            while j < len(lines) and j < i + 40:
                if lines[j].strip() == '{':
                    brace = j
                    break
                j += 1
            if brace > i:
                # brace 行已由后续循环输出；这里先把 i..brace-1 补上
                for k in range(i + 1, brace + 1):
                    out.append(lines[k])
                if ret == 'bool':
                    rstmt = '    return false;'
                else:
                    rstmt = '    return;'
                out.append('        ' + MARK)
                out.append('        if (UiSuppressed)')
                out.append('        {')
                out.append('        ' + rstmt)
                out.append('        }')
                count += 1
                inserted.append(name)
                i = brace + 1
                continue
    i += 1

new = nl.join(out)
io.open(path, 'w', encoding='utf-8-sig', newline='').write(new)
print('inserted:', count)
print('methods :', ', '.join(inserted))
