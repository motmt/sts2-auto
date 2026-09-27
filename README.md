# 杀戮尖塔 2 模组集 · sts2-auto

《Slay the Spire 2》(v0.111.0) 的四个模组，共用一套构建/部署工具链。

> **单机专用说明**：AutoPilot 为单机设计，联机对局会被校验和踢出。

## 模组一览

| 模组 | 作用 | 入口 |
|---|---|---|
| **AutoPilot** | AI 自动驾驶：全自动战斗出牌 / 地图选路 / 卡牌奖励 / 事件 / 商店 / 篝火，可随时暂停插手。内置 **CombatSolver**（Beam Search 战斗求解器，third_party 子工程）作为战斗大脑 | `ap` 控制台命令 |
| **StealthKit** | 潜行助手（联机向：只对自己生效的增益/信息能力） | `sk` 菜单 |
| **ConsoleSyncGuard** | 联机控制台卫士（房主专用，防止控制台命令不同步导致的掉线） | — |
| **ClearConscience** | 问心无愧：战败时保住存档（不触发清档） | — |

## 目录结构

```
sts2-auto/
├── AutoPilot/                   # 主模组
│   ├── src/                     # 决策层（Pilot/Brain/ApCmd/...）
│   ├── third_party/CombatSolver # 融合进来的战斗求解器（同仓双工程）
│   ├── kb/                      # 双知识库（真实对局统计 + spire-codex 游戏数据）
│   └── AutoPilot.csproj
├── StealthKit/  ConsoleSyncGuard/  ClearConscience/
├── AutoPilot.sln
├── build_all.py                 # 一键构建（autopilot / solver / 全部）
├── deploy_autopilot.py          # 一键部署（停游戏 + Win32 拷贝 + MD5 校验）
└── *.md                         # 各模组上手卡与说明
```

## 构建

需要 **.NET 9 SDK**，并在 `AutoPilot/third_party/CombatSolver/CombatSolver.csproj`
里把 `SteamRoot` / `Sts2Dir` 改成本机路径。

```bash
python build_all.py          # 构建全部
python build_all.py solver   # 只构建 CombatSolver
python build_all.py autopilot# 只构建 AutoPilot
```

## 部署

```bash
python deploy_autopilot.py   # 先停游戏 → Win32 拷贝 → MD5 校验
```

游戏内按 <kbd>`</kbd> 打开控制台，输入 `ap` 查看全部命令。

## AutoPilot 常用命令

```
ap start / pause / resume / stop     控制 AI 接管与交还
ap battle                            看战损与搜索耗时（本场已掉血/预计战损/用时秒数）
ap battle total                      追加跨场累计
ap status                            看 AI 在做什么
ap log [条数]                         看决策日志
ap key <Key>                         设置大模型 API Key（默认 DeepSeek）
ap engine <llm|local|hybrid>         切换决策引擎
ap combatengine <solver|local>       战斗用谁算（默认 solver）
ap like <卡名> / ap ban <卡名>        人工选牌建议（优先级高于大模型）
```

## 文档

- `AutoPilot-一页上手卡.md` — 从零上手
- `AutoPilot-融合CombatSolver说明.md` — 双工程融合、UI 剔除、固定策略档、观测量设计
- `AutoPilot-知识库说明.md` — 双知识库来源与流水线

## 第三方

`AutoPilot/third_party/CombatSolver` 源自 [Torch1230/CombatSolver](https://github.com/Torch1230/CombatSolver)（v0.46.4），
按其许可证使用，详见该目录下 `LICENSE` 与 `THIRD_PARTY_NOTICES.md`。

## 免责

本项目为个人学习与自动化研究用途。请自行持有游戏正版副本。
