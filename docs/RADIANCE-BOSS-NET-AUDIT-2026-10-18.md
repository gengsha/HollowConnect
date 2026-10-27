# Radiance / Absolute Radiance 联机静态审计（2026-10-18）

本报告针对“进入 Radiance 房间后镜头锁定并出现白底黑字、传送队友造成镜头错位，以及 Absolute Radiance 二阶段坠落后没有落到平台并持续掉血”的反馈。审计只读完成，没有修改源码、游戏安装、存档或测试插件。

## 扫描边界与输入

当前本机安装目录已找到并扫描：`E:\SteamLibrary\steamapps\common\Hollow Knight`。

| 输入 | 结果 |
|---|---|
| `hollow_knight_Data/Managed/Assembly-CSharp.dll` | 4,064,768 字节；SHA-256 `5944411BD93830369390A4B51766EE68C4AB26195B299E25A07B5E7D0E00086D` |
| `hollow_knight_Data/Managed/PlayMaker.dll` | 212,480 字节；SHA-256 `0EF0E7829D125E1F632C8A189260EC6C6882630BE6932C8C7AE032EFBC53469A` |
| `hollow_knight_Data` | 1,855 个文件；Cecil 静态扫描 3,578 个类型 |
| 技能原始结果 | `.test-results/boss-audit-radiance-2026-10-18/audit.json` 与 `audit-report.md` |

程序集扫描只证明原生类型、方法和资源名存在，不证明每个 prefab 在本次房间中一定启用。下面的风险等级按审计技能的定义使用：**确认的静态风险**表示当前实现缺少对应的注册/生命周期保护；**强候选**表示机制与症状吻合但仍需运行时区分；**需要运行时验证**表示只有名称或通用引擎路径证据。

## 优先风险

| 优先级 | 触发 | 双端分歧与后果 | 证据 | 等级 | 最小运行时探针 |
|---|---|---|---|---|---|
| P0 | 进入 `GG_Radiance` 或传送队友进入该房间 | `CameraLockArea` 可能由远端实体快照启停，远端的原生镜头锁/控制锁与本地 Hero 不在同一时序；传送结束后 `PositionToHero` 只校正位置，不能清理已经进入的原生过场/锁定状态。结果是镜头固定、玩家与镜头错位，随后白底黑字结算/挑战遮罩可能只在一端出现。 | `src/HKMP/Resource/entity-registry.json:17-19` 的 `CameraLockArea` 没有 `networked:false`；`src/HKMP/Game/Client/Entity/EntityManager.cs:615-627` 扫描并注册它；`src/HKMP/Game/Client/GamePatcher.cs:517-575` 对原生 `IsInApplicableGameState` 做联机 IL 改写；`src/HollowConnect/CoopRuntime.cs:1022-1063` 传送完成只重置位置和镜头跟随。 | **确认的静态风险；本次症状的强候选** | 两种先入顺序分别做：直接进房、房内传送队友、传送时触发原生入场；每帧记录 `CameraController.mode`、Hero 输入/控制锁、`GameManager.gameState`、`HeroController.isHeroInPosition`、Boss challenge UI/scene fade 和 `CameraLockArea` 活跃状态。 |
| P0 | Absolute Radiance 二阶段坠落/爬台阶段 | 玩家可能没有拿到正确的深渊/平台复活点，继续处在深渊或危险伤害中；另一端的阶段、平台激活和 hazard respawn 进度不同。 | `src/HKMP/Resource/entity-registry.json:1499-1512` 的 `Absolute Radiance` 只有 `Control`，没有 `HazardRespawn`；`src/HKMP/Game/Client/Entity/Component/HazardRespawnComponent.cs:42-218` 明确依赖 `Control`、`P2A Hazard`、`Ascend Respawns`、`Climb Plats1`，并写入 `HeroController.SetHazardRespawn`/`PlayerData.SetHazardRespawn`。 | **确认的静态风险；具体失位点强候选** | 二阶段坠落前后逐帧记录 Absolute Radiance FSM 状态、`Radiant Plat`/`Abyss Pit` 的 active/collider/位置、每端 hazard respawn marker、`cState.hazardDeath/hazardRespawning`、速度和生命；分别测先入、后入、断线接管。 |
| P1 | 平台/深渊对象被快照重建或传送落点选取 | 通用落点算法只从 layer 8 向下射线，未识别 Radiance 当前平台阶段；如果平台未加载、被停用或目标坐标在场景道具旁，可能无安全落点或落入浮空四边形/非承重点。 | `src/HollowConnect/CoopRuntime.cs:923-958` 的 `TryWarpLanding`；`src/HollowConnect/CoopRuntime.cs:1052-1063` 在目标场景中直接使用该落点；`src/HKMP/Resource/entity-registry.json:1504-1512` 将 `Radiant Plat`/`Abyss Pit` 作为可同步 FSM 对象。 | **强候选** | 在平台出现、平台切换、二阶段坠落和传送四种时刻记录射线命中 collider、法线、目标层、平台实体 ID、实际落点与两端位置；确认是否发生“命中非平台”或“平台尚未 ready”。 |
| P1 | Radiance 动态球、钉和对象池回收 | 主机生成/回收与客机的实体注册、稳定 ID、FSM 状态可能不一致；重入或权威转移时会出现弹幕缺失、重复或旧状态。 | 注册表 `src/HKMP/Resource/entity-registry.json:1250-1277` 网络化 `Radiant Orb`、`Radiant Nail Comb`、`Radiant Nail`；原生扫描发现 `ObjectPool.Spawn/Recycle`、`SpawnFromPool`、`TrackSpawnedEnemies` 等通用池路径；历史 `docs/evidence/warp-stag-performance-2026-10-17.json` 记录 `GG_Radiance` 状态 3/4 被拒绝 753 次。 | **强候选；具体 FSM 尚未定位** | 逐次记录动态对象的稳定网络 ID、owner、FSM 名称/状态、active、collider、spawn/recycle 时间；覆盖 late join、同房重载和权威离房。 |
| P1 | Boss 完成与白底黑字/尸体过场 | 一端先收到空的 additive layout 或完成上下文，可能卸载原生 BossSceneController，跳过本地尸体/过场，留下无法离开的房间；原生 `EndBossScene` 与网络完成消息的先后需要对齐。 | `src/HKMP/Game/Client/Entity/BossRoomSynchronizer.cs` 的 `End`/`pending`/`SceneLayout` 处理；`src/HKMP/Game/Client/Entity/BossSceneLayout.cs:24-38` 只同步 additive Boss 场景选择；原生扫描包含 `BossSceneController.EndSceneDelayed`、`BossSequenceController.FinishLastBossScene`、`HeroController.EnterSceneDreamGate`。 | **强候选** | Boss 死亡瞬间让另一端传送或断线；记录 `EndBossScene`、corpse FSM、BossSceneController 是否仍在、additive scene、fade/UI 状态及最终场景。 |
| P2 | 联机镜头状态改写本身 | 连接/暂停条件被插入 `CameraLockArea.IsInApplicableGameState`，可能改变原版入场镜头的适用判断；它不能解释所有症状，但会放大入场时序差异。 | `src/HKMP/Game/Client/GamePatcher.cs:521-572`。 | **需要运行时验证** | 同一存档离线、主机、客机三组对照，记录 IL hook 前后 camera mode、锁区状态和场景 fade。 |

## 已有历史证据的含义

- `docs/evidence/warp-stag-performance-2026-10-17.json` 的 `GG_Radiance` 753 次状态 3/4 拒绝告警证明有日志和处理开销，**没有**记录具体实体/FSM，因此不能仅凭这条证据修改某个状态映射或把告警静音。
- `docs/evidence/final-boss-runtime-20261017.json` 明确把 Dream/Radiance continuation 排除在该次 final-boss 验收之外；该记录不能证明本次 Radiance 问题已通过。
- 旧的 `CameraLockArea` 静态身份差异记录（`docs/evidence/hierarchy-diagnostics-0.1.13.json`）说明它曾参与实体生命周期问题，但版本和运行上下文不同，只能作为历史线索，不能冒充当前 0.1.22 的实机复现。

## 最小实施候选（待审阅后执行）

1. 把 `CameraLockArea` 纳入本地触发白名单，并让它完全走每端原生触发；保留 Boss、弹幕和场景 layout 的网络同步。进入/传送完成后增加一次受条件保护的本地镜头恢复，只在原生入场已结束且 Hero 可控时执行。
2. 为 Absolute Radiance 的阶段复位补齐与当前原生对象匹配的 hazard respawn 初始化；收到阶段数据前不把玩家放到猜测坐标，平台/深渊未 ready 时拒绝传送落点并等待下一次安全快照。
3. 给 Radiance 传送增加阶段感知的落点选择：优先当前激活平台/原生 hazard marker，排除 `RadianceAbyss`、伤害碰撞体、Boss 过场和未 ready 的 additive scene；传送后再校正镜头，不能覆盖正在运行的原生过场。
4. 为动态 Radiance 实体和 Boss 完成路径增加精确诊断字段（实体类型、稳定 ID、FSM 名称/状态、owner、scene、active、camera mode、hazard marker），以便下一轮运行时探针能把 753 类告警定位到具体对象。

## 覆盖矩阵（本轮仅静态审计）

| 能力 | 静态扫描 | 当前结论 |
|---|---|---|
| 入场镜头锁/白底黑字 | 原生 `CameraLockArea`、Boss/scene transition 类型存在；实现路径已定位 | 未实机验证 |
| 传送进入 Radiance | `WarpTo`/`TryWarpLanding` 路径已审查 | 未实机验证 |
| 二阶段坠落/平台复位 | `HazardRespawnComponent` 与 registry 差异已定位 | 未实机验证 |
| 动态球/钉生成回收 | 原生池/生成路径和注册表已发现 | 未实机验证 |
| Boss 完成、尸体、场景切换 | `BossRoomSynchronizer`、`BossSceneLayout` 和原生完成方法已对照 | 未实机验证 |
| late join / disconnect / authority takeover | 当前报告只列探针，没有运行 | 未验证 |

本报告是修改前的审阅材料。下一步若确认实施，将只改上述最小范围，并先跑对应的核心/Smoke 静态检查，再安排 Radiance 双端专项；不会把旧版本证据或一次同机成功重试当作当前版本通过。
