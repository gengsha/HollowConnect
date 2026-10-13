# Hollow Knight Boss 联机静态审计（0.1.6）

本次只读审计没有修改游戏、存档或联机 DLL。目标安装目录为 `E:\SteamLibrary\steamapps\common\Hollow Knight`。

## 已扫描范围

- `Assembly-CSharp.dll`：SHA-256 `5944411BD93830369390A4B51766EE68C4AB26195B299E25A07B5E7D0E00086D`，4,064,768 字节。
- `PlayMaker.dll`：作为 FSM 依赖载入；未执行 Unity 或 PlayMaker。
- `hollow_knight_Data`：1,854 个不超过 250 MB 的资源文件，扫描 ASCII 资源名和场景线索。
- 程序集共 3,578 个类型；现有 HKMP 注册表 256 条目、237 种实体类型。注册表数量代表实现清单，不代表每种 Boss 已经实战通过。

机器可读的原始结果和关键词报告保存在开发工作区的 `.test-results/boss-audit-0.1.6/`（`audit.json`、`audit-report.md`）。它们是本机诊断资料，不进入发行包。

## 最高优先级候选

| 优先级 | 发现 | 可能结果 | 需要的验证 |
|---|---|---|---|
| P0 | 原生同时存在 `BossSceneController.EndBossScene`、`BossSequenceController.FinishBossScene`、`FinishLastBossScene`、`DoDreamReturn` 和多个 `GGCheck*` FSM。当前 0.1.6 只在实际 `EndBossScene` 后同步完成，并在传送时恢复连战上下文。 | 普通连战中段正常，但最后一战、特殊结束场景或梦回路径可能一端留在 Boss 房、另一端进入结算/下一场，或画面冻结。 | 从完整三门最后一战开始；验证 `FinishLastBossScene`、自定义结束场景、梦回、双方 F8，以及加载中断线。 |
| P1 | `EntitySpawner` 对多种生成路径仍使用 `clientFsms[0]`、`[2]`、`[3]`、`[7]`。相同 Boss 在普通房、神居、复战和变体中 FSM 列表可能不同；Galien/Markoth 已改为按 FSM 名称查找，但其余路径没有统一做到。 | 客机不生成弹幕/召唤物、取错 FSM、直接异常；主机继续，客机房间静止或弹幕缺失。 | 每个 Boss 记录 FSM 名称和顺序；触发每一种召唤后对比实体 ID、数量、位置、激活、碰撞和回收。 |
| P1 | 程序集有全局 `ObjectPool`、`PersonalObjectPool`、`EnemySpawner`、`TrackSpawnedEnemies` 和 `SpawnJarControl`。这些不是单纯的“出现一个 GameObject”，而是带预生成、复用、回收、跟踪列表的生命周期。 | 回收对象重用旧网络 ID，迟加入看到重复实体；权威离房后新对象未接管，或旧对象的 FSM/碰撞状态污染下一轮。 | Collector 罐子、Uumuu 子体、Soul Master 球、Dung Defender 球、Radiance 钉/球/平台、Grimm 弹幕分别测试回收和重新生成。 |
| P1 | `HealthManager` 同时具有 `TakeDamage`、`ApplyExtraDamage`、`Die`、`SetIsDead`、`hasSpecialDeath`、`OnDeath`、无敌方向和 Godhome HP 缩放；现有同步还需要依靠 FSM 快照推断阶段。 | 零血不等于 Boss 完成；护甲、眩晕、二阶段、特殊死亡可能造成 HP 一致但阶段不一致，或提前发送死亡墓碑。 | 每个阶段分别记录 HP、`isDead`、无敌、FSM 状态、BossScene 完成事件；特别检查 False Knight、Soul Master、Lost Kin、Radiance、Pure Vessel。 |
| P1 | 复杂 Boss 的主体、子体和场景机关分别注册，例如 False Knight 地板/头部、Mawlek 手臂/头、Mantis Lord 笼和地板、Soul Master 窗口、Radiance 平台、Grimm 火柱。 | 父体同步而子体缺失，或父体死亡先于子体清理；接管时只恢复一部分，导致碰撞、相机或阶段卡死。 | 迟加入、销毁一个子体后权威离房、同房重载；每次都核对父子实体树和活跃/碰撞状态。 |
| P1 | 更正：普通 HP/无敌数值变化即发送，无变化时每秒保活；FSM 动作/变量另走快照。 | 应检查原生阶段字段、事件和引用是否齐全，不能把一秒保活误当作变化发送延迟。 | 对每个阶段边界在 100–200 ms 内采样；故意在眩晕、变身、落地和死亡前断线或换权威。 |
| P2 | `Music`、`Rotation`、`GravityScale`、`HazardRespawn`、`ChildrenActivation` 等组件覆盖面很广，但它们是按注册表/组件启用的，不是按 Boss 语义自动推导。 | 画面、音效、朝向或场地碰撞不一致，可能进一步影响玩家命中和死亡点。 | 每个 Boss 的动作和场地对象逐项核对组件是否真的存在；没有组件时不要假设已同步。 |

## 按 Boss 家族的风险排序

| 家族 | 静态证据 | 当前结论 |
|---|---|---|
| False Knight / Failed Champion | 主体、头部、桶、地板、`HealthManager` 特殊死亡和多阶段 FSM | 假骑士专项已通过一部分；需要复战/神居、迟加入和地板回收验证。 |
| Brooding Mawlek | 主体、左右手、头部、Dummy，带重力/深度/音乐组件 | 父子对象多，尚无本轮专项；P1。 |
| Mantis Lords | Main/Sub 两个战斗主体、笼子、开场地板和挑战提示 | 同名对象和阶段切换风险高；P1。 |
| Soul Master / Soul Tyrant | Phase2、Orb Spinner、Fake Quake、窗口和大量 `SpawnFromGlobalPool` | FSM/生成/跨阶段三重风险；P1。 |
| Watcher Knights | 多个相同 `Black Knight`，场景完成依赖多个 HealthManager | 同名实体、重复注册和全 Boss 完成判断需专项；P1。 |
| Collector | `Spawn Jar`、`SpawnJarControl`、罐内三种敌人和对象池 | 动态内容最复杂之一；P1。 |
| Uumuu | Uumuu 主体、Quirrel Land、Ooma/Ooma Core/Corpse | 爆炸后子体和剧情对象的生成/清理需专项；P1。 |
| Dung Defender / White Defender | 大小粪球、钻地、尸体，普通与神居变体 | 位置、池复用、特殊死亡需专项；P1。 |
| Nosk / Winged Nosk | 变体、Blob、感染气球和 Dropper | 变身与多种生成器组合；P1。 |
| Radiance / Absolute Radiance | 球、钉、钉组合、平台、深渊复活点，多场景阶段 | 场地和特殊结束路径最多；P0/P1。 |
| Grimm / Nightmare King Grimm | Grimm 已验证火蝠、火球、火柱；NKG 仍是另一套动作/资源组合 | Grimm 修复不能自动证明 NKG；P1。 |
| Galien / Markoth / Xero / Gorb / Hu / Marmu / No Eyes | 远程弹幕、护盾、子镰刀、固定 FSM 名称 | Galien 接管已专项通过；其余需要断线接管和弹幕回收专项。 |
| Oro / Mato / Sheo / Sly / Pure Vessel | 近战连段、组合动作、Godhome 最后序列 | 生成少但 FSM/动作/结算风险高；P0/P1。 |

## 建议的验证顺序

1. 完整三门的最后一战和结算路径，先排除 P0 换场/黑屏/回梦。
2. Watcher Knights、Mantis Lords、Soul Master、Collector、Uumuu，覆盖同名多实体和对象池。
3. Dung Defender、Nosk、Radiance、Pure Vessel，覆盖阶段、场地、特殊死亡和 hazard respawn。
4. Nightmare King Grimm 以及所有梦斗士，覆盖动态弹幕和权威转移。
5. 真实两台机器的樱花 FRP UDP 线路；同机双进程只能证明协议和 Unity 生命周期，不能证明公网隧道下的丢包/乱序恢复。

每个专项至少记录：进入顺序、当前房主、Boss 序号/绑定、实体树、HP/无敌/阶段、生成/回收、原生完成事件、F8 双向、迟加入、权威离房和加载中断线。没有这些证据时，报告应保持“候选风险”，不要直接改代码。
