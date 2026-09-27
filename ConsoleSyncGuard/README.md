# ConsoleSyncGuard · 杀戮尖塔2 联机控制台卫士

让《杀戮尖塔2》(Slay the Spire 2) 在 **多人联机** 中使用开发控制台（按 `~`）时**不再出现"数据不同步 / Desync Detected / 被踢出房间"**。

> 适配版本：游戏 **v0.111.0**（sts2.dll）。抢先体验更新频繁，游戏大版本更新后如失效，请重新编译（见下文"从源码编译"）。

---

## 一、为什么联机用控制台会"数据不同步"？

通过反编译 v0.111 的 `sts2.dll` 确认了官方机制：

1. 多人是**主机权威 + 确定性动作队列**：任何"改状态"的控制台指令，官方都以 `ConsoleCmdGameAction` 经 `ActionQueueSynchronizer` 由**主机排序并广播**，所有玩家按相同顺序各执行一次。
2. 游戏用 `ChecksumTracker` 在"每条指令执行完 / 玩家回合起止 / 事件结束 / 休息点离开"时对**全状态**做 XxHash32 校验；客户端把校验值发给主机比对，不一致 → `StateDivergence` → **主机直接断开该玩家**（这就是你看到的"不同步/被踢"）。
3. 会造成分叉的典型情况：
   - **客机**输入了"仅本地生效"的指令或走了**非同步路径**改状态 → 只有一台机器变了；
   - 有人开 **风灵月影/XMODhub 之类外部修改器**改内存（这种**任何模组都救不了**，必定掉线）；
   - 全员 **mod 列表/加载顺序/游戏版本不一致**（种子与随机序列不同 → 校验必炸）。

## 二、本模组做了什么

默认启用 **主机权威模式（hostonly=on）**：

- **房主（Host）**：照常按 `~` 输入 `gold / heal / card / relic / power / energy / damage / win` 等任意联网指令 → 原样执行并经官方同步队列广播给所有人 → **全员状态一致，不会不同步**。
- **客机（Client）**：输入会"改状态"的联网指令 → **本地直接拦截并给出提示**（"请由房主输入"），绝不只在一台机器上生效 → 从源头消除分叉。纯本地指令（`instant`/`trailer`/`help` 等）不受影响。
- 拦截和放行都会写入游戏日志（Generic/GameSync 分类，前缀 `ConsoleSyncGuard`），方便事后排查。

如果你更希望客机也能直接输入（与游戏原版行为一致，官方会把指令转发给主机执行）：
在任意一方的控制台输入 **`mpmode hostonly off`** 即可关闭主机权威模式（风险自负）。

### 新增控制台指令（装了本 mod 后自动出现）

| 指令 | 作用 |
|---|---|
| `mpstatus` | 显示当前网络角色(Host/Client/单机)、对局状态、守卫模式；列出全部控制台指令并标注"联网同步 ✅ / 仅本地 ❌" |
| `mpmode hostonly on/off` | 主机权威模式开关（默认 on） |
| `mpmode loud on/off` | 详细日志开关（默认 off） |
| `mphelp` | 原理与用法速查 |

## 三、安装

> 需要**主机和每个队友都安装本 mod**（它是纯逻辑 mod，`affects_gameplay=false`，不影响存档数据库；但所有人都装才能让客机侧同样受保护）。

1. 关闭游戏。
2. 把 `ConsoleSyncGuard` 文件夹（内含 `ConsoleSyncGuard.dll` 和 `ConsoleSyncGuard.json`）整个放进：
   `E:\steam\steamapps\common\Slay the Spire 2\mods\`
   即最终路径为 `...\mods\ConsoleSyncGuard\ConsoleSyncGuard.dll`。
3. 启动游戏 → 主菜单 **Mod**（模组）→ 勾选启用 **ConsoleSyncGuard** → 按提示重启游戏。
4. 联机前确认：全员游戏版本一致（`release_info.json`）、**mod 列表与加载顺序一致**（可用社区 ModSync / 顺序同步工具），**不要开风灵月影等修改器**。
5. 进多人对局后按 `~` 打开控制台，先输入 `mpstatus` 确认显示 "本机为客机/主机" 与 "hostOnly=ON"。

## 四、使用建议（防不同步清单）

- 需要改血量/金币/卡牌/遗物/能量/状态时：**让房主来输**，改动会同步给全员。
- 客机不要输入：`godmode`、`card`、`relic`、`power`、`heal`、`gold` 等任何联网改状态指令（会被本 mod 拦截并提示）；若关了 hostonly，则由官方队列转发。
- `instant`（动画加速）等**仅本地**指令不影响校验，可放心用；但注意它只对你本机生效。
- 一旦出现 "State divergence / 连接已断开"，去日志里找原因（见下），**十有八九是有人开了外部修改器或 mod 不一致**，而不是指令本身。

## 五、日志与排查

- 日志目录：控制台输入 `open logs` 打开；或直接看
  `C:\Users\<你>\AppData\Roaming\SlayTheSpire2\logs\`
- 出现不同步时，日志中会看到 `State divergence detected! ... Context: ...` 以及 `LOCAL STATE DUMP` / `REMOTE STATE DUMP`，把这几段连同你当时输入的控制台命令发给作者即可精确定位。
- 本 mod 自身日志前缀：`ConsoleSyncGuard`。

## 六、从源码编译（游戏更新后自建新版）

环境：.NET 9 SDK + 本机游戏文件。

```bash
cd ConsoleSyncGuard
dotnet build -c Release -p:Sts2DataDir="<游戏目录>\data_sts2_windows_x86_64" \
             -p:GameModsDir="<游戏目录>\mods"
```

产物自动安装到 `mods\ConsoleSyncGuard\`。若游戏大版本更新导致类型/方法变化编译失败，先解包新版 `sts2.dll`（如 `ilspycmd -p`）核对 `DevConsole.ProcessCommand`、`AbstractConsoleCmd.IsNetworked` 等签名再改。

源码结构：
- `src/ModEntry.cs` —— 入口（Harmony PatchAll）
- `src/Guard.cs` —— 守卫核心逻辑（真实多人判定 / 主机权威拦截）
- `src/Patches.cs` —— Harmony 前置补丁（拦截本机控制台输入）
- `src/Commands.cs` —— `mpstatus` / `mpmode` / `mphelp`
- `ConsoleSyncGuard.json` —— mod 清单

## 七、已知边界（诚实说明）

- **风灵月影/Cheat Engine/XMODhub 等外部修改器在联机中必然导致不同步**，本模组无法拦截外部进程写内存；联机请关闭。
- 若你在**完全干净的环境**（全员同版本、同 mod 列表、无修改器）仍能稳定复现"客机用某个联网指令 → 不同步"，请按第五节把日志与复现步骤发来，可据此做精确补丁（例如针对该指令强制走主机、或在指令执行期间临时挂起校验）。
- 默认"主机权威"是对"绝不出不同步"的最稳妥保证；它牺牲了客机直接改状态的便利。
