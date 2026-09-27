# CombatSolver 融合说明（v2.9.0）

AutoPilot 仓库现已**内置**战斗路线求解器（CombatSolver v0.46.4，作者 Torch），
作为 `third_party/` 子工程，与 AutoPilot 一起构建、一起部署。

## 为什么是"双工程"而不是"单 dll"

CombatSolver 的 709 个源文件依赖**编译期**特性，无法并入 `AutoPilot.csproj`：

| 依赖 | 作用 | 冲突点 |
|---|---|---|
| `Krafs.Publicizer` + `<Publicize Include="sts2">` | 公开化 `sts2.dll` 的内部成员 | 若合并，AutoPilot 全部代码被迫可见游戏内部成员，破坏封装边界 |
| `Godot.NET.Sdk/4.5.1` | Godot 项目 SDK | AutoPilot 用 `Microsoft.NET.Sdk` |
| `STS2-RitsuLib` | 生命周期/事件框架 | AutoPilot 不依赖它 |

因此采用**工程隔离**：两个 dll 独立编译，统一部署为"一个模组包"。对玩家而言无差别。

## 目录结构

```
AutoPilot/
├── src/                          # AutoPilot 本体（26 个 .cs）
├── third_party/
│   └── CombatSolver/             # 融合的战斗求解器（710 个 .cs）
│       ├── CombatSolver.csproj   # 已改本机路径 + CopyModOnBuild=false
│       ├── src/                  # 求核心源码（零改动）
│       ├── tools/CombatSolver.MemoryCleaner/
│       ├── LICENSE
│       └── THIRD_PARTY_NOTICES.md
├── AutoPilot.csproj              # ⚠️ 含 third_party 排除规则
└── AutoPilot.sln                 # 含两个工程
```

## 构建

```bash
# 在仓库根 D:\尖塔模组\ 下
python build_all.py            # 构建两个工程
python build_all.py autopilot  # 只构建 AutoPilot
python build_all.py solver     # 只构建 CombatSolver
```

产物：
- `AutoPilot/bin/Release/AutoPilot.dll`
- `AutoPilot/third_party/CombatSolver/.godot/mono/temp/bin/Release/CombatSolver.dll`

## 部署

```bash
python deploy_autopilot.py
```

一次完成：停游戏 → 拷 `AutoPilot.dll` + kb → 拷 `CombatSolver.dll` + manifest → MD5 校验。

## ⚠️ 维护注意

### 1. 排除规则不可删

`AutoPilot.csproj` 里必须有：

```xml
<ItemGroup>
  <Compile Remove="third_party\**\*.cs" />
  <Compile Remove="third_party/**/*.cs" />
  <None Remove="third_party\**\*" />
  <EmbeddedResource Remove="third_party\**\*" />
</ItemGroup>
```

否则 `Microsoft.NET.Sdk` 的隐式 `Compile` glob 会递归吞掉 CombatSolver 的 710 个源文件，
导致 **280 个编译错误**（缺 `STS2RitsuLib` 命名空间 / 缺 unsafe）。

### 2. 升级上游

CombatSolver 更新时，替换 `third_party/CombatSolver/src/` 与 `CombatSolver.json` 中的版本号即可，
`CombatSolver.csproj` 的本机路径改动（`SteamRoot`/`Sts2Dir`/`RitsuWorkshopRoot`/`CopyModOnBuild`）需保留。

### 3. 桥接零改动

`AutoPilot/src/SolverBridge.cs` 是**运行时反射**桥接，不依赖编译期引用。
只要 `CombatSolver.dll` 在 mods 目录，桥接即可探到，无需任何代码修改。

### 4. RitsuLib 依赖

CombatSolver 硬依赖 `STS2-RitsuLib` 0.6.0+（Steam 创意工坊 `3747602295`）。
若 RitsuLib 在 `settings.save` 中被禁用，CombatSolver 将加载失败。

### 5. ⚠️ UI 剔除（窗口已移除）

用户不需要 CombatSolver 的战斗路线规划器窗口。改造分两处：

**a) `src/UI/SolverOverlay.cs`**
- `internal static bool UiSuppressed { get; set; } = true;`（UI 总开关）
- **全部 23 个 `Show*` / `Refresh*` / `Apply*` 公开入口方法**首行短路：
  ```csharp
  // 【AutoPilot 融合改造】UI 已剔除：入口短路，仅保留引擎调用语义。
  if (UiSuppressed)
  {
      return;
  }
  ```
- `EnsureCreated(host)` 短路（`_layer = null`）；`ShowLayer()` 短路
- 效果：`_layer` 恒为 null → `IsVisible` 恒 false → 引擎侧控制流照常

⚠️ **必须给每个入口都加短路**：`ShowSearching` 等方法内部存在**无 null 检查**的 `RouteRows[0].TurnLabel` 解引用，
只改 `EnsureCreated` 会导致 `NullReferenceException` → `SEARCH_SETUP_FAILURE stage=combat_root_snapshot`
→ **所有战斗搜索全部失败**（实测踩坑：2MB 日志全是同一异常）。

⚠️ **不要动 `SolverDispatcher`**（它是主线程调度器，不是 UI）；`SolverHudButton` 是死代码。

**b) `src/Runtime/SolverSettings.cs`** —— 固定策略档
- `ApplyAutoPilotFusionDefaults(data)`：
  `{ PerformancePreset=Medium, SearchTimeLimitSeconds=60d,`
  `  ActTransitionBossHpStrategy=MinimizeHpLoss, FinalBossHpStrategy=MinimizeHpLoss,`
  `  AcceptableBattleHpLoss=0, StopAtAcceptableBattleHpLoss=true }`
  - Medium 档 = BeamWidth 60 / MaxExpandedNodes 120000；时间预算压到 **60 秒**
  - `AcceptableBattleHpLoss=0 + StopAtAcceptableBattleHpLoss=true` = 找到零战损路线立即停止；
    找不到时由 Beam 的 `SoldHpPenalty` 选**最低战损**
- **必须同时套用于 `CreateCurrentDefaults()` 和 `Load()`**：
  磁盘上已存在的 `%APPDATA%\SlayTheSpire2\combat_solver_settings.json` 会覆盖默认值，
  所以 `Load()` 里 `migrated = ApplyAutoPilotFusionDefaults(migrated);` 不可省。

### 6. ⚠️ 战斗观测量：`ap battle`（UI 移除后的唯一出口）

窗口被剔除后，**战损数 / 搜索耗时**在游戏里再也看不到。改造把它们搬进 `ap` 控制台：

**a) CombatSolver 侧（融合新增文件，上游可干净 diff）**
- `src/Runtime/SolverTelemetry.cs` —— `public readonly record struct SolverTelemetrySnapshot` + `Build(...)`
  字段：`HpLostThisFight / ProjectedBattleHpLost / ProjectedBattleHpLossKnown / OnlyDeathRoutesFound / SoldHp /
  FutureSoldHp / SearchSeconds / SearchSecondsTotal / RouteSeconds / ExpandedNodes / ReviewedWorldlines /
  StartTurn / SearchedTurns / SearchBoundaryCn / Phase / CombatActive / FullAutoEnabled / ...`
- `src/Runtime/SolverControllerTelemetry.cs` —— `SolverController` 的补充 `partial`：
  `public static SolverTelemetrySnapshot CaptureTelemetry()`（读取静态会话字段 `_combat/_search/_deployment/_solverDisabled`）
- `src/Runtime/BattleDamageTracker.cs` —— 新增只读 `HpLostSoFar`（**本场实际掉血**，每场 `Begin` 重置）、
  `SearchElapsedTotal` + `RecordSearchElapsed(result)`（按结果对象去重累加本场搜索耗时）

⚠️ 记录"本场掉血"必须用 `BattleDamageTracker.HpLostSoFar`，**不要用 `SolverResult.BattleHpLostSoFar`**——
后者只是某一次搜索开始时的快照，战斗打到一半会严重偏低。

**b) AutoPilot 侧**
- `src/BattleStats.cs` —— 反射 `SolverController.CaptureTelemetry()`，逐属性读快照并渲染中文战报；
  跨场累计（`FightsSeen / HpLostAccumulated / SearchSecondsAccumulated`）在 `CombatActive` 由 true→false 时结算
- `src/ApCmd.cs` —— 新子命令：
  - `ap battle`（别名 `ap b` / `ap 战况` / `ap 战损`）→ 本场战报
  - `ap battle total` → 追加跨场累计
  - `ap battle reset` → 清零累计

⚠️ 反射 **按属性名逐个取**（`Convert.ChangeType` 兼容 int/long/double/bool/string），比按位置解构稳；
`SolverTelemetrySnapshot` 与 `CaptureTelemetry` **都必须是 public**（反射用 `BindingFlags.Public`）。

## 验证记录

### 2026-09-26 —— 融合构建
- `build_all.py` → **autopilot OK (0 err / 30 warn) + solver OK (0 err / 0 warn)**
- 自编译 `CombatSolver.dll` = 6,424,064 B vs 官方发布版 6,424,576 B（仅差 512 B 构建元数据）
- 游戏内加载验证：`CombatSolver.Entry` 初始化成功，**68 个 Harmony 补丁全部应用**

### 2026-09-27 —— UI 剔除 + 固定策略档
- 构建产物 == 已部署 DLL：md5 `e50fad6af9f2`，6,424,576 B
- DLL 元数据堆确认含 `get_UiSuppressed` / `set_UiSuppressed` / `ApplyAutoPilotFusionDefaults`
  （⚠️ .NET 元数据字符串是 **UTF-8 + NUL**，不是 UTF-16，搜成员名要用 utf-8）
- 实机验证 `Load()` 覆写：`combat_solver_settings.json` 由 2952B → 2074B，
  `searchTimeLimitSeconds` 120→**60**、`*_boss_hp_strategy` ProgressionFirst→**MinimizeHpLoss**
- 实机确认窗口消失（开战截图无任何浮层），战斗日志 2MB 持续输出
- ⚠️ 首次 UI 剔除版本踩坑：`ShowSearching` 内 `RouteRows[0]` NRE → 已用 23 处入口短路修复

### 2026-09-27 —— 战斗观测量搬进控制台（`ap battle`）
- CombatSolver.dll md5 `d63d9ed3d2d8`（6,436,864 B）、AutoPilot.dll md5 `1ee04441e8cb`（314,880 B），均部署成功
- 双工程编译 **0 error**（AutoPilot 仅 nullable 注释类 warning）
- DLL 元数据堆确认含 `CaptureTelemetry` / `SolverTelemetrySnapshot` / `HpLostThisFight` /
  `SearchSecondsTotal` / `SearchBoundaryCn` / `RecordSearchElapsed`
- 实机加载：AutoPilot 与 CombatSolver 初始化均成功，`SEARCH_SETUP_FAILURE=0`、`NullReferenceException=0`

## 启动游戏（本机可靠方式）

```powershell
Start-Process 'steam://rungameid/2868840'
```
⚠️ `Start-Process -FilePath 'E:\steam\steam.exe' -ArgumentList '-applaunch','2868840'`
与直接启动 `SlayTheSpire2.exe` 在本机**都无效**，必须走 `steam://` 协议 URL。
