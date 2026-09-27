# STS2 真实对局知识库（AutoPilot 选牌引擎数据层）

> 从 **593 局真实玩家存档**训练出的选牌先验知识，自动融合进 AutoPilot 的选牌评分与 LLM 提示词。
> 全部**零依赖纯统计**生成，离线可复现。

---

## 一、数据来源

| 来源 | 局数 | schema | 关键字段 |
|---|---|---|---|
| `saito23490/sts2-run-data` issue#1（5 个 history.zip） | 292 | 8（v0.98.x） | `relic_choices` / `ancient_choice` / `event_choices` |
| 本机 `%APPDATA%\SlayTheSpire2\...\history` | 163 | 9/10（v0.107~0.111） | 额外含 **`card_choices`**（卡牌三选一）/ `potion_choices` / `rest_site_choices` / `cards_gained` / `cards_removed` / `upgraded_cards` |
| **合计** | **593** | — | 清洗后用于训练 **361 局** |

清洗规则（避免污染）：
1. **剔除 `was_abandoned`**（134 局）——主动放弃不等于战败，混入会污染标签；
2. 只保留 `game_mode == "standard"`；
3. 只保留**单角色局**（多人/换角色局语义不同）。

决策节点规模：**卡牌三选一 15,470 / 遗物选择 10,769 / 药水 9,014 / 事件 5,745 / 篝火 5,018**。

---

## 二、价值怎么算出来的

### 三条互相独立的估计

**① 生存增益（survival_gain，权重 0.45）—— 因果近似**
对每个"卡牌三选一"事件，取该局**剩余生存**（`最终层数 − 当前层数`）。
把同 `(角色, asc分桶, 幕, 当前层)` 的所有事件作为一组，组内算平均剩余生存作**基线**，
再比较「选 A 的局」比基线高/低多少。同层配对消除了"出现在后期所以胜率高"的伪相关。

**② 选择率提升（pick_lift，权重 0.35）—— 玩家共识**
该牌出现在候选时被选中的比率 vs **同一稀有度档**的期望选择率。

> ⚠️ 这里的关键修正是**按稀有度分层**。第一版没分层时，`ACROBATICS`（杂技，Silent 顶级卡）
> 被判 **−46.66 分**——因为它出现太频繁，普通牌拉高了全局基线。
> 分层后基线：`basic 0.452 / common 0.266 / uncommon 0.320 / rare 0.312`，结果立刻合理。

**③ 删牌回避（removal_avoid，权重 0.20）**
该牌被主动删除的比率，越低越有价值。

三条指标在**角色内做 z-score 标准化**后加权合成：
```
value = 0.45×z(surv_gain) + 0.35×z(pick_lift) + 0.20×z(removal_avoid)
```

### 分档
`S ≥ 0.80 / A ≥ 0.35 / B ≥ −0.15 / C ≥ −0.65 / D < −0.65`

---

## 三、验证结果（留出法 5 折交叉验证）

```
可用卡牌三选一事件：3,588
知识库 top-1 命中率: 0.539
随机基线:            0.333
相对随机提升:        +61.6%
```

分角色命中率：`WATCHER 0.821 / REGENT 0.608 / SILENT 0.605 / DEFECT 0.540 / IRONCLAD 0.478 / NECROBINDER 0.477`

> 含义：把知识库当排序器，它猜中玩家实际选择的比例是随机的 1.6 倍。
> 说明它学到的是**真实的玩家偏好**，不是数据的偶然波动。

---

## 四、知识库内容

`AutoPilot/kb/sts2_kb.json`（约 131 KB）：

| 字段 | 说明 |
|---|---|
| `cards.{角色}.{卡ID}` | `v` 价值 / `pick` 真实选择率 / `off` 样本量 / `r` 稀有度档 / `t` S~D 分档 |
| `cards_global` | 跨角色通用表（样本 ≥20） |
| `relics` | 遗物 `lift`（相对选择率）+ `off` |
| `potions` | 药水同上 |
| `synergy` | **真实协同组合**：牌 → [伙伴, PMI, 共现次数]，PMI > 0.25 |
| `remove_hint` | 建议优先删除清单 |
| `upgrade_hint` | 优先升级清单 |

### 实测验证的协同（数据自己发现的，未人工喂）

| 组合 | PMI | 说明 |
|---|---|---|
| `BARRICADE → BODY_SLAM` | **2.183** | 壁垒 → 全身撞击（Ironclad 最经典 combo）✓ |
| `FEEL_NO_PAIN → FIEND_FIRE` | 1.944 | 无痛 → 恶魔火焰（格挡流）✓ |
| `FEEL_NO_PAIN → SECOND_WIND` | 1.752 | 无痛 → 二风（格挡流）✓ |
| `INFLAME → MOLTEN_FIST` | 1.665 | 炎上 → 熔拳（力量流）✓ |

---

## 五、几个反直觉但有据的结论

- **`BODY_SLAM`（全身撞击）Ironclad 垫底 v=−1.820 [common]** —— 与用户"没格挡却打全身撞击"的抱怨完全吻合：真实玩家极少选它。
- **`FOCUSED_STRIKE`（聚焦打击）Defect 垫底 −1.853** / **`LEAP`（跃迁）−2.066** —— 公认废卡 ✓
- **`OFFERING`（献祭）Ironclad 榜首 +1.441** —— 与 v2.4.2 条件机制引擎"HP<25% 时否决自伤牌"形成良性互补（满血时才值得打）。
- **`CRUSH_UNDER`（碾碎）Regent 垫底 −1.713** ✓

---

## 六、在 AutoPilot 里怎么用

知识库是**统计先验，不是绝对真理**，因此接法是"融合 + 按样本量降权"：

1. **选牌打分**（`Selector.ScoreOne` 第 ⑧ 维度）：
   `statsBonus = value × 16 × 置信度`（截断 ±45），置信度随样本量从 0.45 增到 1.0。
   另叠加**真实协同**（PMI 加权，上限 +18）。
2. **LLM 提示词**（`Brain.LlmChooseCardReward`）：注入 `RunStats.PromptSummary`，
   逐卡给出「S档，价值+1.44，玩家选择率74%（样本23），真实协同: …」。
3. **人工偏好永远最高优先**：`ap like` / `ap ban` 覆盖一切统计与模型结论。

### 已知局限
- 样本量 593 局偏小，**WATCHER 只有 27 局**（分角色表样本不足，靠全局表兜底）；
- 数据来自他人对局，水平参差；**统计反映的是"平均玩家"，不是"最优解"**；
- 稀有度是用"出现频次"推定的代理值（存档不含真实稀有度字段）；
- 版本跨度 v0.98~v0.111，老版本的卡牌平衡可能与现版本不同。

---

## 七、复现命令

```bash
cd D:/尖塔模组
python rundata/stage1_extract.py    # 归一 + 抽取决策样本 → out/samples_*.jsonl, out/runs.csv
python rundata/stage2b_value.py     # 稳健价值估计 → out/kb_table.json
python rundata/stage3_build_kb.py   # 合成知识库 → out/sts2_kb.json + AutoPilot/kb/sts2_kb.json
python rundata/stage4_validate.py   # 留出法验证
```

拉取新数据：
```bash
python rundata/dl.py                # 下载 GitHub issue 里的 history.zip
```
把新存档丢进 `rundata/extracted/`，或直接让游戏产生新存档（自动从 `%APPDATA%` 读取），重跑上面四步即可**增量更新知识库**。

删除 `rundata/zips`、`rundata/extracted`（约 51 MB）不影响已生成的知识库。
