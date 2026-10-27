# 梦境 Boss 前置跑酷平台本地化

按用户要求，前置平台跑酷采用首次守梦人的原生本地逻辑；Boss 战斗、战斗场地平台和辐光上升台阶不改。

## 修改前观察

先使用原生产 DLL，在共享进度开启的现有两份隔离游戏中，逐个进入五种剧情梦境入口。没有强制 Boss 状态或写入击败标记。

| 场景 | 原生 DreamPlatform 数 | 修改前网络平台副本 | Boss 实体 |
|---|---:|---:|---|
| 失败冠军 Dream_01_False_Knight | 15 | 15 | FalseKnight |
| 灵魂暴君 Dream_02_Mage_Lord | 4 | 4 | SoulTyrant、SoulTyrantPhase2 |
| 失落近亲 Dream_03_Infected_Knight | 15 | 15 | BrokenVessel、LostKin |
| 白色防御者 Dream_04_White_Defender | 0 | 0 | 入口时附加战斗尚未开始，不据此判定战斗实体缺失 |
| 灰色王子 Dream_Mighty_Zote | 0 | 0 | GreyPrinceZote（含原生遗体实例） |

结果在 `.analysis/dream-traversal-local-20261024/baseline.json` 和其引用的五组双端日志中。这证明了前三处跑酷平台当前被同步，不能单凭结构观察宣称已复现玩家截图中的所有卡路条件。

## 边界

`LocalStoryScene.Contains` 仍只匹配 `Dream_Nailcollection`。新增平台判定仅匹配上表前三个场景中具有原生 `DreamPlatform`、`showOnEnable=false`、内外触发器齐全的物件；递归实体处理在构造实体前跳过它们。初始、迟到和生成登记共用这个入口。

这些平台的 `TriggerEnterEvent` 只接受本机骑士，由原生 Start 订阅的 Show/Hide 控制显隐。原生反编译确认 `showOnEnable=true` 平台不订阅距离显隐回调，因此保留其同步路径。没有直接调用 Show/Hide、修改原生程序集，或让整个 Boss 场景脱离同步。

`Dream_Final_Boss`、`GG_Radiance` 不匹配新条件。原有 `DreamPlatformComponent`、`RadianceArenaComponent`、`BossRoomSynchronizer`、`BossSceneLayout`、实体注册表及 `CoopRuntime` 的本次修改前后源码哈希一致，记录于 `unchanged-source-verified.json`。此前守梦人剧情碰撞隔离和永久进度共享修复保留。

## 限定验证

新增 `DreamTraversalChecks.cs`，仅检查三处受影响跑酷路线：所有平台数量、无网络副本/所有者、迟到登记不接管；Boss 实体种类及数量与基线一致；每种平台尺寸选一块，双方轮流实际接近、原生显隐、真实重力落地及离开。共享进度始终开启，不通过模拟完成 Boss 来缩短测试。

开发检查 `traversal-local-20261024-a1`、`a2` 保留。等待端的角色从远处落回平台附近，使平台显隐比较失效；a2 日志记录其实际位置。测试随后固定等待时的刚体位置，落地时恢复原有约束和真实重力，并固定双方平台抽样顺序。生产 DLL 未因此改变。失败记录不算验收通过。

首次守梦人回归的 `traversal-first-dreamer-20261024-final` 客端没有产生探针日志，原生启动日志停在缺少 Hero 的初始化阶段，主端等待连接超时；未进入待测场景。按环境启动失败保留证据，只补跑这一项一次，三组已通过的梦境跑酷不重跑。

最终还检查首次守梦人平台作为直接依赖，不运行完整 Boss 或房间矩阵。只使用现有隔离游戏和禁止保存的内存角色、localhost UDP；未修改个人存档、打包、安装或发布。原生异常检查只允许进 Town 前既有的一次 `GameManager.SetupHeroRefs` 启动空引用；场景流程中的任何新异常均失败。

最终结果和受测哈希见 `.analysis/dream-traversal-local-20261024/final-results.json`。这项验证不等于完整 Boss 战斗、辐光全流程或双机 FRP 验收。

最终 4 组双端检查全部通过，生产及 Smoke 哈希一致，两端完成标记、严格日志及场景原生异常检查均通过：

| 检查 | `.test-results/` 下的结果目录 |
|---|---|
| 失败冠军：15 块原生平台；小、中、大三种尺寸独立显隐和真实落地 | `traversal-local-20261024-a3-dream-01-false-knight` |
| 灵魂暴君：4 块原生平台；小、中、大三种尺寸独立显隐和真实落地 | `traversal-local-20261024-final-dream-02-mage-lord` |
| 失落近亲：15 块原生平台；小、中、大三种尺寸独立显隐和真实落地 | `traversal-local-20261024-final-dream-03-infected-knight` |
| 首次守梦人：原有本地平台直接依赖回归 | `traversal-first-dreamer-20261024-final-a2` |

生产输出为 `src/HollowConnect/bin/Release/net472`，冻结哈希如下：

| DLL | SHA-256 |
|---|---|
| HKMP.dll | 91BF38F5E9DF04C50B8295C3AFA3CFC6DB1402664F96D35764CC9D739CADA083 |
| HollowConnect.dll | AFC67E7EDA957F4116A1A881217533659E686E69A48069B61A1D74BE2D3D2829 |
| HollowConnect.Core.dll | 20D8D11C22ADE2F4661A1FCE915543F77905ABE4CD0EC678E0496D1D0627CD69 |
