# 当前测试与专项完整目录（2026-10-18）

本目录只整理当前源码与已有证据，没有启动任何测试。模式、套件、参数、单项断言是不同层级，互有重叠，不能把数量相加当作独立实机测试总数。0.1.22 按用户要求直接升版打包，未追加运行测试。

2026-10-19 新增关门精英战专项：`Run-Local-Battle-Suite.ps1`，覆盖真菌荒地双蘑菇巨怪的两端先入、部分／全部击杀后进入，以及同房间延后触发；每种交换房间权威。复现、修复与本轮证据单独记录在 [关门战结算](LOCAL-BATTLE-COMPLETION-2026-10-19.md)，以下历史结果保持原口径。

2026-10-20 剧情灵魂大师定向核实：同场景门外队友、永久进度共享开/关，两组均复现房主完成后队友不开门；共享开启能补上下砸，不能解除该挡板。诊断完成不算功能验收通过，见 [灵魂大师核实](SOUL-MASTER-VERIFICATION-2026-10-20.md)。

## 当前结果口径

| 范围 | 已有记录 | 对 0.1.22 的含义 |
| --- | --- | --- |
| 核心回归 | 最后一次完整运行 200 passed / 0 failed，紧急回椅升版前 | 旧构建记录；不冒充本版重测 |
| 房间＋稳定性 | 模板修复构建的 18 组双端终态/严格日志通过 | 早于紧急回椅及升版，保留各自 DLL 哈希 |
| 紧急回椅 | emergency-bench-20261017-e，两端共 6 次通过 | 保存被拦截，验证了请求/回调，未验证真实写盘重读 |
| 回椅后普通综合复测 | emergency-bench-final-20261017-full 被用户叫停 | 未完成，不算通过；随后 defender 未运行 |
| 0.1.22 新 DLL | 升版编译和打包 | 未追加核心/游戏/安装器运行 |
| 帧性能 | 四 Boss × 两端 × 30 秒已有记录 | 白色防御者客机有一次约 100.84 ms 未归因峰值；不宣称所有卡顿消失 |

证据：[稳定性修复记录](STABILITY-FIX-VERIFICATION-2026-10-17.md)、[回椅专项](evidence/emergency-bench-2026-10-17.json)、[0.1.22 打包记录](evidence/runtime-room-0.1.22.json)。

## 核心回归内容

入口：[Program.cs](../tests/Program.cs)，项目：[HollowConnect.Tests.csproj](../tests/HollowConnect.Tests.csproj)。包含真实套接字/DTLS 回归，但不启动 Unity 游戏。支持 HOLLOWCONNECT_TEST_SELECTION 显式名单筛选；最近 100 项并没有自动按 Git 选择器。

- 共享资源来源 ID、房主历史与本机发奖回执、旧档迁移、重复/乱序/迟加入、消费后不补回、槽位隔离。
- 诊断环形缓存、死锁风险识别、离线 Boss 临时上下文、导出脱敏。
- 发送锁/编码并发、失败编码可靠数据保留、分帧主线程队列。
- 玩家及实体平滑、停止、瞬移、包突发和预测边界。
- 死亡排行榜计数、合并、保存槽位隔离与消息序列化。
- 编解码/压缩、可靠重发/选择 ACK、周期丢包、乱序/重复、序列回绕、稀疏快照。
- 房间初始/接管快照与实时包交错、旧包过滤、对象清理重入。
- 身份/引用、静态实体与对象池、动态生成来源、依赖循环和大动作批次。
- 永久能力与护符共享开关、粪便骑士单调进度、个人地图/钱/装备保护。
- 倒地/救援/F6/全队失败/重试屏障、个人检查点、旧轮次与死亡序号拒绝。
- 地图几何/屏外箭头、入场保护、传送和神居上下文。
- 五端并发 DTLS、端口重开、握手取消、会话隔离、UDP 分片、超大 Boss 包、接收错误和退出顺序。

## 双端探针模式（15 种）

通用入口：[Run-Two.ps1](../tests/Smoke/Run-Two.ps1)。Mode 选择以下模式；单独启动不等于已完成验收。

| Mode | 源码 | 覆盖内容 |
| --- | --- | --- |
| `full` | [Probe.cs](../tests/Smoke/Probe.cs) | 综合联机：敌人血量/位置/无敌、远端骨钉、接管、永久能力、地图、F6、救援、双倒地顺序、F7/F8 |
| `authority` | [Probe.cs](../tests/Smoke/Probe.cs) | 权威离房后活怪继续运动、死亡墓碑不复活、旧权威回房血量一致 |
| `join` | [JoinProbe.cs](../tests/Smoke/JoinProbe.cs) | 原生选档/新档、加入、断线、退主菜单、重开房与再加入 |
| `boss` | [BossProbe.cs](../tests/Smoke/BossProbe.cs) | 假骑士原生开战、破甲露头、阶段伤害、露头接管、晚加入与重入 |
| `godhome` | [GodhomeProbe.cs](../tests/Smoke/GodhomeProbe.cs) | 神居连战上下文、格林弹幕、休息房、普通/神居传送和已接受加载后断线 |
| `pantheon` | [PantheonProbe.cs](../tests/Smoke/PantheonProbe.cs) | 原生门进入、两种先入顺序、死亡重试、早移交/断线、生命周期与 FSM 夹具 |
| `grimm-motion` | [GrimmMotionProbe.cs](../tests/Smoke/GrimmMotionProbe.cs) | 普通/梦魇格林原生运动、动态攻击、晚加入、权威移交和菜单竞态对照 |
| `p5-motion` | [P5MotionProbe.cs](../tests/Smoke/P5MotionProbe.cs) | 五门局部连续行程、守望者后续房间、此前互救/温泉/乌恩与表现检查；不是完整五门通关 |
| `rescue` | [RescueProbe.cs](../tests/Smoke/RescueProbe.cs) | 真实骨钉救援、灵魂奖励、生命/无敌、特效、危险地形及边界夹具 |
| `death` | [DeathProbe.cs](../tests/Smoke/DeathProbe.cs) | 个人原版死亡、F6/全队失败、不同检查点、梦境/神居例外、断线及起身输入 |
| `progress` | [ProgressProbe.cs](../tests/Smoke/ProgressProbe.cs) | 鹿角站/科尼法购买、资源、护符开关、梦钉队友、七位战士之梦、粪便骑士与紧急回椅 |
| `coverage` | [CoverageProbe.cs](../tests/Smoke/CoverageProbe.cs) | 可选场景的实体/弹幕/状态观测，加性能、碰撞、平台、传送、地形、快照等断言 |
| `colosseum` | [ColosseumProbe.cs](../tests/Smoke/ColosseumProbe.cs) | 竞技场分档、入退场/复活/重入、标题音乐、结算和护符槽共享 |
| `final-ending` | [FinalBossEndingProbe.cs](../tests/Smoke/FinalBossEndingProbe.cs) | 最终 Boss 原版音乐、自我刺击/死亡尸体、个人聚焦与 A/B 结局；battle/continuation 分开 |
| `shade-inspect` | [ShadeInspectProbe.cs](../tests/Smoke/ShadeInspectProbe.cs) | 只读查看原版暗影素材，不运行暗影回收 FSM，不计战斗验收 |

## 房间套件（12 组）

入口：[Run-Room-Suite.ps1](../tests/Smoke/Run-Room-Suite.ps1)。支持 Only / StartAt 选择。

| Case | 内容 |
| --- | --- |
| `final-native-host-013` | 原生神居入场，房主先入 |
| `final-native-guest-013` | 原生神居入场，客机先入 |
| `final-lifecycle-host-013` | 房主先入的生命周期、模板、FSM 与 8 轮重连接管 |
| `final-lifecycle-guest-013` | 客机先入的生命周期、模板、FSM 与 8 轮重连接管 |
| `final-boss-013` | 假骑士正常进入/阶段/接管 |
| `final-boss-late-013` | 假骑士晚加入和延迟接管 |
| `final-grimm-host-013` | 房主权威格林运动/攻击 |
| `final-grimm-guest-013` | 客机权威格林运动/攻击 |
| `final-rescue-013` | 骨钉救援、奖励、反馈及拒绝路径 |
| `final-death-story-013` | 剧情场景原版死亡与个人返回 |
| `final-full-013` | 综合联机全流程 |
| `final-join-013` | 加入、断开、退菜单、重开房 |

## 稳定性套件（6 组）

入口：[Run-Stability-Suite.ps1](../tests/Smoke/Run-Stability-Suite.ps1)。它与房间套件有内容重叠。

| Case | 内容 |
| --- | --- |
| `resources` | 共享来源、原版领取动画、去重、转换、跨房和重连 |
| `full` | 综合联机流程 |
| `defender` | 粪便骑士原生结算、下砸暗道、梦钉白色防御者和离线锁恢复 |
| `arena-host` | 竞技场房主先领取奖励及补发 |
| `arena-guest` | 竞技场客机先领取奖励及补发 |
| `combat-profile` | 四 Boss 每场 30 秒、双端帧耗时/GC/登记与快照成本 |

## 可独立选择或组合的专项

下表参数省略共同前缀 `HOLLOWCONNECT_`。部分选项必须配合主模式/父开关，完整前置见 [Smoke README](../tests/Smoke/README.md) 与实际源码。不是“每个开关各跑一套完整游戏”。

| 专项 | 入口 | 主要开关/文件 | 内容 |
| --- | --- | --- | --- |
| 紧急回椅 | progress | EMERGENCY_BENCH=1 | 暂停/控制锁/残留加载、真实阻塞梦境加载、离线死亡；左右 Ctrl/Alt、主菜单防误触、连续按键、长椅/起身与保存请求 |
| 个人剧情触发 | coverage | STORY_SCENE；STORY_STAGE=remote-attack/progress/peer-trigger；STORY_GATE | 守梦人原生攻击与聚集、永久标记及计数同步、较慢玩家重复吸收；白王遗体远端隔离与本地掉落；出生地仅原生结束触发区接触，详见剧情修复报告 |
| 梦境前置跑酷平台 | coverage | DREAM_TRAVERSAL_SCENE；DREAM_TRAVERSAL_STAGE=schema/local；DREAM_TRAVERSAL_BASELINE | 失败冠军、灵魂暴君、失落近亲的原生本地平台显隐、真实落地、无副本及迟到接管、Boss 实体登记与修改前基线一致；不含完整 Boss 战斗 |
| 粪便骑士与隐藏梦境 | progress | DEFENDER_CHECK=1 | 原生死亡后离房重进不复活、下砸进隐藏房、真实梦钉入梦、断线后输入/刚体/镜头恢复；DEFENDER_ROUTE_INSPECT 仅观察 |
| 原版资源领取 | progress | RESOURCE_CHECK=1；RESOURCE_CONTINUATION | 矿石、护符槽、面具/容器碎片；相同来源并发和重复领取、跨房动画、整块转换、矿石消费与迟加入；after-shop 检查商店之后续发 |
| 鹿角站与地图购买 | progress | PROGRESS_BUYER；PROGRESS_PROMPT=1；MAP_BOTH=1 | 房主/客机购买、接收端已有对话、仅购买者扣钱、原房即时生效、双方买图后的角色可见性 |
| 护符开关与梦钉队友 | progress | TEAMMATE_DREAM=1；CHARM_SHARE=0/1 | 房主护符开关、真实梦钉碰撞、交替 16/17 灵魂、满魂、重连与原生聚焦回血 |
| 七位战士之梦 | progress | DREAM_WARRIORS=all；DREAM_SUMMONER=host/guest | 泽若、戈布、胡长老、马尔穆、无眼、加利安、马科斯：原生交互挑战、伤害结算、分别领奖、重进和提前伪造完成拒绝 |
| 普通与危险地形救援 | rescue | RESCUE_NATIVE_CHECK=1；HAZARD_RESCUE_CHECK=1 | 剧情/神居/梦境普通致命伤、酸水和尖刺真实碰撞；个人暗影/吉欧、落点、1 血、原生无敌与到期受伤 |
| 白宫与支撑边界救援 | rescue | RESCUE_EDGE_CHECK | placement/native-lift/exception-placement/room-placement，以及移动支撑/无支撑/新复位点回退故障注入；检查救援或原版返回 |
| 死亡镜头 | rescue / coverage | DEATH_CAMERA_CHECK=1；CoverageDeathCameraChecks | 倒地镜头、暗影锚点、危险复位/救援后恢复；按实际接线运行 |
| 玩家运动 | rescue | PLAYER_MOTION_CHECK=1 | 稀疏位置采样在真实 Unity 帧间平滑、短时预测/停止和瞬移恢复 |
| 个人死亡与异常场景 | death | DEATH_SCENE；DEATH_DISCONNECT；DEATH_SEQUENCE；DEATH_NATIVE_ONLY 等 | 不同角色检查点、先死亡/先起身、同椅/不同椅、原版例外死亡与断线；场景参数不代表全部场景已跑 |
| 五门与子动画 | p5-motion | P5_RESCUE_CHECK=1；VISUAL_CHECKS=1 | 局部五门互救/连战推进；副本子动画自然播放、不执行复制来的玩法回调、稀疏移动与接管恢复 |
| FSM 续接 | pantheon | FSM_CONTINUATION_CHECK=1；FSM_CONTINUATION_ONLY | 绝对 tween、固定帧减速、休眠接管；可选 template/combat/entry/hierarchy/snapshot/deceleration |
| 模板 FSM | pantheon | FSM_CONTINUATION_ONLY=template | 两种 Preprocessed 标记的默认占位模板、展开状态表、变量/订阅、首次 Awake、复制后的本机引用 |
| FSM 即时发布与原子入口 | pantheon | FSM_SNAPSHOT_CHECK=1；FSM_ENTRY_CHECK=1 | 原生状态变更即发布、空状态清旧值、原生重激活；父状态与子激活必须完整后才发布 |
| 层级与接管压力 | pantheon | FSM_HIERARCHY_CHECK=1；FSM_CONTINUATION_STRESS=1；COLLIDER_RECONNECT_STRESS=1 | 脱离父体的根和 local-space tween；两种顺序各 8 轮飞针断线/重连、Decel/Return 与自然收针 |
| 静态身份/引用/原生运动 | pantheon | LIFECYCLE_CHECK=1；STATIC_IDENTITY_CHECK=1；PAUSE_MOTION_CHECK=1 | 跨房清理、静态 ID、Needle/Tink 活引用、同房重连、动画与物理运动 |
| iTween 复制安全 | pantheon | TWEEN_CLONE_CHECK=1；TWEEN_CLONE_ONLY=1 | 真实父子 tween 经过生产复制/激活；保留原对象参数，新副本动画能运行 |
| 原生入场/早移交/待加入 | pantheon | NATIVE_ENTRANCE；NATIVE_REENTRY_STRESS；EARLY_TRANSFER；EARLY_DISCONNECT；PENDING_PEER_CHECK；SAME_ROOM_CHECK | 重复尝试、同房进入、早期权威迁移/断线、等待队友与个人返回 |
| 战斗快照 | pantheon / coverage | COMBAT_SNAPSHOT_CHECK=1 / COMBAT_SNAPSHOT_ASSERTIONS=1 | 生产序列化与过滤、重复/倒序/可靠重发/回绕、碰撞体和子激活保持最新、接管 |
| 初始/接管快照与实时包交错 | pantheon / coverage | SCENE_SNAPSHOT_ASSERTIONS=1 | 房间初始和接管快照与实时战斗更新的交错，避免旧快照回退显示/碰撞 |
| FSM null 字符串 | coverage | FSM_NULL_STRING_ASSERTIONS=1 | null→赋值→改变→null 的采样、编解码、迟加入缓存和接管；不证明旧 Mines_11 越界原因 |
| 狭窄平台传送 | coverage | WARP_ASSERTIONS=1；WARP_CROSS_ROOM=1；WARP_UNSUPPORTED=1 | Fungus1_02 / Dream_Nailcollection；实际支撑落点、酸水复位、复位中拒绝 F8/迟到响应、无支撑回退 |
| 鹿角虫旅行传送边界 | coverage | WARP_TRAVEL_ASSERTIONS=1 | 电影场景资格、伪造 presence/错误授权、等待中断清理和双向 F8；未完整运行原生骑乘电影 |
| 梦钉剧情平台 | coverage | DREAM_PLATFORM_ASSERTIONS=1 | Dream_Nailcollection 全场景本地、无网络实体/副本、拒绝迟到注册、原生靠近/离开与真实站立、双方显隐独立 |
| 水晶翻转平台 | coverage | LOCAL_FLIP_ASSERTIONS=1 | Mines_35 的 21 个平台本地运行、真实踩踏翻转/尖刺/自然恢复，另一端不被同步带动 |
| 大盾守卫原生动画 | coverage | ROOT_ANIMATION_ASSERTIONS=1 | Ruins2_04 根动画至少三个不同精灵帧，防止用停动画降低开销 |
| 悬崖地形与井口 | coverage | SCENE_GEOMETRY_TRACE=1；GEOMETRY_GUEST_ALONE=1；CLIFFS_INTERIOR_CHECK=1 | 已加载场景/地形边界、客机单独往返；Fungus1_28 两井真实重力下落、原生开箱和盖板碰撞同步 |
| 环境/粒子分类 | coverage | ENVIRONMENT_ASSERTIONS=1 | 装饰落叶排除、伤害粒子保留、绑定复用、新增命中盒刷新 |
| 真实战斗碰撞与阶段 | coverage | COMBAT_ASSERTIONS=1；NATIVE_CONTACT_ASSERTIONS=1；COVERAGE_SOUL_PHASE2=1 | 真实网络/角色受伤、纯粹容器接触和法术大师/暴君二阶段激活 |
| 帧预算与快照性能 | coverage | COVERAGE_PERFORMANCE=1；COVERAGE_PROFILE=1；PERFORMANCE_ASSERTIONS=1 | P95/P99/最长帧/GC、登记/重建/编码成本、分帧预算、缺预制体候选缓存和真实压缩初始快照 |
| Unity 对象寿命与空闲 CPU | full 内置 | UnityLifetimeChecks | 销毁 Transform 后 Refresh 清缓存、缺少 Host 的层级节点、8 秒空闲 Update 成本与诊断采样频率 |
| 加利安单项 | godhome | GALIEN_ONLY=1 | 迟加入/接管中的 GetOwner 本机引用与原生音效异常 |
| 网络故障注入 | 跨模式 | NETWORK_FAULTS=1 / periodic | 加密前固定种子丢包、乱序、重复；保留实际计数和原断言；periodic 另保留周期性丢包对照 |
| 镜头判定对照 | Run-Camera-Gate.ps1 | CAMERA_GATE_NEGATIVE；CAMERA_HAZARD | 非危险冻结应立即拒绝；真正危险复位被阻塞应在 15 秒拒绝；正常尖刺自行恢复。前两项预期失败是门槛对照 |
| 最终 Boss 尸体/聚焦结局 | final-ending | FINAL_CASE=battle/continuation | 原版音乐/自我刺击/死亡收尾、原生 Spell Control 聚焦、A/B 结局与个人清理；battle 历史整轮失败保留，续接另记专项通过 |
| 只读检查和诊断 | 跨模式 | 含 INSPECT / TRACE / BASELINE 的参数 | 素材/FSM/碰撞体/原始包/原生状态轨迹，以及旧 DLL 预期失败对照；不能单独算玩法通过 |

## 竞技场专项全部 Case（15 种）

入口：[Run-Colosseum.ps1](../tests/Smoke/Run-Colosseum.ps1)。可选 Bronze / Silver / Gold、host / guest 先入、Share 开关。支持参数组合不代表所有组合都已有实测；当前夹具预先解锁试炼，不覆盖首次付费或三档逐波全通。

| Case | 内容 |
| --- | --- |
| `inspect` | 原生对象/FSM 检查 |
| `entry` | 报名和入场 |
| `return` | 个人返回 |
| `wipe` | 双方死亡自动重试 |
| `late` | 迟加入 |
| `bench` | 原生椅子/检查点 |
| `native` | 原生入口与流程 |
| `bounds` | FSM 字符串/快照边界夹具 |
| `bounds-baseline` | 旧引擎越界失败对照 |
| `lifecycle` | 轮流断线接管、重连、换个人椅子、再报名 |
| `flow-inspect` | 只读流程检查 |
| `music-late` | 晚进入的音乐/标题 |
| `music-boundary` | 音乐状态边界 |
| `victory` | 原生胜利流程 |
| `reward-continuation` | 首次护符槽奖励、迟入者补发及重复结算 |

## coverage 默认观察场景（13 个）

这些是默认场景，不表示所有 Boss 招式已经验证。COVERAGE_SCENES 可定向指定其它场景，COVERAGE_AUTHORITY 可交换先入顺序。

`GG_Hornet_1`、`GG_Nailmasters`、`GG_Mantis_Lords`、`GG_Uumuu`、`GG_Ghost_Markoth`、`GG_Collector`、`GG_Hollow_Knight`、`GG_Painter`、`GG_Nosk`、`GG_Ghost_No_Eyes`、`GG_Soul_Tyrant`、`GG_White_Defender`、`GG_Grimm`。

## 日志门槛、安装器与静态/包检查

日志门槛自身有 14 个对照：原生警告、Stun Fail 诊断文本、跨行模组异常、独立 engine 日志 Spawn expiry、回调 ERROR、probe 内 ERROR、Unity crash、Access Violation、MissingReference、异常在空行/新时间戳结束、完成后迟到失败、精确镜头预期失败允许、镜头对照不能掩盖其它异常。

安装器有 13 个检查项：现代/旧版 Steam 库、多个副本去重、手选可执行文件与非法目录、系统 PowerShell 模块隔离、中文/空格/括号/单引号/& 路径与无关 cwd、升级保留原备份/皮肤/配置、校验失败前拒绝、升级失败回滚、首次安装失败回滚、真实文件锁导致的部分恢复报告、卸载恢复最初基线、无需开发工具的自动发现。

最终 Boss 另有 Assert-FinalBossEndingStatic.ps1 源码接线检查；包检查入口 Verify-Release.ps1 验证载荷/版本/证据/依赖/源码 ZIP/排除测试插件与存档。Build.ps1 是包含构建、核心回归和打包的入口，不应在“只列清单”时执行。

## 所有测试/汇总 PowerShell 入口

| 脚本 | 作用 |
| --- | --- |
| [tests/Installer.Tests.ps1](../tests/Installer.Tests.ps1) | 13 项隔离安装器测试；不启动真实游戏 |
| [tests/LogGate.Tests.ps1](../tests/LogGate.Tests.ps1) | 14 项日志门槛自身测试 |
| [tests/Smoke/Assert-FinalBossEndingStatic.ps1](../tests/Smoke/Assert-FinalBossEndingStatic.ps1) | 最终 Boss 尸体/结局接线静态断言，结果不是实机验收 |
| [tests/Smoke/Assert-ProbeLogs.ps1](../tests/Smoke/Assert-ProbeLogs.ps1) | 扫描所有角色/引擎/Unity 日志与迟到失败标记 |
| [tests/Smoke/Complete-Colosseum.ps1](../tests/Smoke/Complete-Colosseum.ps1) | 正常退出、只停止隔离进程、严格日志和 DLL 哈希收尾 |
| [tests/Smoke/Run-Camera-Gate.ps1](../tests/Smoke/Run-Camera-Gate.ps1) | 3 组真实 Unity 镜头判定对照 |
| [tests/Smoke/Run-Colosseum.ps1](../tests/Smoke/Run-Colosseum.ps1) | 竞技场 15 种 Case、三档试炼与两种先入顺序 |
| [tests/Smoke/Run-FinalBossEnding.ps1](../tests/Smoke/Run-FinalBossEnding.ps1) | 最终 Boss battle / continuation 双端 |
| [tests/Smoke/Run-Room-Suite.ps1](../tests/Smoke/Run-Room-Suite.ps1) | 12 组房间套件 |
| [tests/Smoke/Run-Stability-Suite.ps1](../tests/Smoke/Run-Stability-Suite.ps1) | 6 组稳定性套件 |
| [tests/Smoke/Run-Two.ps1](../tests/Smoke/Run-Two.ps1) | 通用双端启动/编译/载荷哈希记录；Mode 选择探针 |
| [tests/Smoke/Summarize-Hierarchy-Diagnostics.ps1](../tests/Smoke/Summarize-Hierarchy-Diagnostics.ps1) | 层级/引用诊断和对应失败证据汇总 |
| [tests/Smoke/Summarize-Network-Reliability.ps1](../tests/Smoke/Summarize-Network-Reliability.ps1) | 既定网络故障证据汇总 |
| [tests/Smoke/Summarize-Room-Suite.ps1](../tests/Smoke/Summarize-Room-Suite.ps1) | 房间套件终态/哈希/日志汇总 |
| [tests/Smoke/Summarize-Selective-Ack-Recovery.ps1](../tests/Smoke/Summarize-Selective-Ack-Recovery.ps1) | 选择 ACK、重复重发、入场恢复证据汇总 |
| [tests/Smoke/Summarize-Stability-Suite.ps1](../tests/Smoke/Summarize-Stability-Suite.ps1) | 汇总 18 组、200 回归和 8 个性能窗口 |

另有 [scripts/Build.ps1](../scripts/Build.ps1)、[scripts/Package.ps1](../scripts/Package.ps1)、[scripts/Verify-Release.ps1](../scripts/Verify-Release.ps1) 构建和交付入口。只读 Boss 静态扫描来自本机 hollow-knight-boss-net-audit 技能，不是运行安装包内的玩法测试。

## 全部核心检查项原名（最近完整运行的 200 项）

以下保持运行器原名，便于精确筛选。每项曾在紧急回椅升版前通过，不重新运行测试。

1. server guest receipt persists without marking the host locally awarded
2. legacy host receipts seed authority history without aliasing local awards
3. resource catalog covers 8 notches, 16 mask shards, 9 vessel fragments and 6 ore sources
4. resource union gives each save one award for simultaneous claims from the same place
5. distinct resource sources survive reordered grants and fragment conversion
6. saved ore receipts prevent reconnect from refunding personally spent ore
7. late entrant receives missing sources while imported native receipts remain spent
8. source receipts reject aggregate balances and malformed persisted claims
9. native reward lookup keeps unrelated scene pickups separate
10. resource adapter preserves only local source receipts and resource nail values
11. slot settings copy sanitizes resource receipts without sharing mutable collections
12. host shared progress and resource receipts survive a slot copy without cross-slot aliases
13. debug event ring is bounded and keeps chronological tail
14. save safety risk model catches a stale transient lock
15. offline pending boss context is treated as a save safety risk
16. diagnostic export is a state summary without identity or save dump
17. outgoing encoding allows next gameplay updates without changing the detached batch
18. failed detached encoding preserves reliable data for a subsequent carrier
19. player position history smooths jittered cadence 0.017
20. player position history smooths jittered cadence 0.05
21. player position history smooths jittered cadence 0.1
22. player forecast is bounded and entities still hold the real endpoint
23. player teleport gap burst and reuse discard prediction history
24. replica positions interpolate rendered frames between sparse authority samples
25. replica positions hold at a stop and never extrapolate into hazards
26. replica teleports and long packet gaps reset stale presentation history
27. replica packet bursts coalesce without duplicate timestamps or unbounded storage
28. death standings ignore duplicate and older reliable reports
29. death standings count downed players once across rescue and formal return
30. death standings survive full-team retry and restore into a new room
31. death count message preserves reliable saved totals and Chinese names
32. copied death histories merge by maximum without doubling or rollback
33. save-specific death data copies isolate slots and missing data starts empty
34. main-thread backlog yields between complete callbacks and preserves FIFO
35. scalar packet encoding preserves BitConverter wire bytes and floating-point bits
36. reused scratch snapshots do not change published packets
37. compressed combat updates preserve independent full snapshots and reject invalid lengths
38. periodic loss delivers both enter directions once: start=0 lostStep=12
39. periodic loss delivers both enter directions once: start=0 lostStep=13
40. periodic loss delivers both enter directions once: start=0 lostStep=90
41. periodic loss delivers both enter directions once: start=0 lostStep=91
42. selective ACK loss evidence tolerates reordering and duplicate reports: 12
43. periodic loss delivers both enter directions once: start=65523 lostStep=12
44. periodic loss delivers both enter directions once: start=65523 lostStep=13
45. periodic loss delivers both enter directions once: start=65523 lostStep=90
46. periodic loss delivers both enter directions once: start=65523 lostStep=91
47. selective ACK loss evidence tolerates reordering and duplicate reports: 65535
48. periodic loss delivers at congested send rate
49. reliable loss retains its timeout fallback without newer ACK evidence
50. nested reliable retransmissions retain origin across sequence 22
51. nested reliable retransmissions retain origin across sequence 1
52. nested addon retransmissions retain origin across sequence wrap
53. consumed retransmission origins and delayed originals execute once
54. consumed origins survive beyond the 64-packet ACK history and sequence wrap
55. consumed addon origins and duplicate carriers execute once
56. delayed datagrams keep newer sparse entity snapshots across sequence 22
57. delayed resends keep newer sparse entity snapshots across sequence 22
58. delayed datagrams keep newer sparse entity snapshots across sequence 1
59. delayed resends keep newer sparse entity snapshots across sequence 1
60. consumed origins allow fresh payloads after a complete ushort sequence wrap
61. delayed combat snapshots preserve newer collider state across sequence 20
62. resent combat snapshots preserve newer collider state across sequence 20
63. delayed combat snapshots preserve newer collider state across sequence 1
64. resent combat snapshots preserve newer collider state across sequence 1
65. client receive and main-thread dispatch preserve reordered fragmented entity updates
66. room snapshot/live order: sequence=20 transfer=False snapshotNewer=False resend=False
67. room snapshot/live order: sequence=20 transfer=False snapshotNewer=False resend=True
68. room snapshot/live order: sequence=20 transfer=False snapshotNewer=True resend=False
69. room snapshot/live order: sequence=20 transfer=False snapshotNewer=True resend=True
70. room snapshot/live order: sequence=20 transfer=True snapshotNewer=False resend=False
71. room snapshot/live order: sequence=20 transfer=True snapshotNewer=False resend=True
72. room snapshot/live order: sequence=20 transfer=True snapshotNewer=True resend=False
73. room snapshot/live order: sequence=20 transfer=True snapshotNewer=True resend=True
74. room snapshot/live order: sequence=0 transfer=False snapshotNewer=False resend=False
75. room snapshot/live order: sequence=0 transfer=False snapshotNewer=False resend=True
76. room snapshot/live order: sequence=0 transfer=False snapshotNewer=True resend=False
77. room snapshot/live order: sequence=0 transfer=False snapshotNewer=True resend=True
78. room snapshot/live order: sequence=0 transfer=True snapshotNewer=False resend=False
79. room snapshot/live order: sequence=0 transfer=True snapshotNewer=False resend=True
80. room snapshot/live order: sequence=0 transfer=True snapshotNewer=True resend=False
81. room snapshot/live order: sequence=0 transfer=True snapshotNewer=True resend=True
82. reliable resend order preserves snapshots and events across sequence 22
83. reliable resend order preserves snapshots and events across sequence 1
84. addon resends preserve chronological collections and newest scalar without mutating sends
85. final entity snapshot is consumed before same-datagram disconnect
86. replica cancellation remains final after a destroyed-target exit failure
87. replica exit still detaches observers when coroutine cancellation fails
88. reentrant replica cleanup cannot run the same exit twice
89. reliable revive identifies the accepted rescuer independently of victim
90. personal checkpoint rejects invalid types and permits native implicit destinations
91. entry protection waits for actual readiness and a reaction interval
92. interrupted arrival restarts grace but combat input loss cannot rearm it
93. wipe freezes a separate respawn destination and type for every member
94. native implicit returns need no invented common bench or marker
95. personal dream return accepts the native gate and does not reset survivors
96. avatar room and both participants' visits survive all scene packets
97. late teammate leave and duplicate enter cannot remove a newer avatar
98. queued snapshots freeze generic data, FSM dictionaries and scale
99. pre-entry movement keeps newest peer visit and independent changed fields
100. pre-entry animation retains visual frame without replaying attack effects
101. wide entity IDs survive position, reliable state and spawn serialization
102. dense boss action batches retain more than 255 ordered updates and following fields
103. oversized entity action batches and component payloads reject partial serialization
104. same-type spawn sources retain exact owner, action, prefab and origin
105. malformed spawn descriptors cannot enter a deferred queue
106. static identity is stable when unrelated entities are omitted or reordered
107. pooled spawn lifetimes cannot reuse a static or earlier dynamic identity
108. server entity key equality retains high bits and scene scope
109. only a newer explicit health reset opens a death tombstone
110. object-reference snapshots preserve clearing, state and the eighth flag bit
111. retired projectile snapshots keep only dependencies needed by a live child
112. spawn dependency cycles are bounded during late join
113. Hall of Gods tier, return gate and authoritative itinerary survive addon messages
114. invalid challenge tier and oversized itinerary are rejected
115. new game's early hero event waits for matching scene preparation
116. ordinary transitions publish after preparation and distinguish reentry
117. old scene requests cannot publish into a different scene
118. disconnect discards pending and prepared scene entry
119. Mantis Claw is idempotent
120. charm sharing defaults to existing behavior
121. charm sharing toggle leaves other permanent progress available
122. abilities never regress
123. dung defender encounter flags are shared and monotonic
124. spells take maximum, not sum
125. invalid progress is rejected
126. maps, currency, health and equipment are private
127. all progression keys exist in this game build
128. progress union survives duplicates and random ordering
129. late heartbeat cannot revive downed guest
130. guest down then host down creates one wipe
131. host down then guest down creates one wipe
132. new attempt rejects stale down and presence
133. retry barrier waits for everyone
134. disconnect releases a pending retry barrier
135. late join participates in retry barrier
136. personal retry destination is stable when another player disconnects
137. loading player prevents false team wipe
138. rescue validates distance and death episode
139. rescue validates scene, life and freshness
140. rescue cannot happen twice
141. old down event cannot undo successful rescue
142. reliable death anchor overrides last heartbeat and cannot drift
143. second death event cannot move an existing shade
144. shade hit cannot revive another death episode
145. self hits and reset-round hits never revive
146. death anchor rejects nonfinite positions and invalid scene
147. give up uses personal bench without resetting the surviving teammate
148. alive players and missing checkpoints cannot give up
149. duplicate give up and new down events cannot interrupt returning
150. unsupported accepted rescue can return under a fresh death serial
151. rescue and give up accept only the first successful action
152. old death and round cannot request or finish a bench return
153. bench destination stays frozen during return
154. bench arrival requires the authorized scene and finite position
155. late old-room heartbeat cannot overwrite a completed bench return
156. later bench heartbeat arriving before acknowledgement does not block arrival
157. returning teammate prevents a false full-team failure
158. give up cannot bypass full-team failure
159. out-of-order position is ignored
160. map coordinates remain available while teleport input is unavailable
161. map center and edges use actual room geometry
162. custom map origin offset is respected
163. invalid map dimensions do not produce stale positions
164. offscreen diagonal arrow stays within viewport
165. message roundtrip preserves Chinese names and life version
166. events require reliable non-dropping delivery
167. warp roundtrip preserves pantheon runtime context
168. malformed pantheon context is rejected before use
169. native boss completion is reliable and retained in room snapshots
170. HKMP addon collection decodes all messages using the live receiver factory
171. malformed protocol message is rejected
172. entity position preserves room visit and attempt
173. entity spawn preserves scene scope
174. authority transfer carries health and terminal death snapshot
175. server health cache rejects stale values
176. terminal death is an immutable tombstone
177. server health snapshot converges under random reordering
178. five simultaneous real DTLS clients remain isolated and listener restarts
179. stopping after a peer closes DTLS permits reopening the same server port
180. DTLS close retires a peer after its final snapshot without waiting for heartbeat timeout
181. failed bind can be stopped safely and retried on the same port
182. maximum-size fragmented datagram survives reverse order and duplicates
183. boss action packets larger than 64 KiB survive framing and live update decoding
184. lost fragment does not consume a later heartbeat
185. five simultaneous fragmented peers with equal message IDs remain isolated
186. fragment expiry and bounded pending messages allow later heartbeats
187. malformed or truncated datagrams cannot corrupt the following packet
188. five real DTLS peers keep heartbeats after interleaved reordered and lost fragments
189. stopped chunk worker and queued old chunks cannot leak into a new session
190. acknowledged chunk flags are reset on reconnect and completion cannot spin
191. one received datagram commits parent, child and addon updates in the same Unity frame
192. queued callbacks from a retired client session never execute
193. initial player messages wait for queued game setup and cannot survive disconnect
194. server rejection fully releases the client DTLS session
195. future chunk fragments cannot contaminate the active chunk
196. rapid handshake cancellation, rejection and old timeouts cannot affect a new DTLS session
197. room shutdown cancels receiving before waiting for the final outgoing update
198. active UDP receive failure remains an error and cannot fabricate a received datagram
199. disconnect or reconnect inside a completed chunk callback preserves the new receiver
200. malformed slice bounds and lengths cannot corrupt a following valid chunk

## 测试源码内全部环境变量索引

以下按源码读取点提取，包含专项开关、参数、诊断与基础运行变量；同名变量可以在不同模式有不同作用，不能全设为 1。不是专项数量统计。

| 变量 | 读取源码 |
| --- | --- |
| `HOLLOWCONNECT_CAMERA_GATE_NEGATIVE` | [PantheonProbe.cs](../tests/Smoke/PantheonProbe.cs) |
| `HOLLOWCONNECT_CAMERA_HAZARD` | [PantheonProbe.cs](../tests/Smoke/PantheonProbe.cs) |
| `HOLLOWCONNECT_CAMERA_RING` | [PantheonProbe.cs](../tests/Smoke/PantheonProbe.cs) |
| `HOLLOWCONNECT_CAMERA_TRACE` | [PantheonProbe.cs](../tests/Smoke/PantheonProbe.cs) |
| `HOLLOWCONNECT_CHARM_SHARE` | [ProgressProbe.cs](../tests/Smoke/ProgressProbe.cs), [ProgressTeammateDream.cs](../tests/Smoke/ProgressTeammateDream.cs) |
| `HOLLOWCONNECT_CLIFFS_INTERIOR_CHECK` | [CoverageProbe.cs](../tests/Smoke/CoverageProbe.cs) |
| `HOLLOWCONNECT_COLLIDER_RECONNECT_STRESS` | [PantheonProbe.cs](../tests/Smoke/PantheonProbe.cs) |
| `HOLLOWCONNECT_COLLIDER_TRACE` | [PantheonProbe.cs](../tests/Smoke/PantheonProbe.cs) |
| `HOLLOWCONNECT_COLOSSEUM_CASE` | [ColosseumProbe.cs](../tests/Smoke/ColosseumProbe.cs) |
| `HOLLOWCONNECT_COLOSSEUM_FIRST` | [ColosseumProbe.cs](../tests/Smoke/ColosseumProbe.cs) |
| `HOLLOWCONNECT_COLOSSEUM_SHARE` | [ColosseumProbe.cs](../tests/Smoke/ColosseumProbe.cs) |
| `HOLLOWCONNECT_COLOSSEUM_TRIAL` | [ColosseumProbe.cs](../tests/Smoke/ColosseumProbe.cs) |
| `HOLLOWCONNECT_COMBAT_ASSERTIONS` | [CoverageProbe.cs](../tests/Smoke/CoverageProbe.cs) |
| `HOLLOWCONNECT_COMBAT_SNAPSHOT_ASSERTIONS` | [CoverageProbe.cs](../tests/Smoke/CoverageProbe.cs) |
| `HOLLOWCONNECT_COMBAT_SNAPSHOT_CHECK` | [FsmContinuationChecks.cs](../tests/Smoke/FsmContinuationChecks.cs) |
| `HOLLOWCONNECT_CONTINUATION_PACKET_TRACE` | [PantheonProbe.cs](../tests/Smoke/PantheonProbe.cs) |
| `HOLLOWCONNECT_CONTINUATION_TRACE` | [PantheonProbe.cs](../tests/Smoke/PantheonProbe.cs) |
| `HOLLOWCONNECT_COVERAGE_AUTHORITY` | [CoverageProbe.cs](../tests/Smoke/CoverageProbe.cs) |
| `HOLLOWCONNECT_COVERAGE_ENTRY_GATE` | [CoverageProbe.cs](../tests/Smoke/CoverageProbe.cs) |
| `HOLLOWCONNECT_COVERAGE_NEAR_TARGET` | [CoverageProbe.cs](../tests/Smoke/CoverageProbe.cs) |
| `HOLLOWCONNECT_COVERAGE_PERFORMANCE` | [CoverageProbe.cs](../tests/Smoke/CoverageProbe.cs) |
| `HOLLOWCONNECT_COVERAGE_PROFILE` | [CoverageProbe.cs](../tests/Smoke/CoverageProbe.cs) |
| `HOLLOWCONNECT_COVERAGE_SCENES` | [CoverageProbe.cs](../tests/Smoke/CoverageProbe.cs) |
| `HOLLOWCONNECT_COVERAGE_SECONDS` | [CoverageProbe.cs](../tests/Smoke/CoverageProbe.cs) |
| `HOLLOWCONNECT_COVERAGE_SOUL_PHASE2` | [CoverageProbe.cs](../tests/Smoke/CoverageProbe.cs) |
| `HOLLOWCONNECT_DEATH_CAMERA_CHECK` | [RescueProbe.cs](../tests/Smoke/RescueProbe.cs) |
| `HOLLOWCONNECT_DEATH_DISCONNECT` | [DeathProbe.cs](../tests/Smoke/DeathProbe.cs) |
| `HOLLOWCONNECT_DEATH_FIRST` | [DeathProbe.cs](../tests/Smoke/DeathProbe.cs) |
| `HOLLOWCONNECT_DEATH_INPUT_CHECK` | [DeathProbe.cs](../tests/Smoke/DeathProbe.cs) |
| `HOLLOWCONNECT_DEATH_NATIVE_ONLY` | [DeathProbe.cs](../tests/Smoke/DeathProbe.cs) |
| `HOLLOWCONNECT_DEATH_SCENE` | [DeathProbe.cs](../tests/Smoke/DeathProbe.cs), [RescueEdgeChecks.cs](../tests/Smoke/RescueEdgeChecks.cs) |
| `HOLLOWCONNECT_DEATH_SEQUENCE` | [DeathProbe.cs](../tests/Smoke/DeathProbe.cs) |
| `HOLLOWCONNECT_DEATH_SHARED_BENCH` | [DeathProbe.cs](../tests/Smoke/DeathProbe.cs) |
| `HOLLOWCONNECT_DEATH_STAND_FIRST` | [DeathProbe.cs](../tests/Smoke/DeathProbe.cs) |
| `HOLLOWCONNECT_DEFENDER_CHECK` | [ProgressProbe.cs](../tests/Smoke/ProgressProbe.cs) |
| `HOLLOWCONNECT_DEFENDER_ROUTE_INSPECT` | [ProgressDefender.cs](../tests/Smoke/ProgressDefender.cs), [ProgressProbe.cs](../tests/Smoke/ProgressProbe.cs) |
| `HOLLOWCONNECT_DREAM_PLATFORM_ASSERTIONS` | [CoverageProbe.cs](../tests/Smoke/CoverageProbe.cs) |
| `HOLLOWCONNECT_DREAM_SUMMONER` | [ProgressDreamWarriors.cs](../tests/Smoke/ProgressDreamWarriors.cs) |
| `HOLLOWCONNECT_DREAM_WARRIORS` | [ProgressDreamWarriors.cs](../tests/Smoke/ProgressDreamWarriors.cs), [ProgressProbe.cs](../tests/Smoke/ProgressProbe.cs) |
| `HOLLOWCONNECT_EARLY_DISCONNECT` | [PantheonProbe.cs](../tests/Smoke/PantheonProbe.cs) |
| `HOLLOWCONNECT_EARLY_TRANSFER` | [PantheonProbe.cs](../tests/Smoke/PantheonProbe.cs) |
| `HOLLOWCONNECT_EMERGENCY_BENCH` | [ProgressProbe.cs](../tests/Smoke/ProgressProbe.cs) |
| `HOLLOWCONNECT_ENVIRONMENT_ASSERTIONS` | [CoverageProbe.cs](../tests/Smoke/CoverageProbe.cs) |
| `HOLLOWCONNECT_ENVIRONMENT_INSPECT` | [CoverageProbe.cs](../tests/Smoke/CoverageProbe.cs) |
| `HOLLOWCONNECT_FINAL_CASE` | [FinalBossEndingProbe.cs](../tests/Smoke/FinalBossEndingProbe.cs) |
| `HOLLOWCONNECT_FSM_CONTINUATION_CHECK` | [PantheonProbe.cs](../tests/Smoke/PantheonProbe.cs) |
| `HOLLOWCONNECT_FSM_CONTINUATION_ONLY` | [FsmContinuationChecks.cs](../tests/Smoke/FsmContinuationChecks.cs), [PantheonProbe.cs](../tests/Smoke/PantheonProbe.cs) |
| `HOLLOWCONNECT_FSM_CONTINUATION_STRESS` | [PantheonProbe.cs](../tests/Smoke/PantheonProbe.cs) |
| `HOLLOWCONNECT_FSM_ENTRY_CHECK` | [FsmContinuationChecks.cs](../tests/Smoke/FsmContinuationChecks.cs) |
| `HOLLOWCONNECT_FSM_HIERARCHY_CHECK` | [FsmContinuationChecks.cs](../tests/Smoke/FsmContinuationChecks.cs), [PantheonProbe.cs](../tests/Smoke/PantheonProbe.cs) |
| `HOLLOWCONNECT_FSM_LAYOUT_TRACE` | [CoverageProbe.cs](../tests/Smoke/CoverageProbe.cs) |
| `HOLLOWCONNECT_FSM_NULL_STRING_ASSERTIONS` | [CoverageProbe.cs](../tests/Smoke/CoverageProbe.cs) |
| `HOLLOWCONNECT_FSM_SCHEMA_TRACE` | [FsmSchemaTrace.cs](../tests/Smoke/FsmSchemaTrace.cs) |
| `HOLLOWCONNECT_FSM_SNAPSHOT_CHECK` | [FsmContinuationChecks.cs](../tests/Smoke/FsmContinuationChecks.cs) |
| `HOLLOWCONNECT_GALIEN_ONLY` | [GodhomeProbe.cs](../tests/Smoke/GodhomeProbe.cs) |
| `HOLLOWCONNECT_GEOMETRY_GUEST_ALONE` | [CoverageProbe.cs](../tests/Smoke/CoverageProbe.cs), [CoverageSceneGeometry.cs](../tests/Smoke/CoverageSceneGeometry.cs) |
| `HOLLOWCONNECT_GODHOME_AUTHORITY` | [GodhomeProbe.cs](../tests/Smoke/GodhomeProbe.cs) |
| `HOLLOWCONNECT_GODHOME_FAULT_ASSERTIONS` | [GodhomeProbe.cs](../tests/Smoke/GodhomeProbe.cs) |
| `HOLLOWCONNECT_GRIMM_AUTHORITY` | [GrimmMotionProbe.cs](../tests/Smoke/GrimmMotionProbe.cs) |
| `HOLLOWCONNECT_GRIMM_ENTRY_DELAY` | [GrimmMotionProbe.cs](../tests/Smoke/GrimmMotionProbe.cs) |
| `HOLLOWCONNECT_GRIMM_HANDOFF` | [GrimmMotionProbe.cs](../tests/Smoke/GrimmMotionProbe.cs) |
| `HOLLOWCONNECT_GRIMM_MENU_BASELINE` | [GrimmMotionProbe.cs](../tests/Smoke/GrimmMotionProbe.cs) |
| `HOLLOWCONNECT_GRIMM_SCENE` | [GrimmMotionProbe.cs](../tests/Smoke/GrimmMotionProbe.cs) |
| `HOLLOWCONNECT_HAZARD_RESCUE_CHECK` | [RescueProbe.cs](../tests/Smoke/RescueProbe.cs) |
| `HOLLOWCONNECT_LIFECYCLE_CHECK` | [PantheonProbe.cs](../tests/Smoke/PantheonProbe.cs) |
| `HOLLOWCONNECT_LOCAL_FLIP_ASSERTIONS` | [CoverageProbe.cs](../tests/Smoke/CoverageProbe.cs) |
| `HOLLOWCONNECT_MAP_BOTH` | [ProgressProbe.cs](../tests/Smoke/ProgressProbe.cs) |
| `HOLLOWCONNECT_NATIVE_ATTEMPTS` | [PantheonProbe.cs](../tests/Smoke/PantheonProbe.cs) |
| `HOLLOWCONNECT_NATIVE_CONTACT_ASSERTIONS` | [CoverageProbe.cs](../tests/Smoke/CoverageProbe.cs) |
| `HOLLOWCONNECT_NATIVE_ENTRANCE` | [PantheonProbe.cs](../tests/Smoke/PantheonProbe.cs) |
| `HOLLOWCONNECT_NATIVE_REENTRY_STRESS` | [PantheonProbe.cs](../tests/Smoke/PantheonProbe.cs) |
| `HOLLOWCONNECT_NETWORK_FAULTS` | [ProbeNetworkFaults.cs](../tests/Smoke/ProbeNetworkFaults.cs) |
| `HOLLOWCONNECT_NETWORK_TRACE` | [ProbePresenceTrace.cs](../tests/Smoke/ProbePresenceTrace.cs) |
| `HOLLOWCONNECT_P5_RESCUE_CHECK` | [P5MotionProbe.cs](../tests/Smoke/P5MotionProbe.cs) |
| `HOLLOWCONNECT_PANTHEON_AUTHORITY` | [PantheonProbe.cs](../tests/Smoke/PantheonProbe.cs) |
| `HOLLOWCONNECT_PAUSE_MOTION_CHECK` | [PantheonProbe.cs](../tests/Smoke/PantheonProbe.cs) |
| `HOLLOWCONNECT_PAUSE_MOTION_ONLY` | [PantheonProbe.cs](../tests/Smoke/PantheonProbe.cs) |
| `HOLLOWCONNECT_PENDING_PEER_CHECK` | [PantheonProbe.cs](../tests/Smoke/PantheonProbe.cs) |
| `HOLLOWCONNECT_PERFORMANCE_ASSERTIONS` | [CoverageProbe.cs](../tests/Smoke/CoverageProbe.cs) |
| `HOLLOWCONNECT_PERFORMANCE_CHECK` | [CoverageProbe.cs](../tests/Smoke/CoverageProbe.cs) |
| `HOLLOWCONNECT_PLAYER_MOTION_CHECK` | [RescueProbe.cs](../tests/Smoke/RescueProbe.cs) |
| `HOLLOWCONNECT_PROGRESS_BASELINE` | [ProgressProbe.cs](../tests/Smoke/ProgressProbe.cs) |
| `HOLLOWCONNECT_PROGRESS_BUYER` | [ProgressProbe.cs](../tests/Smoke/ProgressProbe.cs) |
| `HOLLOWCONNECT_PROGRESS_INSPECT` | [ProgressProbe.cs](../tests/Smoke/ProgressProbe.cs) |
| `HOLLOWCONNECT_PROGRESS_PROMPT` | [ProgressProbe.cs](../tests/Smoke/ProgressProbe.cs) |
| `HOLLOWCONNECT_REFERENCE_BASELINE` | [PantheonProbe.cs](../tests/Smoke/PantheonProbe.cs) |
| `HOLLOWCONNECT_RESCUE_EDGE_CHECK` | [RescueEdgeChecks.cs](../tests/Smoke/RescueEdgeChecks.cs), [RescueProbe.cs](../tests/Smoke/RescueProbe.cs) |
| `HOLLOWCONNECT_RESCUE_NATIVE_CHECK` | [RescueNativeChecks.cs](../tests/Smoke/RescueNativeChecks.cs), [RescueProbe.cs](../tests/Smoke/RescueProbe.cs) |
| `HOLLOWCONNECT_RESOURCE_CHECK` | [ProgressProbe.cs](../tests/Smoke/ProgressProbe.cs) |
| `HOLLOWCONNECT_RESOURCE_CONTINUATION` | [ProgressResources.cs](../tests/Smoke/ProgressResources.cs) |
| `HOLLOWCONNECT_ROOT_ANIMATION_ASSERTIONS` | [CoverageProbe.cs](../tests/Smoke/CoverageProbe.cs) |
| `HOLLOWCONNECT_SAME_ROOM_CHECK` | [PantheonProbe.cs](../tests/Smoke/PantheonProbe.cs) |
| `HOLLOWCONNECT_SCENE_GEOMETRY_TRACE` | [CoverageProbe.cs](../tests/Smoke/CoverageProbe.cs) |
| `HOLLOWCONNECT_SCENE_SNAPSHOT_ASSERTIONS` | [FsmContinuationChecks.cs](../tests/Smoke/FsmContinuationChecks.cs) |
| `HOLLOWCONNECT_SMOKE_BOSS_CONTROL` | [BossProbe.cs](../tests/Smoke/BossProbe.cs) |
| `HOLLOWCONNECT_SMOKE_BOSS_LATE` | [BossProbe.cs](../tests/Smoke/BossProbe.cs) |
| `HOLLOWCONNECT_SMOKE_DAMAGE_STATE` | [BossProbe.cs](../tests/Smoke/BossProbe.cs) |
| `HOLLOWCONNECT_SMOKE_DAMAGE_TRACE` | [BossProbe.cs](../tests/Smoke/BossProbe.cs) |
| `HOLLOWCONNECT_SMOKE_HANDOFF_DELAY` | [BossProbe.cs](../tests/Smoke/BossProbe.cs) |
| `HOLLOWCONNECT_SMOKE_MODE` | [Probe.cs](../tests/Smoke/Probe.cs) |
| `HOLLOWCONNECT_SMOKE_ROLE` | [BossProbe.cs](../tests/Smoke/BossProbe.cs), [ColosseumProbe.cs](../tests/Smoke/ColosseumProbe.cs), [CoverageProbe.cs](../tests/Smoke/CoverageProbe.cs), [DeathProbe.cs](../tests/Smoke/DeathProbe.cs), [FinalBossEndingProbe.cs](../tests/Smoke/FinalBossEndingProbe.cs), [FsmSchemaTrace.cs](../tests/Smoke/FsmSchemaTrace.cs), [GodhomeProbe.cs](../tests/Smoke/GodhomeProbe.cs), [GrimmMotionProbe.cs](../tests/Smoke/GrimmMotionProbe.cs), [JoinProbe.cs](../tests/Smoke/JoinProbe.cs), [P5MotionProbe.cs](../tests/Smoke/P5MotionProbe.cs), [PantheonProbe.cs](../tests/Smoke/PantheonProbe.cs), [Probe.cs](../tests/Smoke/Probe.cs), [ProbePresenceTrace.cs](../tests/Smoke/ProbePresenceTrace.cs), [ProgressProbe.cs](../tests/Smoke/ProgressProbe.cs), [RescueProbe.cs](../tests/Smoke/RescueProbe.cs), [ShadeInspectProbe.cs](../tests/Smoke/ShadeInspectProbe.cs) |
| `HOLLOWCONNECT_SMOKE_ROOT` | [BossProbe.cs](../tests/Smoke/BossProbe.cs), [ColosseumProbe.cs](../tests/Smoke/ColosseumProbe.cs), [CoverageProbe.cs](../tests/Smoke/CoverageProbe.cs), [DeathProbe.cs](../tests/Smoke/DeathProbe.cs), [FinalBossEndingProbe.cs](../tests/Smoke/FinalBossEndingProbe.cs), [FsmSchemaTrace.cs](../tests/Smoke/FsmSchemaTrace.cs), [GodhomeProbe.cs](../tests/Smoke/GodhomeProbe.cs), [GrimmMotionProbe.cs](../tests/Smoke/GrimmMotionProbe.cs), [JoinProbe.cs](../tests/Smoke/JoinProbe.cs), [P5MotionProbe.cs](../tests/Smoke/P5MotionProbe.cs), [PantheonProbe.cs](../tests/Smoke/PantheonProbe.cs), [Probe.cs](../tests/Smoke/Probe.cs), [ProbePresenceTrace.cs](../tests/Smoke/ProbePresenceTrace.cs), [ProgressProbe.cs](../tests/Smoke/ProgressProbe.cs), [RescueProbe.cs](../tests/Smoke/RescueProbe.cs), [ShadeInspectProbe.cs](../tests/Smoke/ShadeInspectProbe.cs) |
| `HOLLOWCONNECT_SMOKE_TAKEOVER_TRACE` | [BossProbe.cs](../tests/Smoke/BossProbe.cs) |
| `HOLLOWCONNECT_STATIC_IDENTITY_CHECK` | [PantheonProbe.cs](../tests/Smoke/PantheonProbe.cs) |
| `HOLLOWCONNECT_STOP_AFTER_PENDING` | [PantheonProbe.cs](../tests/Smoke/PantheonProbe.cs) |
| `HOLLOWCONNECT_TEAMMATE_DREAM` | [ProgressProbe.cs](../tests/Smoke/ProgressProbe.cs) |
| `HOLLOWCONNECT_TEST_SELECTION` | [Program.cs](../tests/Program.cs) |
| `HOLLOWCONNECT_TWEEN_CLONE_CHECK` | [PantheonProbe.cs](../tests/Smoke/PantheonProbe.cs) |
| `HOLLOWCONNECT_TWEEN_CLONE_ONLY` | [PantheonProbe.cs](../tests/Smoke/PantheonProbe.cs) |
| `HOLLOWCONNECT_VISUAL_CHECKS` | [P5MotionProbe.cs](../tests/Smoke/P5MotionProbe.cs) |
| `HOLLOWCONNECT_WARP_ASSERTIONS` | [CoverageProbe.cs](../tests/Smoke/CoverageProbe.cs) |
| `HOLLOWCONNECT_WARP_CROSS_ROOM` | [CoverageWarpChecks.cs](../tests/Smoke/CoverageWarpChecks.cs) |
| `HOLLOWCONNECT_WARP_DIAGNOSTIC` | [CoverageWarpChecks.cs](../tests/Smoke/CoverageWarpChecks.cs) |
| `HOLLOWCONNECT_WARP_INSPECT` | [CoverageProbe.cs](../tests/Smoke/CoverageProbe.cs) |
| `HOLLOWCONNECT_WARP_TRAVEL_ASSERTIONS` | [CoverageProbe.cs](../tests/Smoke/CoverageProbe.cs) |
| `HOLLOWCONNECT_WARP_UNSUPPORTED` | [CoverageWarpChecks.cs](../tests/Smoke/CoverageWarpChecks.cs) |

## 当前测试源文件索引

此表用于找到未单独列为模式的共享夹具和网络辅助测试。`.draft` 明确为草稿，不计已编译/可运行测试。

- [tests/ChunkLifecycleProbe.cs](../tests/ChunkLifecycleProbe.cs)
- [tests/ChunkReceiverProbe.cs](../tests/ChunkReceiverProbe.cs)
- [tests/ClientSessionProbe.cs](../tests/ClientSessionProbe.cs)
- [tests/DatagramProbe.cs](../tests/DatagramProbe.cs)
- [tests/DebugDiagnosticsTests.cs](../tests/DebugDiagnosticsTests.cs)
- [tests/NetworkLifecycleProbe.cs](../tests/NetworkLifecycleProbe.cs)
- [tests/NetworkTrafficProbe.cs](../tests/NetworkTrafficProbe.cs)
- [tests/PacketEncodingProbe.cs](../tests/PacketEncodingProbe.cs)
- [tests/Program.cs](../tests/Program.cs)
- [tests/ReliabilityProbe.cs](../tests/ReliabilityProbe.cs)
- [tests/ResourceClaimsTests.cs](../tests/ResourceClaimsTests.cs)
- [tests/SceneSnapshotProbe.cs](../tests/SceneSnapshotProbe.cs)
- [tests/SelectiveAckProbe.cs](../tests/SelectiveAckProbe.cs)
- [tests/Smoke/BossProbe.cs](../tests/Smoke/BossProbe.cs)
- [tests/Smoke/ColosseumProbe.cs](../tests/Smoke/ColosseumProbe.cs)
- [tests/Smoke/CoverageArenaFlowChecks.cs](../tests/Smoke/CoverageArenaFlowChecks.cs)
- [tests/Smoke/CoverageDeathCameraChecks.cs](../tests/Smoke/CoverageDeathCameraChecks.cs)
- [tests/Smoke/CoverageDreamPlatforms.cs](../tests/Smoke/CoverageDreamPlatforms.cs)
- [tests/Smoke/CoverageEnvironmentChecks.cs](../tests/Smoke/CoverageEnvironmentChecks.cs)
- [tests/Smoke/CoverageFinalBossEndingChecks.cs](../tests/Smoke/CoverageFinalBossEndingChecks.cs)
- [tests/Smoke/CoverageFinalBossEndingChecks.cs.draft](../tests/Smoke/CoverageFinalBossEndingChecks.cs.draft)（草稿，不计可执行）
- [tests/Smoke/CoverageFsmLayouts.cs](../tests/Smoke/CoverageFsmLayouts.cs)
- [tests/Smoke/CoverageLocalPlatforms.cs](../tests/Smoke/CoverageLocalPlatforms.cs)
- [tests/Smoke/CoveragePerformanceChecks.cs](../tests/Smoke/CoveragePerformanceChecks.cs)
- [tests/Smoke/CoveragePlayerMotionChecks.cs](../tests/Smoke/CoveragePlayerMotionChecks.cs)
- [tests/Smoke/CoverageProbe.cs](../tests/Smoke/CoverageProbe.cs)
- [tests/Smoke/CoverageRadianceChecks.cs](../tests/Smoke/CoverageRadianceChecks.cs)：`HOLLOWCONNECT_RADIANCE_WARP_ASSERTIONS` / `HOLLOWCONNECT_RADIANCE_PHASE_ASSERTIONS`，原生阶段、双端坠落复位和跨房迟入；实际结果见 [辐光修复记录](RADIANCE-BOSS-FIX-2026-10-18.md)。
- [tests/Smoke/CoverageSceneGeometry.cs](../tests/Smoke/CoverageSceneGeometry.cs)
- [tests/Smoke/CoverageWarpChecks.cs](../tests/Smoke/CoverageWarpChecks.cs)
- [tests/Smoke/CoverageWarpTravelChecks.cs](../tests/Smoke/CoverageWarpTravelChecks.cs)
- [tests/Smoke/DeathProbe.cs](../tests/Smoke/DeathProbe.cs)
- [tests/Smoke/EmergencyBenchChecks.cs](../tests/Smoke/EmergencyBenchChecks.cs)
- [tests/Smoke/FinalBossEndingProbe.cs](../tests/Smoke/FinalBossEndingProbe.cs)
- [tests/Smoke/FsmContinuationChecks.cs](../tests/Smoke/FsmContinuationChecks.cs)
- [tests/Smoke/FsmSchemaTrace.cs](../tests/Smoke/FsmSchemaTrace.cs)
- [tests/Smoke/FsmTemplateChecks.cs](../tests/Smoke/FsmTemplateChecks.cs)
- [tests/Smoke/GodhomeProbe.cs](../tests/Smoke/GodhomeProbe.cs)
- [tests/Smoke/GrimmMotionProbe.cs](../tests/Smoke/GrimmMotionProbe.cs)
- [tests/Smoke/JoinProbe.cs](../tests/Smoke/JoinProbe.cs)
- [tests/Smoke/P5MotionProbe.cs](../tests/Smoke/P5MotionProbe.cs)
- [tests/Smoke/PantheonProbe.cs](../tests/Smoke/PantheonProbe.cs)
- [tests/Smoke/Probe.cs](../tests/Smoke/Probe.cs)
- [tests/Smoke/ProbeLogGuard.cs](../tests/Smoke/ProbeLogGuard.cs)
- [tests/Smoke/ProbeNetworkFaults.cs](../tests/Smoke/ProbeNetworkFaults.cs)
- [tests/Smoke/ProbePresenceTrace.cs](../tests/Smoke/ProbePresenceTrace.cs)
- [tests/Smoke/ProbeShutdown.cs](../tests/Smoke/ProbeShutdown.cs)
- [tests/Smoke/ProgressDefender.cs](../tests/Smoke/ProgressDefender.cs)
- [tests/Smoke/ProgressDreamWarriors.cs](../tests/Smoke/ProgressDreamWarriors.cs)
- [tests/Smoke/ProgressProbe.cs](../tests/Smoke/ProgressProbe.cs)
- [tests/Smoke/ProgressResources.cs](../tests/Smoke/ProgressResources.cs)
- [tests/Smoke/ProgressTeammateDream.cs](../tests/Smoke/ProgressTeammateDream.cs)
- [tests/Smoke/ReplicaVisualChecks.cs](../tests/Smoke/ReplicaVisualChecks.cs)
- [tests/Smoke/RescueDeathCameraChecks.cs](../tests/Smoke/RescueDeathCameraChecks.cs)
- [tests/Smoke/RescueEdgeChecks.cs](../tests/Smoke/RescueEdgeChecks.cs)
- [tests/Smoke/RescueHazardChecks.cs](../tests/Smoke/RescueHazardChecks.cs)
- [tests/Smoke/RescueNativeChecks.cs](../tests/Smoke/RescueNativeChecks.cs)
- [tests/Smoke/RescueProbe.cs](../tests/Smoke/RescueProbe.cs)
- [tests/Smoke/ShadeInspectProbe.cs](../tests/Smoke/ShadeInspectProbe.cs)
- [tests/Smoke/UnityLifetimeChecks.cs](../tests/Smoke/UnityLifetimeChecks.cs)
- [tests/TransportProbe.cs](../tests/TransportProbe.cs)
- [tests/UdpSendContentionTests.cs](../tests/UdpSendContentionTests.cs)

## 2026-10-19 本地关门战风险补测

2026-10-19 本地关门战修复另有 [11 组双端风险补测](LOCAL-BATTLE-RISKS-2026-10-19.md)，覆盖重置接收边界、离房重入、断线接管、假骑士阶段、格鲁兹之母及青铜竞技场原版 28 波；每组均通过严格日志并记录相同生产 DLL 哈希。它补充本轮修改的相关覆盖，不改变下面全游戏／真实双机等范围限制。

## 当前仍不在已验证覆盖内

- 真实保存后退出／重读仍待自动化验证；回椅专项拦截磁盘写入。用户已确认快捷键救回真实坏档，此项为 [用户实档 PASS](evidence/user-dead-save-recovery-2026-10-18.json)。
- 真实两台机器、用户 FRP 网络及目标分辨率/144 FPS 长时间压力。
- 游戏全部 Boss 全招式、三档竞技场逐波通关、完整五门和完整剧情长流程。
- 尚未逐项运行的资源来源/参数组合；没有自动最近 100 项选择器。
- 原因未知的历史问题不会因后续通过自动关闭，见 [当前账本](evidence/runtime-blockers-0.1.22.json)。

## 辐光平台补充专项

**用户在 0.1.23 仍复现缺平台。** 以下旧六组未实际渲染镜头，也未覆盖宣战前迟入，不能证明该问题已解决。0.1.24 起套件默认执行实际镜头渲染和逐平台承重检查，增加 `-ArrivalStage before-challenge` 的四组普通辐光入场检查。原因及失败证据见 [渲染回调复查](RADIANCE-RENDER-CALLBACK-AUDIT-2026-10-18.md)，新结果见 [0.1.24 更新说明](UPDATE-0.1.24.md)。

[Run-Radiance-Arena-Suite.ps1](../tests/Smoke/Run-Radiance-Arena-Suite.ps1) 运行雕像／五门无上辐光和普通剧情辐光各两种迟入方式，含双方先入顺序及一组固定种子网络故障。
[CoverageRadianceArenaChecks.cs](../tests/Smoke/CoverageRadianceArenaChecks.cs) 对照完整平台集合、阶段锁区、爬升台阶激活时间，并验证低／中／高台阶承重、控制和 viewport。相关核心回归新增本轮记录为 30 passed / 0 failed。
具体运行、哈希及未覆盖边界见 [平台修复记录](RADIANCE-PLATFORM-FIX-2026-10-18.md) 和 [机器证据](evidence/radiance-platform-runtime-2026-10-18.json)。不把上次辐光包的三组通过扩写为本轮平台专项通过。

0.1.24 追加的十组实际渲染检查已通过，覆盖宣战前三座平台和全部二阶段／爬升平台；每一座均以真实重力逐项检查承重。测试实现另见 [RadianceEntryVisualChecks.cs](../tests/Smoke/RadianceEntryVisualChecks.cs)，最终构建结果见 [0.1.24 机器证据](evidence/radiance-render-runtime-2026-10-18.json)。旧六组证据继续保留，不能替代本轮结果。

## 2026-10-29 Boss 重入死亡存档专项

[Run-Boss-Reentry.ps1](../tests/Smoke/Run-Boss-Reentry.ps1) 支持 `host`、`guest`、`private`、`no-effects` 四组，使用 `boss-reentry` 模式和现有双端隔离副本。覆盖毛里克原生入场、死亡、碎片碰撞领奖、原远端先重入；当前 `private` 也验证双方各自领奖，且对方拾取不会代领自己的碎片。首轮旧 `private` 结果仅覆盖死亡存档与重入，后续完整领奖结果另行绑定新构建哈希。边界断言另检查显式/隐式存档标识、同名子对象、附加场景标识、阶段零血、重复/旧血量包、明确生命重置和原生半永久重置。修复与分轮验收范围见 [本轮说明](BOSS-REENTRY-FIX-2026-10-29.md)。
