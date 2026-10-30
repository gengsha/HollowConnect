# 测试专用 Unity 探针

此项目不进入运行安装包，不能放在日常游戏的 Mods 目录中。

灵魂战士 Folly 专项使用 `Run-Two.ps1 -Mode soul-folly -NoBuild -RunName <新目录>`，设置 `HOLLOWCONNECT_FOLLY_SCENE=p5|Ruins1_31b`、`HOLLOWCONNECT_FOLLY_CASE=normal|late|handoff|cleanup|diagnostic`、`HOLLOWCONNECT_FOLLY_FIRST=host|guest`、`HOLLOWCONNECT_FOLLY_SHARED=0|1`。已验证的组合见[专项记录](https://github.com/gengsha/HollowConnect/blob/c1a6d16fb5cf14b1f8470bf8c8b18b8f74349a2b/docs/SOUL-WARRIOR-FOLLY-FIX-2026-10-23.md)。正式分支检查原生池配对、移动、主客机实际骨钉碰撞击杀；迟入等原生召唤后再入场，接管由权威原生离场触发。`cleanup` 对 Boss 注入致命健康伤害检查原生小怪清理，并等真实结算完成后退出；不等于完整人工 Boss 战斗。`diagnostic` 仅导出原生 FSM 和观察，不算功能验收。

战吼控制专项使用 `Run-Two.ps1 -Mode boss-roar -NoBuild -RunName <新目录>`，设置 `HOLLOWCONNECT_ROAR_SCENE=GG_Grimm|GG_Collector_V`、`HOLLOWCONNECT_ROAR_FIRST=host|guest` 和 `HOLLOWCONNECT_ROAR_CASE=normal|delayed-start|missing-exit|late|handoff`。收藏家使用原生五门序列索引 26 的上下文，格林实际在敬礼时受到骨钉伤害；后两项针对收藏家。延迟开始用例隔离可靠动作迟到，并以原生 `No Roar` 标记模拟权威跳过锁定的结果，不等于自动执行原版卡菜单技巧。两端必须实际经历预期锁定、恢复控制、等待正常入场保护结束并接受原生伤害；副本还验证旧结束事件不会解除另一段原生战吼。仅 `diagnostic` 模式的 FINAL PASS 表示记录完成。详见 [战吼修复记录](https://github.com/gengsha/HollowConnect/blob/c1a6d16fb5cf14b1f8470bf8c8b18b8f74349a2b/docs/BOSS-ROAR-LOCK-FIX-2026-10-23.md)。

叛徒领主救援耗尽专项：构建一次 Smoke，设置 `HOLLOWCONNECT_EXHAUSTED_BOSS_CHECK=exhaustion`、`HOLLOWCONNECT_EXHAUSTED_BOSS_AUTHORITY=host|guest`，分别运行 `Run-Two.ps1 -Mode rescue -NoBuild -RunName <新目录>`。固定 3 次救援、耗尽后观战开启，要求原生附加 Boss 场景和前置波次就绪；两轮分别覆盖观战者提前返回后最后一人倒地、观战者留场时全灭，并在实际坐椅后再次完成 3 次真实骨钉救援。内存角色补齐原生加载器要求的暗影冲刺，不强制 Boss FSM 或直接加载附加场景。角色位置、死亡和攻击由探针提供；结束后停止隔离进程、禁用 Smoke 并检查双端严格日志。见[修复记录](https://github.com/gengsha/HollowConnect/blob/c1a6d16fb5cf14b1f8470bf8c8b18b8f74349a2b/docs/EXHAUSTED-BOSS-RESCUE-2026-10-22.md)。

五门守望者 0.1.27 复现用 `Run-P5-Repro.ps1 -Prefix <全新前缀>`，默认八组局部行程对照。套件从指定交付 ZIP 固定完整生产 payload，Smoke 只负责内存角色、协调入场、原生 HealthManager 致死和只读跟踪；每个 Smoke 源码版本构建一次，其余组复用。`-NoBuild` 会拒绝比 Smoke 源码旧的插件；`Run-Two.ps1 -PayloadPath <目录>` 可复用固定 payload，不能同时传 `-EnginePath`。生产源码与 DLL 哈希、原始失败及实际先入间隔保存在各次运行目录。`needs-inspection` 和严格日志失败不等于功能通过；人工复现归类见 [复现报告](https://github.com/gengsha/HollowConnect/blob/c1a6d16fb5cf14b1f8470bf8c8b18b8f74349a2b/docs/P5-WATCHERS-REPRODUCTION-2026-10-20.md)。`-Only grimm-host-to-guest-native` 是单独选择的实验性真实入口预备项：本轮取得原生返回值，但入口后的人工快进未通过，未计入守望者覆盖。

修复构建可用 `Run-P5-Repro.ps1 -Prefix <全新前缀> -PayloadPath src/HollowConnect/bin/Release/net472` 固定当前完整生产输出，不能同时指定 ZIP。`-Only context-guards` 追加同步执行并还原的无效采集、空行程、无效计划、缺失/重复回调和取消恢复检查，再实际击杀守望者并注入结算后的迟到心跳及重复完成，要求两端仍正常到乌姆。真实五门菜单/入口动画后快进格林的两种顺序为 `grimm-host-to-guest-native` 与 `grimm-guest-to-host-native`；它们仍不是从入口连续打过前 28 场。修复证据见 [专项记录](https://github.com/gengsha/HollowConnect/blob/c1a6d16fb5cf14b1f8470bf8c8b18b8f74349a2b/docs/P5-WATCHERS-FIX-2026-10-20.md)。

剧情灵魂大师诊断使用 `rescue` 模式，设置 `HOLLOWCONNECT_SOUL_MASTER_CHECK=completion-inspect`、`HOLLOWCONNECT_SOUL_SHARED=0|1`；默认 host 先入，`HOLLOWCONNECT_SOUL_FIRST=host|guest` 可选。`inspect` / `phase1-inspect` / `death-inspect` 仅分段观察。探针实际触碰原版法术、使用原版输入关闭领取提示，并记录门外队友的碰撞与行走结果；终结伤害由测试注入。done / FINAL PASS 指诊断流程完成，不代表 Boss 功能通过；本轮开关对照均确认卡门，详见 [核实记录](https://github.com/gengsha/HollowConnect/blob/c1a6d16fb5cf14b1f8470bf8c8b18b8f74349a2b/docs/SOUL-MASTER-VERIFICATION-2026-10-20.md)。

日常构建与测试现已拆开，见 [开发入口](../../docs/DEVELOPMENT.md)。`Run-Two.ps1` 默认先构建一次；显式 `-NoBuild` 可复用已准备的 Smoke，并检查生产依赖 DLL 哈希。两个套件入口在开始构建一次，各组复用，不再每组编译。改源码后先重建；仅复用输出不表示源码已经重新编译。历史日志和游戏副本只在相关运行目录内读取。

2026-10-17 稳定性复测使用 `Run-Stability-Suite.ps1 -RunPrefix <全新前缀>`，包含共享资源全流程、综合联机、粪便骑士原版下砸/梦钉路线、竞技场双方先领取和四个 Boss 各 30 秒的双端性能窗口。`-Only resources,defender,combat-profile` 可选择专项。`Run-Room-Suite.ps1 -RunPrefix <全新前缀>` 运行 12 组房间、接管、死亡和重连回归；前缀防止覆盖历史证据。每组检查双方终态、严格日志和生产 DLL 哈希，失败后停止；运行完整套件不表示覆盖游戏全部 Boss 的所有招式。

生命周期组现在包含未激活模板夹具：分别使用两种 Preprocessed 标记的默认占位状态，验证完整状态表、运行时动作/变量、首次原生 Awake 和复制后的本机引用。只跑夹具时使用 `pantheon` 模式，设 `HOLLOWCONNECT_FSM_CONTINUATION_CHECK=1`、`HOLLOWCONNECT_FSM_CONTINUATION_ONLY=template`。定向调查真实召唤物可设 `HOLLOWCONNECT_FSM_SCHEMA_TRACE=1`，输出独立 `.schema.log`；性能采样中不启用此跟踪。`unknown state index` 会使所有模式的严格门槛失败，不能仅因最终场景推进就算通过。

竞技场专项使用 `Run-Colosseum.ps1 -RunName <全新名称> -Trial Bronze -First guest -Case return -Wait`。该入口复用现有隔离副本，不构建生产、不删除旧证据；若指定副本仍运行会直接拒绝。`Complete-Colosseum.ps1` 要求双方完成和正常网络退出，仅停止两个确切隔离路径的进程，等待退出后检查严格日志及生产 DLL 哈希。

物理入口专项使用 `Run-Two.ps1 -Mode colosseum -NoBuild`，设置 `HOLLOWCONNECT_COLOSSEUM_CASE=entrance-late|entrance-live`、`HOLLOWCONNECT_COLOSSEUM_TRIAL=Bronze|Silver|Gold`、`HOLLOWCONNECT_COLOSSEUM_FIRST=host|guest`。两种模式分别覆盖开战后加载与已同场但仍在走廊等待；从原生试炼加载位置实际行走跨门，不使用旧 `WalkIntoArena()` 的场内摆放和入场事件。入场后检查本机关门、左右墙恢复碰撞、原生敌人与敌笼推进至第3波（Gold至第30波首次收墙）。最后仅调用原生最终开门动作验证事件传播，不声称完整通关或奖励领取；这些 case 尚未加入旧 `Run-Colosseum.ps1` 枚举。`entrance-schema` 仅记录结构。详见[入口修复](https://github.com/gengsha/HollowConnect/blob/c1a6d16fb5cf14b1f8470bf8c8b18b8f74349a2b/docs/COLOSSEUM-ENTRY-GATE-FIX-2026-10-25.md)。

`Case` 可选 `entry`、`return`、`wipe`、`late`、`bench`、`native`、`lifecycle`、`bounds`、`bounds-baseline` 或只读 `inspect`；三档试炼和两种先入顺序独立选择。`wipe` 不按 F6，检查双方死亡后自动完成一次重试。`lifecycle` 检查双方轮流断线接管、同房重连、实际换个人长椅和再次报名；首次城镇标题需经过原生 Inert 周期后再坐椅。`bounds` 是独立的真实 PlayMaker 快照边界/字符串夹具，使用未联网的测试 NetClient；`bounds-baseline -EnginePath <旧HKMP.dll>` 明确要求旧引擎出现越界，不计修正通过。测试角色已解锁试炼，尚不覆盖首次付费或逐波通关。仅改插件时构建加 `-p:BuildProjectReferences=false`。

左右墙检查使用 `Run-Two.ps1 -Mode colosseum -NoBuild`，设置 `HOLLOWCONNECT_COLOSSEUM_TRIAL=Gold`、`HOLLOWCONNECT_COLOSSEUM_CASE=walls|walls-entry|walls-late`、`HOLLOWCONNECT_COLOSSEUM_FIRST=host|guest`。这些模式尚未加入 `Run-Colosseum.ps1` 的参数枚举。`walls-entry` 在原生第一波比较墙体；`walls` 通过原版敌人死亡回调推进至第 30 波，保留原生墙体 MOVE 和位移动画；`walls-late` 让队友等到原生收缩完成后才入场。墙体从实际登记的独立墙实体及其配对子对象采样，同时检查个人门／镜头锁的事件隔离。测试角色免伤／悬空，接近隐藏小怪触发出现后离开其出生保护区；不强置波次、计数器或墙体状态。输出墙对象、启用的渲染器／实体碰撞体、双端同坐标射线和相机截图；渲染器启用不等于已在相机画面内。done 只表示观测结束，必须另比较权威与复制端结果、检查双端严格日志及 DLL 哈希，不能当成功能通过。见[原始复现](https://github.com/gengsha/HollowConnect/blob/c1a6d16fb5cf14b1f8470bf8c8b18b8f74349a2b/docs/GOLD-WALLS-REPRODUCTION-2026-10-22.md)及[修复验收](https://github.com/gengsha/HollowConnect/blob/c1a6d16fb5cf14b1f8470bf8c8b18b8f74349a2b/docs/GOLD-WALLS-FIX-2026-10-22.md)。

`coverage` 配合 `HOLLOWCONNECT_SCENE_GEOMETRY_TRACE=1` 定向调查叠图：记录已加载场景、地形碰撞体路径/边界及入口，不修改或卸载地形。悬崖行程可设 `HOLLOWCONNECT_COVERAGE_SCENES=Cliffs_01,Cliffs_06,Cliffs_01,Fungus1_28,Cliffs_01,Cliffs_04,Cliffs_05,Cliffs_04,Cliffs_01`。先完成原生长椅起身，从 Town 通过有效的 `Cliffs_01/right3` 入口建立起点；后续有相邻入口时，将本机角色碰撞体交给原生 `TransitionPoint.OnTriggerEnter2D`，不是实际操控走过全房。`HOLLOWCONNECT_GEOMETRY_GUEST_ALONE=1` 让房主始终留在 Town，由副机独自往返；默认双方依次进房。输出只证明采样时的场景/碰撞体状态，不等于复现或修复用户的叠图，也不覆盖完整渲染和长时间游玩历史。

叠加 `HOLLOWCONNECT_CLIFFS_INTERIOR_CHECK=1` 在 `Fungus1_28` 内开启共享进度，检查幼虫左井和宝箱下竖井：角色放到开口上方后必须依靠原生重力实际落下，场景权威通过原生攻击输入挥刀开箱，另一端必须看到盖板碰撞关闭、Opened 子物体启用并实际穿过箱口。可用 `Cliffs_01,Fungus1_28,Cliffs_01,Fungus1_28` 配合 guest-alone 检查副机重入。不会关闭地形、强设宝箱 FSM 或穿墙；不是从入口完整行走、原故障渲染或长期游玩复现。

测试选择遵循用户 2026-10-16 的要求：日常小改动选取最近新增或修改的 100 个检查项，以 Git 修改记录为准，不足 100 项则全选；另补本次改动直接相关的检查和实机专项。100 项指检查项，不是 100 组双客户端运行，不默认启动全部长期实机专项。当前运行器未提供按 Git 历史自动选取最近 100 项的功能，执行前需明确选择范围。

重大更新（如公共协议、权威交接、生命周期架构或大范围功能变更）再做全量检测。普通小版本号、文档或仅构建标识/哈希变化不单独触发全量；打包仍核对载荷、版本和校验和。记录实际运行范围及未跑项目，未跑不算通过，失败记录和原断言仍保留。下文的全套场景与完整发布矩阵用于全量检测或明确的完整发布验收，不作为每次普通修复的默认范围。

普通回归运行器支持 `HOLLOWCONNECT_TEST_SELECTION` 指向 UTF-8 检查项名称列表，每行一个完整名称；不设时运行全部，列表中未匹配的名称使运行失败。它只按明确列表执行，不自动判断最近 100 项。本次危险地形救援按 Git 中检查注册/主体和直接引用的 Probe 文件修改顺序选择最近 100 项，再补 21 项救援/倒地等相关检查，具体名单留在专项证据中。

`rescue` 配合 `HOLLOWCONNECT_HAZARD_RESCUE_CHECK=1` 使用原生酸水 `Fungus1_02` 和尖刺 `GG_Vengefly`，分别让双方实际碰撞致死并由队友骨钉碰撞救起。验证两端救援锚点位于本次原生安全复位点、救起两秒后仍一血站立且可操作、保留个人暗影和吉欧，并留在原房间。使用原生入场安全点作为测试复位点，救援者站在房间内侧，不能跨入入口触发器；不设置 Boss FSM、不直接调用复活、不写个人存档。深坑类型 5 的生产分支尚无真实深坑专项，不把上述两种危险地形扩写为全部危险源验收。

`rescue` 配合 `HOLLOWCONNECT_RESCUE_NATIVE_CHECK=1`（或 `placement`）在 `Crossroads_01`、`GG_Vengefly`、`Dream_02_Mage_Lord` 分别让双方受到空中普通致命伤，队友必须用实际骨钉救援。流程／梦境要求有支撑的死亡侧落地，神居保留原生本地危险复位点，并包含梦境复位点为零的夹具。救起后检查原生无敌、脉冲颜色、重复确认不续期、保护到期后正常受伤、真实落地和双端可见。为隔离保护逻辑，屏蔽额外战斗接触伤害；显式伤害检查仍执行原生 TakeDamage。救援者允许移位至经碰撞体净空检查的挥刀位置，该专项不是行走路线证明；路线使用下方独立流程专项。值为 `protection` 仅用于旧 DLL 无保护的失败对照，跳过落点断言，不可作为最终验收。

`progress` 配合 `HOLLOWCONNECT_DREAM_WARRIORS=all` 检查七位普通战士之梦，`HOLLOWCONNECT_DREAM_SUMMONER=host|guest` 选择唯一交互发起者。双方实际加载原生场景，从 NPC 交互入口和 Yes/No 选择启动挑战，由原生 HealthManager.Hit 造成致命伤害，等原生死亡收尾后各自完成原生领奖。发起者先领并留在房间，队友再领；检查重复完成、旧挑战不能重复领奖或重启，副机通过真实 UDP 提交提前完成必须被服务器拒绝。`inspect` 只导出状态机，不算战斗验收。各战士之间经过中立场景并等旧 boss 附加场景卸载，避免直接强跳导致原生同名 Warrior 文件夹查错；不将该前置处理当作生产修复。

无眼共享提灯专项：先构建一次 Smoke，执行 `Run-Dream-Warriors.ps1 -Prefix <全新前缀> -PayloadPath src/HollowConnect/bin/Release/net472 -Cases guest,host -Summoner guest`；交换 `-Summoner host` 再检查反向发起。`Cases` 指亲自取得提灯的玩家，双方均未战胜无眼，另一端能力经真实 UDP 共享；等待双方实际拥有提灯才进入房间。检查 NPC、单一权威原生无眼与远端实体、原生死亡、双方各领一次 200 精华、输入恢复、重复完成与伪造完成拒绝。提灯购买经历是内存夹具，不宣称执行了商店购买动画。`-Scenes RestingGrounds_02` 可检查泽若对照，或指定其他已知战士场景；`-Inspect` 只导出 FSM。默认 `Fungus1_35`。测试仅使用隔离副本、拦截保存，保留失败和二进制哈希。

战士之梦测试的内存角色预先具备梦之钉和灯笼；无眼的原生 NPC 在没有灯笼时会自行停用，不能将缺少挑战资格的运行记为同步失败。

`godhome` 的主动观察器故障注入需显式设置 `HOLLOWCONNECT_GODHOME_FAULT_ASSERTIONS=1`，故障日志仍须被严格日志门槛拒绝，不能与普通通过的运行合并。F8 夹具先等待本机原生入场允许操作，再发送一次按键；不会修改生产输入门槛。

末步“传送已接受后断线”同样等待生产 `CanStartWarp`，保留一次按键、原 45 秒超时和断线到达断言。诊断记录传送资格、发出/已处理序号和本地提示；0.1.17 首次末步超时缺少相应轨迹，追踪和补前置后通过不能唯一解释旧失败。该失败单独保留，不因重跑通过自动关闭。

`progress` 模式用无存档写入的双客户端检查遗忘十字路鹿角站共享：购买者由 `HOLLOWCONNECT_PROGRESS_BUYER=host|guest` 选择，`HOLLOWCONNECT_PROGRESS_PROMPT=1` 让接收者正在原生购买对话内。探针程序化进入原生交互入口，随后通过原生 Yes/No 控制 FSM 的选择事件支付，保留真正的 TakeGeo、授权和开站动画。断言当前房间无需重新加载即可使用、仅购买者扣 50 吉欧、对话结束、控制恢复、重复进度 offer 不改变钱或原生动作。`HOLLOWCONNECT_PROGRESS_BASELINE=1` 在旧实现上确认接收者还能再次付费并明确失败；不能计入验收。之后实际购买科尼法首张地图并跨 Crossroads_08、Crossroads_30 检查双端角色/快照，`HOLLOWCONNECT_MAP_BOTH=1` 覆盖双方购买；地图和吉欧仍为个人数据。`HOLLOWCONNECT_PROGRESS_INSPECT=1` 只导出原生购买 FSM，不是购买验收。成功路径使用已有 ProbeShutdown 完成网络退出后才写终态。

所有测试模式创建 SilentProbe，启动及每帧将 AudioListener.volume 设为 0；只静音隔离测试进程，不修改用户音量配置。

`coverage` 的 `HOLLOWCONNECT_PERFORMANCE_ASSERTIONS=1` 检查六个耗时回调在真实 Unity 帧上按顺序分帧完成；再让十二个不同的缺失预制体请求共用同帧资源候选，下帧重新查询、清理后释放缓存。缺失项必须返回 null，不允许取错误资源。它验证公共限额与失败路径，不代表全部真实弹幕突发都没有长帧。

该开关配合 `HOLLOWCONNECT_COVERAGE_PERFORMANCE=1` 和 `HOLLOWCONNECT_COVERAGE_PROFILE=1` 时，还要求后来进入的非场景权威在每房确实收到压缩初始快照，并在夹具运行前记录数量；仅夹具的缓存回放不能代替此要求。

`coverage` 的 `HOLLOWCONNECT_COMBAT_SNAPSHOT_ASSERTIONS=1` 使用未连接的发送器检查真实战斗状态采样：未变化去重、复用缓冲后已发布包不变、压缩完整快照经生产编解码和序列过滤处理乱序/重传/回绕、缓存副本及接管。该夹具不冒充真实迟加入网络流程；`HOLLOWCONNECT_COMBAT_ASSERTIONS=1` 另走房间 UDP 与实际 HeroBox 受伤。性能输出的 `allocatedBytes` 仅在运行时提供当前线程分配计数器时有效；Unity Mono 输出 `allocation counter=unavailable` 时不能把零值当作零分配。

`coverage` 中的 `HOLLOWCONNECT_FSM_NULL_STRING_ASSERTIONS=1` 在双方各运行真实 PlayMaker 字符串夹具：初始 null 后赋值、再次改变、清回 null，经生产采样和实际编解码检查原生/副本变量、重复采样、迟加入缓存及接管恢复。夹具使用未连接的 NetClient，不将测试实体发进真实房间；它证明字符串遗漏问题，不证明用户 Mines_11 数组越界的根因。`HOLLOWCONNECT_FSM_LAYOUT_TRACE=1` 仅在测试插件观察登记、Awake 和首次字符串接收时的实体/FSM/变量表，用于继续定位该未解异常。

已载入内存角色的游戏内探针连接时设置 `_autoConnect`，保留已选角色；从菜单开始的 JoinProbe 则仍走真正选档/新档路径。格林探针观察 ProfileMenu 请求，正常游戏内检查必须为零；`HOLLOWCONNECT_GRIMM_MENU_BASELINE=1` 保留旧夹具路径并在实际菜单请求后明确失败，用于对照，不触发 Boss 战或计入验收。原生崩溃标记在发布日志扫描中独立失败。

`Run-Camera-Gate.ps1` 顺序执行真实 Unity 镜头判定的三组对照：主动冻结且没有危险复位状态必须立即失败，实际尖刺碰撞后明确阻塞原生危险复位协程必须在 15 秒真实时间后失败，正常原生尖刺复位必须自行恢复。两组故障注入保留 `.failed`，只在失败原因为预期精确原因且没有其他运行错误时，才记为判定对照成立；不计入 12 组战斗验收，也不强制解冻。`-ReuseNonHazard` 复用并重新核对已完成的第一组；`-ReuseAll` 重新核对三组现有日志、标记与生产哈希并生成证据，不启动新游戏。早先停止模拟时间的夹具被游戏恢复时间倍率，未形成持续故障，其失败单独保留。

`HOLLOWCONNECT_TWEEN_CLONE_CHECK=1` 在真实 Unity 父子物体上启动两条 iTween，经生产 Entity 构造复制并激活显示副本；要求无复制来的运行组件、无激活异常、原动画 ID / 参数仍完整且新副本动画实际运动。`HOLLOWCONNECT_TWEEN_CLONE_ONLY=1` 可只运行此项。旧引擎稳定触发 RetrieveArgs / Awake 空引用，保留为失败基线；两种最终生命周期专项均包含此检查。

0.1.13 增加房间生命周期专项：`HOLLOWCONNECT_LIFECYCLE_CHECK=1` 在真实苔藓冲锋者显示副本执行原生持续音效动作，控制其音源先销毁，然后击杀权威 Boss 并沿原生行程进入大黄蜂；验证旧实体清除、唯一模拟权、Boss / 队友可见、镜头锁一致，并在大黄蜂房内断线重连再次检查。原始 0.1.12 DLL 在该销毁顺序下清理抛错、后续登记失效。故障注入不是完整自然游玩复现。

`HOLLOWCONNECT_COLLIDER_RECONNECT_STRESS=1` 再追加六轮真实大黄蜂 Throw / Thrown 时断线、同房重连，要求每轮自然再次投针、Needle / Needle Tink 有效并进入 Throw Recover。固定原生免伤靶子留在场内；等待原生招式而不强设 Boss 状态。两组最终生命周期专项都必须启用，零样本失败。

重连压力测试还比较双方所有静态实体 ID，防止 Boss ID 一致但脱离层级的飞针 ID 已经分叉。原构建在首次同房重连即失败；单纯检查引用非空无法发现此问题。`HOLLOWCONNECT_STATIC_IDENTITY_CHECK=1` 用真实 Unity 对象检查脱离父级仍保留身份、复制的元数据不继承原对象身份，两组最终生命周期专项均启用。

`HOLLOWCONNECT_FSM_CONTINUATION_CHECK=1` 使用真实 PlayMaker / iTween 动作和生产 `Entity.MakeHost` 检查未完成的绝对位移动画先到目的地再完成、休眠快照不启动 tween 且后续原生激活正常完成，以及带权威速度快照的 `DecelerateV2` 恢复固定帧减速。夹具物体独立于 Boss。`HOLLOWCONNECT_FSM_CONTINUATION_ONLY=1` 只做此项；值为 `deceleration` 时单独检查减速，用于保留旧引擎失败对照。旧引擎双端分别出现提前完成和持续 38 速度；只加恢复表的中间构建会在休眠源创建未 Awake 的 tween。这些失败不能用随机六轮重连通过覆盖。

`HOLLOWCONNECT_FSM_CONTINUATION_STRESS=1` 强制每轮重连换到另一端模拟权威，在原有六轮自然 Throw / Thrown 断线后，再追加自然 Needle Decel 和 Return 中各一次断线。每轮仍要求静态身份、引用、血量、镜头与真实 Throw Recover 通过，原超时和严格日志门槛不变。启用此项的两种先入顺序是本次修复的专项范围，不自动替代旧构建的完整 12 组发布证据。

`HOLLOWCONNECT_PAUSE_MOTION_CHECK=1` 等待原生 A Dash 且速度大于 10，断线后让同一原权威先重连，要求继续移动并自然完成冲刺。`HOLLOWCONNECT_PAUSE_MOTION_ONLY=1` 可只做这项定向诊断；最终两组生命周期专项启用前者，并继续六轮投针检查。旧实现的真实基线在相同 FSM 状态恢复成零速度，8 秒内不能完成；没有强设 Boss 状态或速度。

`HOLLOWCONNECT_CAMERA_HAZARD=1` 诊断使用真实虫王房 Cloud Hazard，记录原生危险死亡的冻结来源，调用与普通房间验收相同的恢复判定。只允许 hazardDeath / hazardRespawning 中的冻结在 15 秒内恢复；非危险状态或持续冻结仍失败，不改变生产镜头。`HOLLOWCONNECT_CAMERA_RING=1` 保存最近模式调用来源，在冻结时输出，正式两种原生入口均启用。

`HOLLOWCONNECT_NATIVE_ENTRANCE=1` 让内存角色经实际一门选单、入口动画和对白进入战斗。测试仅启用新角色锁住的门，并调用门选单状态；不强迫任何 Boss FSM。对白通过原生 NEXT 输入事件推进。主客两种先入顺序各跑两轮，完成大黄蜂后进入温泉；大黄蜂另检查双方实际骨钉伤害进入同一权威血量。其他房通过 HealthManager 额外伤害推进，迟转场条件仍由测试 Down 标志控制，不能当作全程实际操控通关。

`Run-Two.ps1` 仅启动已准备的 `.test-results/game-host` 和 `game-guest` 隔离副本，记录实际生产 DLL 哈希；不会创建游戏副本。`Run-Room-Suite.ps1` 顺序执行本轮专项并核对双端终态与最终 DLL。任一失败记录不得计入通过。

`boss` 模式新增脱离层级的子物体断言：露头后 FSM 的子物体引用仍指向同一对原生/副本；移交时仅原生地形块参与碰撞，恢复战斗后地形块全部停用；断线移除实体登记表后，脱离层级的显示副本仍必须销毁。`HOLLOWCONNECT_SMOKE_BOSS_LATE=1` 让客机在原生 Boss 已露头且经过两秒层级刷新后才进房，核对初始快照和接管。`HOLLOWCONNECT_SMOKE_BOSS_CONTROL=stay` 是不移交权威的诊断对照，不属于最终验收。`Summarize-Hierarchy-Diagnostics.ps1` 保留修改前失败和中间构建哈希；正式记录由 `Summarize-Room-Suite.ps1` 核对 12 组最终构建。`Run-Room-Suite.ps1 -Only <case-name>` 可补跑指定场景，汇总仍要求全部记录与当前生产 DLL 一致。

0.1.11 新增 `HOLLOWCONNECT_SMOKE_MODE=death`。两个内存角色先分别验证 Town / Crossroads_47 的真实 RestBench 标记，通过真实 UDP 连接后错开入场。检查就绪前免伤、就绪后的 1.5 秒窗口、恢复正常受伤；普通房间另检查地形危险不扣血但完成原生危险重生。注入列表早于可靠 Down、重复 Down 和复活后旧 Down，核对反馈只一次、旧事件不能覆盖复活、粒子按真实时间清理。

普通模式从真实致命 TakeDamage 倒地，核对无死亡动画、无丢钱、一次本机受击声与有界原生粒子，然后授权救援、一血复活。通过 F6 原生死亡回客机自己的长椅，检查动画、吉欧池、灵魂限制与暗影场景；再进房找到原生 Hollow Shade(Clone)，使用原生 HealthManager.Die 回收自己的吉欧和灵魂。全队失败验证不同个人长椅、原版动画与一次个人处罚。`HOLLOWCONNECT_DEATH_FIRST=host/guest` 交换全队倒地顺序。实际骨钉击破救援暗影和存活者继续造成敌人伤害由完整 Probe 另验，不把直接授权请求当作骨钉碰撞验证。

`HOLLOWCONNECT_DEATH_SCENE=Dream_01_False_Knight/GG_Grimm/White_Palace_01` 切换原版特殊区域，验证相应死亡分支、不掉钱/不生成暗影和个人隐性返回或长椅。梦境使用实际 door1，神居使用实际 door_dreamEnter；先访问 GG_Atrium 并读取原版 Start 重命名后的个人挑战返回门，再进入战斗。`HOLLOWCONNECT_DEATH_SEQUENCE=1` 使用三门内置行程从格林段开始验证全队失败；这不等于完整三门通关。白宫原版采用 Death Dream 动画但走个人长椅返回分支。`HOLLOWCONNECT_DEATH_DISCONNECT=1` 在普通房间接受 F6 正式死亡动画后断线，要求原版死亡仍到达个人长椅、只扣一次吉欧，存活端继续保有房间权威和原轮次。

批处理探针仅对 Respawn、HazardRespawn 与 LoadFirstScene 的渲染帧等待改为下一模拟帧。生产模块不替换这些原生协程。

复活运动检查在个人 F6、全队回椅和特殊区域返回后，等待原生入场与反应保护完成，再连续两次调用原生 HeroJump / Jump，要求正重力、起跳、负向落速和最终落地。悬空测试靶子使用 AffectedByGravity(false) 保留原生缓存；返回后不再 PrepareHero。梦境返回的 Dream Return/Ready 原本等待玩家按键，探针发送其 ListenForJump.wasPressed 配置的原生事件完成唤醒，未直接改 FSM 状态。超时保存角色物理、输入及附近/骑士 FSM 状态。

`HOLLOWCONNECT_DEATH_INPUT_CHECK=1` 加强上述返回检查：在原版长椅起身前打开、关闭物品栏，再按跳跃离椅；随后用输入接口连续跳跃、攻击，并在站立时再次开关物品栏，要求原生控制和地图资格恢复。测试只临时替换指定 InControl action 的按下/按住读取，让原版监听器和角色输入分支自行处理，不调用跳跃或攻击方法，也不强行 RegainControl。长椅仍须自然完成唤醒动画。`HOLLOWCONNECT_DEATH_SHARED_BENCH=1` 让双方都回 Town，默认仍为不同个人长椅；配合 `HOLLOWCONNECT_DEATH_FIRST=host/guest` 交换死亡顺序。

`coverage` 的 `HOLLOWCONNECT_COVERAGE_PERFORMANCE=1` 用自然落地的免伤靶子观察灵魂法师系列下砸，每两秒记录实体数量、帧耗时和临时 detour 测得的登记、退休、接收生成、预制体查找与处理耗时。它关闭逐帧位置文件和重复实体详列，保留开场 FSM/预制体及最终生成数量；未观察到原生 Quake Down 必须失败。`HOLLOWCONNECT_COVERAGE_SCENES=GG_Soul_Master,GG_Soul_Tyrant` 选择两个变体，`HOLLOWCONNECT_COVERAGE_AUTHORITY=guest` 让服务器房主显示副本。`HOLLOWCONNECT_COVERAGE_SOUL_PHASE2=1` 用原生 HealthManager 伤害推进二阶段，不设置 Boss FSM。该同机诊断不等于真实双机 FRP 或完整招式验收。

普通远程敌人诊断可加 `HOLLOWCONNECT_COVERAGE_PROFILE=1`：临时统计实体 Tick、CombatState 采样/应用耗时和按类型生成的应用层字节；这些是累计量，不等于单帧耗时或实际网卡流量。`HOLLOWCONNECT_COVERAGE_ENTRY_GATE=right1` 选择真实存在的入场门，配合 `HOLLOWCONNECT_COVERAGE_SCENES=Mines_11,Ruins1_30`。`HOLLOWCONNECT_COVERAGE_NEAR_TARGET=1` 在入场后把免伤靶子放到首个目标旁并悬停，以触发原生范围判断；不设置敌人 FSM。它要求水晶房观察到 CrystalCrawler/Beam、圣所观察到 Mage Orb 生成，否则失败。这仍不证明全部招式、伤害或真实双机掉帧已通过。

0.1.10 新增 `HOLLOWCONNECT_SMOKE_MODE=grimm-motion`。`HOLLOWCONNECT_GRIMM_SCENE=GG_Grimm/GG_Grimm_Nightmare` 选择房间，`HOLLOWCONNECT_GRIMM_AUTHORITY=host/guest` 交换先入顺序，`HOLLOWCONNECT_GRIMM_ENTRY_DELAY` 指定后来者延迟秒数。直接观察原生 FireGrimmBall 的推力、网络登记后的协程和横向运动，另检查横向火蝠、副本无重力 / 速度和位置样本。要求至少 8 次保留发射参数、3 次正确方向横向运动和 3 个横向飞行火蝠；不能用两端共同错误的位置一致替代它。

`HOLLOWCONNECT_GRIMM_HANDOFF=1` 在气球火球一轮结束后让原权威离房，要求新权威继续发射上述原生攻击，再让原权威重新入房核对移动弹幕。这不证明飞行中弹幕接管。双方正常断开后才停止服务器，任一 `.failed` 均为失败。当前角色免伤但仍会受到原生运动与碰撞，招式未触发 / 样本不足也必须失败；诊断记录 Hero 和 Boss 的站位及原生状态，不能隐藏这种失败。

0.1.9 新增 `HOLLOWCONNECT_SMOKE_MODE=pantheon`：使用原生一门行程，从苍蝇之王连续推进到大黄蜂，重复两轮并重连；`HOLLOWCONNECT_PANTHEON_AUTHORITY=host/guest` 交换先入顺序。延后真实初始快照三秒，首房构造“快照捕获时关闭、随后开启”的 Boss / 镜头锁区状态，检查两端活跃、渲染、镜头模式、实体身份和血量；重放重复初始快照、旧访问离房及重复 Connect，当前人物不能被清掉。晚入场端通过测试 Down 标志延后原生结算，先入者先转场并开打，再释放后来者；未强迫 Boss FSM 状态。假骑士通过各轮原生露头状态的 HealthManager 伤害完成阶段。

`HOLLOWCONNECT_PENDING_PEER_CHECK=1` 在快照延迟期间让队友移动后停下，后来者必须显示最新位置；`HOLLOWCONNECT_SAME_ROOM_CHECK=1` 单独验证不换房的初次连接和断线重连；`HOLLOWCONNECT_EARLY_TRANSFER=1` 单独让先入者在后来者初始快照未交付时离房，接管不能被迟到快照撤销。后两种模式完成专项后退出探针。

0.1.8 新增 `HOLLOWCONNECT_SMOKE_MODE=coverage`：`HOLLOWCONNECT_COVERAGE_SCENES` 指定逗号分隔的原生 Boss 房间，`HOLLOWCONNECT_COVERAGE_SECONDS` 指定各房观察秒数，`HOLLOWCONNECT_COVERAGE_AUTHORITY=guest` 调换先入顺序。使用原生 BossSequence 上下文入房，记录根/子碰撞器、DamageHero、位置、动态实体数量和模拟帧数；马科斯盾要求至少十个可比样本且位置匹配率不低于 80%。这只是观察与有限断言，不保证全部招式或阶段已触发。

`HOLLOWCONNECT_COMBAT_ASSERTIONS=1` 在首房加入同 ID 的测试危险物，验证根物体旋转、空中间父节点的启停、子碰撞器尺寸/偏移/世界坐标、透明度、材质 PropertyBlock 且不污染本地共享材质、副本 Recoil 不本地叠加位移。将角色置入已同步判定，必须经原生 HeroBox 碰撞实际扣血；不会直接调用 TakeDamage 代替碰撞。双方完成启用、伤害和停用后才清理夹具。

0.1.8 的 authority 探针进一步要求接管后的 FSM 状态推进，不能把短暂惯性滑动算作 AI 恢复。前置骨钉测试把客机留在高处，接管检查把客机放到怪物左侧附近，以免测到无目标/撞墙的自然静止。Boss 阶段探针记录原生阶段、动画和刚体诊断，仍不强迫 Boss FSM 状态。

Coverage 还要求大黄蜂的原生 Needle 在副本上有至少三个位置匹配且渲染/碰撞/伤害组件有效的样本；纯粹容器至少三个横斩或冲刺子判定启用且位置匹配样本。`HOLLOWCONNECT_NATIVE_CONTACT_ASSERTIONS=1` 另在纯粹容器房等待原生横斩/冲刺，将角色置入其有效碰撞判定，要求两端实际扣血且伤害来源是 Slash1/2/3 或 Dash Stab。不会强迫 Boss FSM 或直接调用 TakeDamage；这仍不代表全部招式的伤害验收。

0.1.7 的 godhome 探针另覆盖：带血量 Boss 的 FSM 改名与歧义拒绝、脱离父体的注册子体、同名/不可见子体及特殊字符路径引用、倒地期间延后结算，以及同房 F8 修复错误序号和束缚。日志记录器支持网络线程并发，异常与超过五分钟会明确生成 .failed，不能把没有终态的探针算作通过。

构建：`dotnet build tests/Smoke/HollowConnect.Smoke.csproj -c Release`。先按根目录 Build.ps1 准备本机游戏引用。

辐光专项：增量构建 `scripts/Build.ps1 -Target Smoke -NoRestore` 后，以 `coverage` 模式设置
`HOLLOWCONNECT_COVERAGE_SCENES=GG_Radiance`、`HOLLOWCONNECT_COVERAGE_SECONDS=2`、
`HOLLOWCONNECT_RADIANCE_WARP_ASSERTIONS=1`、`HOLLOWCONNECT_RADIANCE_PHASE_ASSERTIONS=1`，
分别设置 `HOLLOWCONNECT_COVERAGE_AUTHORITY=host` 和 `guest`，每次用全新 RunName 调用 `Run-Two.ps1 -NoBuild`。
专项包含原生入场、两端 F8 初始地面落点、正常 Hit 推进阶段、真实深渊碰撞复位、平台支撑、5 秒生命稳定、从 Town 跨房迟入后 28 秒镜头/控制与 Boss 阶段保持。单独 coverage 采样不包含这些验收。
结束后停止对应隔离进程并运行 `Assert-ProbeLogs.ps1 -RunPath <本轮目录> -RequireRuntimeFiles`。

辐光完整平台／爬升迟入矩阵：`tests/Smoke/Run-Radiance-Arena-Suite.ps1 -Prefix <全新前缀>`。
0.1.24 起默认显式调用实际 D3D 游戏镜头渲染，渲染后按原版数量核对平台显示与碰撞，并逐平台检查重力支撑；纯批处理未渲染的旧通过记录不足以覆盖 Unity 可见回调。增加 `-ArrivalStage before-challenge` 运行普通辐光宣战前四组（F8／原生入口 × 双方先入）；默认六组继续覆盖二阶段与爬升。相关复查见 [渲染回调审计](https://github.com/gengsha/HollowConnect/blob/c1a6d16fb5cf14b1f8470bf8c8b18b8f74349a2b/docs/RADIANCE-RENDER-CALLBACK-AUDIT-2026-10-18.md)。
一次构建后运行雕像、五门、普通辐光 × F8／原生入口六组，交换先入者，其中一组注入并验收丢包／乱序／重复。
支持 `-Only` 选择 case 和 `-NoBuild` 复用当前 Smoke。逐平台、锁区、爬升激活时间、低中高台阶承重与 viewport 均有断言；双方有序断开和退出后执行严格日志门槛。
单组使用 `HOLLOWCONNECT_RADIANCE_FRESH_LATE=1`，可选 `RADIANCE_FRESH_SCENE=Dream_Final_Boss`、`RADIANCE_FRESH_ENTRY=native`、`RADIANCE_FRESH_P5=1`（均带 `HOLLOWCONNECT_` 前缀）。运行边界及证据见 [平台修复记录](https://github.com/gengsha/HollowConnect/blob/c1a6d16fb5cf14b1f8470bf8c8b18b8f74349a2b/docs/RADIANCE-PLATFORM-FIX-2026-10-18.md)。

必须在两个独立的测试游戏副本中运行，设置 `HOLLOWCONNECT_SMOKE_ROLE=host` 或 `guest`，并让两端 `HOLLOWCONNECT_SMOKE_ROOT` 指向同一个全新协调输出目录。探针使用本机 UDP 29650；双方需要相同的引擎、协作模块和测试 DLL。

Unity 的 PlayerSettings `forceSingleInstance` 需要仅在测试副本中关闭，也需从副本 boot.config 移除 single-instance。可用 UnityPy 修改独立复制的 globalgamemanagers；不要修改或硬链接后写入正常游戏的数据文件。启动前设置 SteamAppId 和 SteamGameId 为 367520，Steam 需要正常运行。可使用 `-batchmode -screen-fullscreen 0 -logFile 完整日志路径`。测试结束以两端的 .done 文件及日志 FINAL PASS 为准，任一 .failed 均为失败。

测试副本的 `Config.ini` 也必须是独立副本，语言初始化会写入该文件；硬链接会使同时启动的进程发生文件锁冲突。原版骨钉通过 `damages_enemy` FSM 调用 HitTaker，命中检查同时兼容该路径和 DamageEnemies。

0.1.1 探针包含实际致命 TakeDamage、原生暗影 Idle 资源核对、死亡角色和相机位移后的锚点恢复、靠近与挥空无效、真实骨钉命中后原地一血复活、断线后本地状态恢复与远端暗影移除。全队重试和原有怪物、进度、地图与传送场景继续执行。

0.1.2 探针通过真正的 F6 输入处理分别让副机和房间权威主动放弃：核对个人长椅标记、抵达位置、骑士渲染和镜头恢复，全队编号保持不变，存活端继续造成伤害，返回者重新进房获取当前血量。原版 PositionToHero 在 isHeroInPosition 后仍会短暂冻结镜头；检查等待原生定位完成，5 秒内仍冻结则失败。存活时 F6 无效。

0.1.3 新增 `HOLLOWCONNECT_SMOKE_MODE=join`：从菜单建立连接后调用正常 StartNewGame，经过开场进入 Tutorial_01。房主先进入，客机稍后加入；检查双方远端骑士对象、Renderer 和怪物权威初始化，再让客机断开、退回菜单、新建角色重连，重复检查。该探针已在 0.1.2 上复现单向不可见，在 0.1.3 上两端通过。0.1.4 再触发原生房主超时退菜单，保留监听器、通过正常 ServerManager.Start 在同一端口重新开房，两端重新加入并核对全新协作 session、骑士可见性和权威初始化。

0.1.4 新增 `HOLLOWCONNECT_SMOKE_MODE=authority`：杀死并销毁一个副本敌人，另一原生活怪索敌移动后权威离房，核对留下者完成接管、AI 继续运动、死怪不复活，以及旧权威回房收到持续战斗的血量。该探针已在 0.1.3 复现接管中断，在修复版通过。其它模式继续执行完整战斗探针。

0.1.5 完整探针新增：第二轮 Release 后重放同轮 Wipe，确认屏障不再打开；随后在真实跨房传送与检查点加载启动后立即断线，确认协程、入口、锁定状态和隐藏渲染均清理。新增 `HOLLOWCONNECT_SMOKE_MODE=boss`：进入 Crossroads_10，用原生竞技场触发器与 HealthManager 伤害驱动假骑士开战、盔甲倒地和头部部分受伤；在头部暴露期间移交权威，击败头部推进阶段，确认 AI 继续运动和再伤害，并让原权威回房核对血量与 Stunned Amount 阶段计数。没有强行设置 Boss FSM 状态，也没有覆盖所有 Boss。

0.1.6 新增 `HOLLOWCONNECT_SMOKE_MODE=godhome`：使用内置三门从格林段开始，观察原生火蝠、火柱、气球火球和地刺，原生伤害及死亡后双方进入休息房；双向普通 / 神居 F8 检查淡入、镜头与输入，故意损坏落后端连战序号后核对恢复，再在已接受跨房加载后立即断线并检查正常抵达。`HOLLOWCONNECT_GODHOME_AUTHORITY=host` 让服务器房主先入房，否则客机先入房，分别覆盖房主作为显示副本和模拟权威。动态弹幕每 0.1 秒观测本地源位置，小于 3 世界单位的副本样本计为匹配；每种弹幕至少三次匹配。角色固定在指定位置并免伤，直接调用 HealthManager 伤害；没有强制 Boss FSM 状态，也没有完成完整三门。`HOLLOWCONNECT_GALIEN_ONLY=1` 单独验证加利安迟加入接管的 GetOwner 本机引用及原生音效异常。故障注入与原生音效诊断只存在于此测试插件。

探针拦截三个 SaveGame 重载，不读取现有存档；角色在内存中创建。测试插件在批处理模式下将 Respawn 和 LoadFirstScene 中的渲染帧等待改为下一模拟帧，发行模块没有这一行为。测试结束停止这两个测试进程，移除测试副本中的插件；不要把它复制到日常游戏。验证内容与局限见 docs/VERIFICATION.md。
## FSM 快照与接管专项

`HOLLOWCONNECT_NETWORK_FAULTS=1` 在测试插件内、DTLS 加密前按每个独立 transport 注入固定种子故障：连续丢两条消息，间隔 10–16 条；将一条延后到后续消息之后，间隔 6–12 条；重复一条，间隔 12–22 条。均值分别为 13、9、17，种子为 `0x6D2B79F5`，不依赖包内容；缓存完整应用消息，绝不跨连接混合分片。不会增加生产重试或调整超时；最终仍要求原有招式、回收位置、镜头、双端终态及严格日志门槛全部通过。结束日志的 `NETWORK FAULTS` 记录实际丢包、乱序、重复和嵌套重发数，没有实际覆盖全部配置故障就失败。这是同机故障注入，不等于真实双机 FRP 验收。

`HOLLOWCONNECT_NETWORK_FAULTS=periodic` 保留最初每 13/9/17 条的周期性故障。每 13 条丢两条可能与现有约 13 包一次的可靠重发同步，造成持续心跳正常而同一入场消息所有重发都被丢弃；对应旧失败不能由固定种子组通过解除。`HOLLOWCONNECT_NETWORK_TRACE=1` 额外记录入场/离场消息的原始来源、接收筛选及游戏上下文，并记录实际 DROP/HOLD，日志为各角色独立的 `.presence.log`，不改变分发。

`Summarize-Network-Reliability.ps1` 汇总 2026-10-14 网络专项的五组指定验收及修改前失败；只接受锁定的生产构建和三个 DLL 哈希、双方 done/FINAL PASS、实际故障计数、九项 Unity 夹具与两种顺序各八轮交换/回收。须先停止两个隔离进程并移除隔离 Smoke 插件，脚本再复扫结束后的日志；旧构建、缺项或失败都拒绝生成通过汇总，报告始终保留未解决项和 `releaseReady=false`。

`HOLLOWCONNECT_FSM_CONTINUATION_CHECK=1` 在联网前运行真实 Unity/PlayMaker 的绝对 tween、固定帧减速和休眠接管夹具；`HOLLOWCONNECT_FSM_SNAPSHOT_CHECK=1` 再检查原生状态在采样窗口内即时发布、空状态清除旧快照，以及原生重激活的 active/state 在同一可靠更新中发布。`HOLLOWCONNECT_FSM_CONTINUATION_ONLY=snapshot` 只运行后三项，须同时启用前两个开关。所有夹具都通过生产 Entity 路径，使用独立测试对象，不强制 Boss 状态。

`HOLLOWCONNECT_FSM_CONTINUATION_STRESS=1` 配合投针重连专项，让每轮另一端先重连并成为权威，前六轮之外再覆盖 Needle 自然 `Decel` 和 `Return`，共八轮。可用 `HOLLOWCONNECT_CONTINUATION_TRACE=1` 记录原生状态恢复、激活/停用、通知收发、父物体和坐标；追踪不改变原生执行，也不放宽恢复超时或日志门槛。

`HOLLOWCONNECT_FSM_HIERARCHY_CHECK=1` 另运行独立登记根脱离父体的快照/接管夹具，要求副本先脱离、休眠源保持停用，接管后父体和世界坐标正确，并实际完成原生 local-space tween。配合八轮实战时，还要求 Needle 在原生 `Throw Recover` 时回到发射位置附近；仅发出通知而飞到错误坐标也拒绝。`HOLLOWCONNECT_FSM_CONTINUATION_ONLY=hierarchy` 可单跑这项。

`HOLLOWCONNECT_FSM_ENTRY_CHECK=1` 在真实原生 StateChanged / OnEnter 边界让独立发送线程尝试正常发送锁，拒绝“父体新状态已发布，原生子体激活尚未执行”的部分入口；同时要求完整入口结束后子体激活已发布。`HOLLOWCONNECT_FSM_CONTINUATION_ONLY=entry` 可单跑此项。与前三组夹具共八项；旧构建即使随机八轮实机通过，也不能覆盖这项确定性失败。

## 救援反馈专用探针

`HOLLOWCONNECT_SMOKE_MODE=rescue` 使用两个隔离客户端与真实 UDP 连接，交替由主机/客机用实际骨钉命中救援暗影，验证只有服务器确认的救人者增加 33 灵魂、受救者一血不获奖励、满槽/储备/个人灵魂限制以及旧暗影记录保留。通过生产接收函数注入合成事件，检查名单先于救援、重复确认、旧死亡、错误会话/轮次和全队重试中的拒绝。暂停时检查原版 Death/Death Glow、粒子波、两段音效资源与原生混音通道以及 2.5 秒清理。`shade-inspect` 仅在未保存新角色中反序列化原版素材以查看资源，不执行暗影回收 FSM。

所有模式统一安装 `ProbeLogGuard`，引擎 ERROR、模组异常或 Spawn expired 立即使本轮失败。运行器和汇总器调用 `Assert-ProbeLogs.ps1` 扫描全部独立日志；测试完成后的失败标记仍有效。`tests/LogGate.Tests.ps1` 验证十四种日志门槛情形，包括异常堆栈的空行/新时间戳边界。最终正式组使用双方断开并等待服务器确认的结束屏障。

假骑士迟入场用 `HOLLOWCONNECT_SMOKE_BOSS_LATE=1` 在露头后延迟 2.2 秒加载；`HOLLOWCONNECT_SMOKE_HANDOFF_DELAY` 另延迟旧权威离房。原生露头只有 5 秒，测试必须允许其自然超时，按实际阶段检查地形、血量与继续战斗，再用原生伤害驱动第二次露头，不强设 FSM。可选 `HOLLOWCONNECT_SMOKE_TAKEOVER_TRACE=1` 只在测试插件中记录接管前后子物体身份与原生 Activate 动作。

`HOLLOWCONNECT_SMOKE_DAMAGE_TRACE=1` 将第二次破甲后的血量、FSM、动画和刚体轨迹先保存在内存，等待结束后写入各角色的 `.damage.log`，失败时同样保留。`HOLLOWCONNECT_SMOKE_DAMAGE_STATE` 可让这次诊断等待指定原生战斗状态再施加伤害，不设置 Boss 状态，不改变露头断言或其超时；不指定时保持原触发条件。诊断重跑通过不能解除先前原因未知的失败。

`HOLLOWCONNECT_COMBAT_SNAPSHOT_CHECK=1` 在 FSM 夹具入口增加真实 Unity 碰撞体/子物体检查：从生产 CombatStateComponent 捕获两份快照，经实际序列化、序列回绕与接收过滤倒序交付，随后接管，要求原生和副本碰撞体、子物体仍保留新值。与 `HOLLOWCONNECT_FSM_CONTINUATION_CHECK=1` 一起使用；`HOLLOWCONNECT_FSM_CONTINUATION_ONLY=combat` 只运行此夹具。它不证明其他 GenericData 组件或初始大快照的顺序已受保护。

`coverage` 的 `HOLLOWCONNECT_COMBAT_SNAPSHOT_ASSERTIONS=1` 可运行同一独立夹具；再设 `HOLLOWCONNECT_SCENE_SNAPSHOT_ASSERTIONS=1`，增加房间初始快照/接管快照与实时更新交错的四种检查。真实 CombatState 字节经序列回绕、可靠重发和重复原包进入生产接收过滤，要求显示对象、原生/副本碰撞体和子物体不回退，之后执行生产接管。它验证真实 Unity 对象上的合成包顺序，不冒充自然复现用户旧断线或地图故障；普通网络回归另覆盖完整 NetClient 分片接收、主线程分发及稀疏 FSM 字段。

飞针回收的坐标断言从原生 Throw 的世界坐标 SetPosition 动作读取并保存本轮发射目标；原生 Throw 随后缩小 Boss 碰撞体，Boss 可在飞针返回期间下沉，不能将完成时的 Boss 坐标当成发射点。日志同时记录发射目标、返回实际值及完成时 Boss 坐标；仍要求飞针无父物体且距原始目标不超过 1 单位，未放宽距离或完成超时。

格林运动夹具每帧维持原定固定无敌靶子在场内 `(80,8)`，防止零重力靶子被固体碰撞推出场外，令原生 AD Pos 永远无合法落点。靶子还须距气球中心 `(85.77,13.5)` 至少 6 单位；先前 `(85,8)` 导致原生 Balloon Retry，因此该轮失败保留。此条件仅约束测试角色，不修改 Boss 的 FSM 或位置。

`HOLLOWCONNECT_CONTINUATION_PACKET_TRACE=1` 仅在隔离 Pantheon 探针中记录原始发送及合并接收的可靠 active/FSM 状态、序列号、实体 ID 和 visit。追踪通过临时 detour 观察生产包，不修改包内容或测试断言，用于区分未送达与旧重发覆盖。

`Summarize-Selective-Ack-Recovery.ps1` 锁定 `44ae5d1` 构建、142 项回归、14 项日志门槛、九组当前构建运行及一组旧发送器诊断。要求先停止隔离客户端并停用 Smoke，再核对 DLL 和复扫日志；两组独立周期入场必须双方通过全部三次房间检查。完整接管压力失败仍记录为拒绝，不因入场通过、后续重跑通过或旧发送器诊断通过而解除。它输出专项证据，不授予发布通过。

梦之钉剧情平台专项复用 `coverage`：设置 `HOLLOWCONNECT_COVERAGE_SCENES=Dream_Nailcollection`、`HOLLOWCONNECT_COVERAGE_ENTRY_GATE=door_dreamReturn`、`HOLLOWCONNECT_COVERAGE_SECONDS=1`、`HOLLOWCONNECT_DREAM_PLATFORM_ASSERTIONS=1`。该专项开启共享进度，确认首次守梦人梦境仍没有网络实体或平台副本，迟到注册也不能接管平台。等待原生入口就绪后，双方轮流通过真实碰撞靠近、自然下落站立并离开；要求各自平台显隐独立，队友靠近不会改变本机平台。探针不调用 Show/Hide，不设置平台 FSM；不代表完整剧情或真实 FRP 已验收。2026-10-15 的同步人数/接管探针已由这项本地行为检查替代，历史结果保留。

`HOLLOWCONNECT_WARP_ASSERTIONS=1` 在 `Fungus1_02` 或 `Dream_Nailcollection` 验证真实窄平台上的 F8 落点。双方各传送三次，`HOLLOWCONNECT_WARP_CROSS_ROOM=1` 使第一次跨 Town，后两次同房；苍绿场景另用真实酸水碰撞检查复位点、复位期间拒绝 F8 请求和注入的延迟回复、恢复后两次跳跃落地。`HOLLOWCONNECT_WARP_UNSUPPORTED=1` 再验证无支撑目标：同房不移动角色或复位点，跨房回到可操作的原版入口。落点断言使用真实角色碰撞体和地形，死亡复位走原生流程；延迟回复是探针注入，并非真实网络故障测试。

水晶翻转平台本地运行专项：`coverage` 中设置 `HOLLOWCONNECT_COVERAGE_SCENES=Mines_35`、`HOLLOWCONNECT_LOCAL_FLIP_ASSERTIONS=1`。要求真实场景 21 个平台均未注册为实体且没有复制；双方轮流实际落在同一平台上，触发原生翻转、尖刺切换及自然恢复，另一端的平台须保持原位。`HOLLOWCONNECT_ROOT_ANIMATION_ASSERTIONS=1` 在 `Ruins2_04` 另要求两端原生大盾守卫动画至少出现三个不同精灵帧，拒绝通过停动画减少流量。二者可配合既有性能计数，对比修改前后的整房 CombatState 生成量；初次/接管完整快照和命中盒仍用现有专项验证。

Boss 击败后解除暗影专项：`Run-Boss-Victory-Suite.ps1 -Prefix <新目录前缀>`。普通格鲁兹之母／神居格鲁兹之母与假骑士均交换先入权威，另有房主关闭选项对照和空洞骑士尸体／真实聚焦结局。使用 `-Only health-host,armour-guest` 等限制范围；`-NoBuild` 复用已准备的 Smoke。普通 `rescue` 模式的入口开关为 `HOLLOWCONNECT_BOSS_VICTORY_CHECK=1`，个案 `HOLLOWCONNECT_BOSS_VICTORY_CASE=health|armour|godhome`；`HOLLOWCONNECT_BOSS_VICTORY_AUTHORITY=guest` 交换权威，`HOLLOWCONNECT_BOSS_VICTORY_DISABLED=1` 检查关闭。结局仅以 `HOLLOWCONNECT_FINAL_CASE=continuation`、`HOLLOWCONNECT_FINAL_BOSS_RESCUE=1` 从原生死亡边界开始，不能作为整场最终 Boss 战斗或音乐验收。测试保留每组 DLL、探针哈希及原始失败，结束断开 UDP 后仅关闭隔离进程，再执行严格日志检查。

关门精英战结算专项：`Run-Local-Battle-Suite.ps1 -Prefix <新目录前缀>`，默认 8 组，`-Only together-host,partial-guest` 可限定范围，`-NoBuild` 复用 Smoke。`together` 双方先触发本地战斗；`partial` 第一只巨怪死后队友进房；`late` 两只死后进房；`outside` 队友已在同房间、但等两只怪死后才进入本地触发器。各自交换房主／客机担任场景权威。开房前启用永久进度共享，使用 `Fungus2_05/right1` 原版入口和真实碰撞触发 Battle Control，经原版伤害击杀、原版 Shiny 检查流程领奖，并要求双方真实走过左右两道门。后领取者先等待共享护符槽到账，再走本地领取流程，余额仍只能是 4；`outside` 另以探针注入重复死亡快照，检查触发前／结算后的敌人数不变。版本和覆盖边界见 [LOCAL-BATTLE-COMPLETION-2026-10-19.md](https://github.com/gengsha/HollowConnect/blob/c1a6d16fb5cf14b1f8470bf8c8b18b8f74349a2b/docs/LOCAL-BATTLE-COMPLETION-2026-10-19.md)；不是全部关门战或完整战斗操作的验收。

关门战改动风险补测：`Run-Local-Battle-Risks.ps1 -Prefix <新目录前缀>`，显式运行本轮 11 组；`-Only boundary,reentry-host,disconnect-guest,phase-handoff,mother-host,waves-guest` 等可限制范围，`-NoBuild` 复用 Smoke。涵盖死亡重置和后续计数初始化边界、未触发先离房重入、第一只死亡后权威断线及同房重连、假骑士原版破甲／头部接管、格鲁兹之母真实挥刀唤醒与七只小怪，以及青铜竞技场完整 28 波和奖励防重。`boundary` 中临时接收器夹具的正血重置不代表完整原版复活流程；母体战必须实际进入 Start，再等小怪到达战场，不能以序列化的正敌人数或场外模板当作已经开战。原先日常套件仍默认 8 组。详情与未验收夹具记录见 [LOCAL-BATTLE-RISKS-2026-10-19.md](https://github.com/gengsha/HollowConnect/blob/c1a6d16fb5cf14b1f8470bf8c8b18b8f74349a2b/docs/LOCAL-BATTLE-RISKS-2026-10-19.md)。
# 队友显示与 Boss 人数难度专项

菜单补全专项：构建一次 Smoke 后，显式设置 `$env:HOLLOWCONNECT_DISPLAY_MENU_CHECK='1'`，运行 `Run-Two.ps1 -Mode boss -NoBuild -RunName <新目录>`。此分支只在德特茅斯验证三个菜单项、默认值和旧 JSON、自定义透明度、两端显示独立、已有队友/脱离角色的法术即时透明与恢复、名字即时开关、房主难度设置权与开房时规则、配置序列化，随后正常断线停服；不会进入假骑士战斗。停止隔离进程后运行严格日志检查。

显式设置 `$env:HOLLOWCONNECT_BOSS_BALANCE_CHECK='1'` 后运行 `Run-Two.ps1 -Mode boss -NoBuild`，另设 `$env:HOLLOWCONNECT_SMOKE_BOSS_LATE='1'` 覆盖暴露头部后迟入。先运行 `scripts/Build.ps1 -Target Smoke -NoRestore` 一次；两组使用不同 RunName，结束后停止隔离进程并执行 `Assert-ProbeLogs.ps1 -RequireRuntimeFiles`。普通 Boss 探针默认不启用本轮附加检查。

Boss 真正死亡后普通怪伤害专项：显式设置 `HOLLOWCONNECT_POST_BOSS_DAMAGE_CHECK=1`、`HOLLOWCONNECT_LOCAL_BATTLE_CHECK=mother`，运行 `Run-Two.ps1 -Mode rescue -NoBuild`；分别以 `HOLLOWCONNECT_LOCAL_BATTLE_AUTHORITY=host` / `guest` 运行。实际唤醒并击杀母体、等待原生七只小怪后，检查普通怪骨钉／法术／玩家持续伤害全额扣血与副本 HP 收敛、玩家攻击属性不变、离房清空 Boss 登记、断线倍率为 1。精确伤害用原生接收入口给定数值，不等于整场操作和三种攻击碰撞均已覆盖。默认不自动加入其它套件。

苍绿之径藤蔓石块本地化专项：显式设置 `HOLLOWCONNECT_LOCAL_VINE_CHECK=1`，运行 `Run-Two.ps1 -Mode rescue -NoBuild`。先构建一次 Smoke；永久进度共享开启，双方进入真实 `Fungus1_06/left1`，轮流以本机原生攻击砍落石块，另一端保持悬挂。远端碰撞边界使用真实 HKMP 刀光 prefab 显式触发原生接收入口，不冒充完整网络攻击链路。另检查 19 项持久化映射禁止共享、原索引保留，以及旧掉落记录在客户端接收入口不能覆盖本地 SceneData。双方完成后关闭隔离进程并执行严格日志检查；详情和覆盖限制见 [LOCAL-VINE-PLATFORMS-2026-10-19.md](https://github.com/gengsha/HollowConnect/blob/c1a6d16fb5cf14b1f8470bf8c8b18b8f74349a2b/docs/LOCAL-VINE-PLATFORMS-2026-10-19.md)。

入队进度核对专项：先构建一次 Smoke，再设置 `HOLLOWCONNECT_PROGRESS_AUDIT=join` / `full` / `off`，分别以独立 RunName 执行 `Run-Two.ps1 -Mode progress -NoBuild`。`join` 覆盖互补能力与护符合并、旧缺槽/缺碎片修复、已共享碎片真实碰撞和原生二段跳过场中队友真实重连；`full` 覆盖全进度新人、39 个资源来源、原生碎片合成、已消费矿石不返还，以及原生重复护符领取；`off` 是共享关闭的负对照。全进度场景允许原版碎片 UI 串行播放，不能沿用短商店同步屏障。`HOLLOWCONNECT_PROGRESS_AUDIT_INSPECT=1` 仅导出拾取场景结构，不作为验收。结束后停止隔离进程并执行 `Assert-ProbeLogs.ps1 -RequireRuntimeFiles`，生产哈希与覆盖限制见 [PROGRESS-JOIN-AUDIT-2026-10-19.md](https://github.com/gengsha/HollowConnect/blob/c1a6d16fb5cf14b1f8470bf8c8b18b8f74349a2b/docs/PROGRESS-JOIN-AUDIT-2026-10-19.md)。

`HOLLOWCONNECT_PROGRESS_AUDIT=charm` 是护符短续测：保留真实初次加入、并集和旧档补偿，跳过碎片拾取场景及二段跳场景，只运行原生护符交互。不能把该模式单独当作完整入队专项通过。

`HOLLOWCONNECT_PROGRESS_AUDIT=nocharms` 检查房主仅关闭 `ShareCharms` 时，能力和非消耗资源仍合并，护符拥有状态、总数及 `hasCharm` 解锁标记保持个人独立。

`halves-charm` 是两种国王之魂半片合并为完整状态的短续测；`forty-charm` 是只给新人全套护符、资源保持普通进度的短续测。二者均保留真实入队/旧档补偿和原生普通护符重复领取，不包含二段跳过场。

虚空之心费用专项：设置 `HOLLOWCONNECT_ROYAL_CHARM=host|guest|stale|halves|nocharms`，并设置 `HOLLOWCONNECT_PROGRESS_AUDIT=royal-charm`（`nocharms` 用例改为 `nocharms`），执行 `Run-Two.ps1 -Mode progress -NoBuild`。用隔离内存夹具和真实 UDP 覆盖双向进度、旧错误状态修复、国王之魂费用及关闭共享；实际调用原版占槽计算与过度装配刷新，检查个人装备列表。装备操作由夹具建立，不作为护符菜单交互、变身过场或私人存档写盘重读的验收。需要双端完成及严格日志通过。

菜单选档资源专项：先用 `scripts/Build.ps1 -Target Smoke -TestSelection tests/progress-audit.selection.txt` 构建一次，再执行 `tests/Smoke/Run-Menu-Resources.ps1 -Prefix <全新前缀> -PayloadPath src/HollowConnect/bin/Release/net472`。默认 `new,stale` 两组均让房主先核对无来源标记的满资源档；客机保持原生菜单，关闭 `_autoConnect`，在真实 UDP 收齐 39 个来源后才原生新建档，经开场进入 Tutorial。`stale` 额外模拟菜单中上一档已拥有全部回执；该条件是内存夹具，新档载入及 `PlayerData` 替换走原生流程。随后检查完整结算、4 类左下角原版提示、无远端合成 UI、骨钉独立及消费后的实际重连去重。`-Cases new` 可单独复现旧版交付包；失败和严格日志结果必须保留，不作为验收通过。保存仍被拦截，不读写私人存档；未覆盖原生磁盘旧档 LoadGame、真实双机或 FRP。

粪球守卫者挡板专项：`Run-Defender-Barrier.ps1 -RunName <新名称> -Case outside|late -First host|guest -NoBuild`，先构建一次 Smoke，各组必须等待上一组完整退出再启动。`outside` 以原版碰撞触发开战，检查门外队友不受远端关门影响、可实际入场并自行触发；`late` 等原生尸体结算后才进房，再碰触发区也不能重新开战。双方均实际横穿左右门洞；角色免伤且在门洞高度关闭重力，不代表自然平台路线通关。`inspect` 只导出场景结构，不算功能通过。`One Way Wall/break_floor` 是永久捷径墙，探针要求保留进度同步。证据及边界见 [DUNG-DEFENDER-BARRIERS-2026-10-20.md](https://github.com/gengsha/HollowConnect/blob/c1a6d16fb5cf14b1f8470bf8c8b18b8f74349a2b/docs/DUNG-DEFENDER-BARRIERS-2026-10-20.md)。

本地世界模式专项：先构建一次 Smoke，再以 `Run-Local-World.ps1 -RunName <新名称> -Case world|different-host|different-guest -First host|guest -NoBuild` 运行。`world` 检查房主模式覆盖访客相反偏好和完整存档同步请求、普通怪登记与远端刀光/法术隔离、真实本机攻击和捷径墙、双方各自原生二段跳过场及面具碎片、断线重连和切回共享后的普通怪登记。`different-*` 让指定一方预先击败粪球守卫者，交换先入顺序；另一人必须保留本地 Boss、实际触发开战，并由原生死亡逻辑完成。终结伤害由测试注入，不是完整战斗操作验收。每组核对严格日志和生产 DLL 哈希，不写个人存档。

当前 Pantheon 回归按本地镜头锁区检查：锁区必须存在、没有网络实体或网络所有者，随后仍检查镜头恢复、Boss/队友可见和操作状态。旧版“镜头锁区必须注册为实体并比较双方启停”的夹具已过时，不用于当前挡板本地化后的验收。

斯莱商店同来源碎片专项：先用 `scripts/Build.ps1 -Target Smoke -NoRestore` 准备 Smoke，再运行 `Run-Shop-Shards.ps1 -Prefix <新前缀> -PayloadPath <包含四个生产 DLL 的目录>`。默认八组：面具／容器碎片各自的双方确认框打开后等待共享来源、共享已结算后再购买、双方同时确认，以及 `world-mask` / `world-vessel` 的真实世界来源先共享再碰撞；初始碎片接近合成阈值。`-Cases settled-vessel,queued-vessel` 限定场景，`-Initial zero` 从零碎片开始，检查共享后仍有余数的重复领取。真实商店入口、库存、确认、扣费、奖励与退出均使用原版 FSM，只注入选项和按键；世界来源使用真实碰撞和场景回执。角色为禁止写盘的内存档、网络为 localhost UDP。检查原版首次显示、重复领取跳过计数展示及原生收尾，记录实际 DLL/探针哈希和严格日志结果；脚本执行完毕不代表通过。修改前复现见 [SHOP-SHARD-STALL-AUDIT-2026-10-20.md](https://github.com/gengsha/HollowConnect/blob/c1a6d16fb5cf14b1f8470bf8c8b18b8f74349a2b/docs/SHOP-SHARD-STALL-AUDIT-2026-10-20.md)，修复与当前验证见 [SHOP-SHARD-STALL-FIX-2026-10-20.md](https://github.com/gengsha/HollowConnect/blob/c1a6d16fb5cf14b1f8470bf8c8b18b8f74349a2b/docs/SHOP-SHARD-STALL-FIX-2026-10-20.md)。

梦战士个人历史回归：先 `scripts/Build.ps1 -Target Smoke`，再 `tests/Smoke/Run-Dream-Warriors.ps1 -Prefix <全新前缀> -PayloadPath <固定生产payload> -Cases guest -Scenes Fungus1_35 -Summoner host -GuestHistory 2`。`GuestHistory=0/1/2` 分别是未战胜／战胜未领奖／已领奖，入房前在禁止保存的内存角色准备，混合历史要求未战胜主机发起挑战。已领奖客机战斗中不交互，主机领奖后检查墓碑、实际对话文本、输入恢复、精华不重复和过时状态去重；`RestingGrounds_02` 可作泽若对照。测试实际调用原生交互 FSM 和 HealthManager.Hit，使用 localhost UDP，不是双机 FRP 或完整招式实战。原始失败、首轮修复失败及最终哈希见 [已领取客机专项](https://github.com/gengsha/HollowConnect/blob/c1a6d16fb5cf14b1f8470bf8c8b18b8f74349a2b/docs/NO-EYES-COLLECTED-2026-10-21.md)。

救援次数／本场失败／观战专项：构建一次 Smoke，再运行 `Run-Rescue-Rules.ps1 -Prefix <全新前缀> -NoBuild`；默认五组功能检查，另以 `-Only native-control` 检查关闭救援与观战的原版死亡对照。使用真实原生死亡、实际骨钉碰撞、原生长椅输入和 localhost UDP，核对赶路玩家不被牵连、成功救起扣次数、死亡返回不补次数、实际坐椅恢复，以及救援关闭仍可观战。内存角色预置 `visitedDirtmouth=true`，首次城镇标题不在本专项范围；所有保存入口被拦截。

辐光观战镜头专项：`Run-Radiance-Spectator.ps1 -Prefix <全新前缀> -NoBuild`，可用 `-Only story-host,story-guest,absolute-host,absolute-guest` 筛选。原生阶段推进到最终攀升前让指定一方真实死亡观战，再逐级把存活角色放在原生台阶上、使用原生跳跃输入与物理，观察者每级采样 12 帧。检查剧情 15／无上 18 级台阶、队友持续在视口内、镜头高度范围和 F6 原版返回后的控制／物理／镜头恢复；首尾实际渲染 PNG。夹具不设置观战镜头或观战者坐标，不等于连续手动攀升通关。每组记录受测生产和探针哈希、严格日志，必须串行复用现有两份隔离副本。

Smoke 同时拦截原生 SaveGame 三个入口与 `Mod.SaveGlobalSettings`，后者防止正常 Application.Quit 将临时房主规则写入玩家全局偏好。旧运行在加入全局保存拦截之前确实产生过偏好副作用，不能追溯称其完全隔离；见救援规则报告。生命周期／救援用例须显式准备所需次数和观战设置，避免继承本机偏好。Boss 胜利探针须等原生入口完全结束，存活角色保持输入就绪，以便实际报告 Alive。

个人剧情触发专项使用 `StoryCutsceneChecks.cs`：`HOLLOWCONNECT_STORY_SCENE` 选择三个 `Dream_Guardian_*`、`Dream_Abyss` 或 `White_Palace_09`；`STORY_GATE=right1` 是白宫王座的原生入口。`STORY_STAGE=remote-attack` 用真实原生攻击复现远端刀光误触；`progress` 继续守梦人原生聚集与共享标记/计数，卢瑞恩还要求较慢玩家再次亲自吸收不重复计数；白宫则检查自己的原生遗体破坏与掉落。`peer-trigger` 只用于出生地真实远端身体重叠，不能视为完整剧情完成。运行选择与限定验收见 [剧情触发修复](https://github.com/gengsha/HollowConnect/blob/c1a6d16fb5cf14b1f8470bf8c8b18b8f74349a2b/docs/STORY-CUTSCENE-FIX-2026-10-24.md)。

梦境 Boss 前置跑酷专项使用 `DreamTraversalChecks.cs`：设置 `HOLLOWCONNECT_DREAM_TRAVERSAL_SCENE`、`HOLLOWCONNECT_DREAM_TRAVERSAL_STAGE=schema/local`；local 还需用 `HOLLOWCONNECT_DREAM_TRAVERSAL_BASELINE` 指定修改前的平台数量及 Boss 实体基线 JSON。双方使用同一抽样顺序，等待时固定角色位置，落地时恢复真实物理；检查无副本/所有者、迟到不接管、独立原生显隐与真实落地、Boss 登记不变。范围和证据见 [前置跑酷修复](https://github.com/gengsha/HollowConnect/blob/c1a6d16fb5cf14b1f8470bf8c8b18b8f74349a2b/docs/DREAM-TRAVERSAL-LOCAL-2026-10-24.md)，不代表完整战斗验收。

梦境Boss修复专项：构建一次Smoke，再运行 `Run-Dream-Variants.ps1 -Prefix <全新前缀> -PayloadPath <生产DLL目录> -Scenes Dream_01_False_Knight -DamageCheck`，检查真实开战后本体／头部登记与原生17伤害收敛。`-LifecycleCheck` 包含伤害检查并继续原生多轮破甲、露头、最终死亡、个人返回与起身输入，随后与未领奖主机的真实灵魂对话领取300精华；已领奖客机应保持230精华、无可交互灵魂。场景入口仍是测试直切，不是梦钉入梦；致命伤害及结束对话输入由探针提供，不代表手动全场战斗。

戈布离房回归：`Run-Dream-Warriors.ps1 -Prefix <全新前缀> -PayloadPath <生产DLL目录> -Cases both -Scenes Cliffs_02 -Summoner host -GuestHistory 2 -ExitMode native`，另用独立前缀运行 `-ExitMode f8`。`both` 表示双方预置共享提灯；仍是未领奖主机／已领奖客机。native将角色放进真实出口触发区，f8使用运行时按键入口跟随已离房主机。两者要求原生目标房间进入完成、控制正常且旧附加场景卸载。默认 `-ExitMode none` 不额外离房；连续 `-Scenes Cliffs_02,Fungus2_32` 可检查切房后继续胡长老。每组必须完整停止退出后再运行下一组。

通用Probe的Freeze必须保留原生输入就绪：永久能力与资源在原版UI、对话或过场期间等待发放，主动RelinquishControl会让螳螂爪断言一直等待。不能通过绕过生产Idle判断修复此类测试。原生梦境返回的Dream Return/Ready等待起身按键，精华由返回房间的灵魂对话发放；探针必须实际完成这些步骤。

Boss倒地索敌专项：构建一次Smoke，再运行 `Run-Boss-Targets.ps1 -Prefix <全新前缀> -NoBuild`，默认主机／客机分别先入房成为权威，`-Authority host` 或 `guest` 可限定。真实进入 `GG_Hornet_1`，自然出招，权威实际死亡；观察原生角度和朝向在存活队友换边后是否继续正确，随后经真实网络救起并检查原生目标恢复。各段要求三次自然朝向读取，换边后的倒地阶段还要求实际攻击角度（最多等120秒）；所有期间采到的角度均须正确，不强设Boss状态。另查目标资格、原生副本／无实体归属动作隔离，以及FSM不持久保存远端角色。禁止保存的内存角色、localhost UDP；记录生产／探针哈希与严格日志，结束后禁用测试插件。普通四轮骨钉救援仍用无此环境变量的 `Run-Two.ps1 -Mode rescue -NoBuild` 单独回归。见[索敌报告](https://github.com/gengsha/HollowConnect/blob/c1a6d16fb5cf14b1f8470bf8c8b18b8f74349a2b/docs/BOSS-SHADE-TARGETING-2026-10-21.md)。

巨型苔藓冲锋者回椅专项：rescue 模式加 `HOLLOWCONNECT_MOSS_RETARGET=fixed`，`HOLLOWCONNECT_MOSS_AUTHORITY=host` / `guest` 选择初始 AI 权威；默认让该玩家真实死亡回 Town，队友接管后观察自然攻击，并验证实际范围离开/重入。`HOLLOWCONNECT_MOSS_VICTIM=remote` 改为远端已选目标死亡、原权威留场；`HOLLOWCONNECT_MOSS_NATIVE=1` 使用原版索敌，其余默认灵活。`schema` 只预检原生 GG 场景结构；`baseline` 只记录旧 DLL 观测，不把停滞当作通过的产品行为。正式用例进入 `Fungus1_29`，不强制 Boss 状态。证据与固定名单见[修复报告](https://github.com/gengsha/HollowConnect/blob/c1a6d16fb5cf14b1f8470bf8c8b18b8f74349a2b/docs/MOSS-RETARGET-FIX-2026-10-26.md)。

流程暗影路线专项：构建一次 Smoke，再运行 `Run-Story-Rescue.ps1 -Prefix <全新前缀> -NoBuild`。默认双向房间权威，双方交换倒地；`-Only mother,knight,tyrant,kin,defender,ogres,acid,moving,vanishing,retry,rejected,timeout,blocked,revive-loss` 可跑完整专项。死亡前允许位置准备和致命伤害注入；死亡后救援者仅用原生移动输入和实际骨钉碰撞，不传送至暗影、不注入命中、不拆门。为隔离路线，屏蔽额外战斗接触伤害；酸水分支保留真实原生致命碰撞。移动／消失支撑及网络故障明确为注入。`timeout`、`blocked`、`revive-loss` 检查返回兜底，只让客机倒地并交换房间权威；其余用例均交换倒地者。旧 `HOLLOWCONNECT_HAZARD_RESCUE_CHECK=1` 仅保留神居原检查点规则，流程酸水由新路线专项覆盖。测试范围与局限见[整改报告](https://github.com/gengsha/HollowConnect/blob/c1a6d16fb5cf14b1f8470bf8c8b18b8f74349a2b/docs/STORY-RESCUE-FIX-2026-10-21.md)。

## 银行家藏匿地对话专项

先构建 Smoke，使用 progress 模式与 `HOLLOWCONNECT_BANKER_CASE=dialogue`；`HOLLOWCONNECT_BANKER_TALKER=host` 或 `guest` 选择交谈者。仅检查原生结构时使用 `HOLLOWCONNECT_BANKER_CASE=inspect`。两端内存中准备银行家盗款和余额；ProbeMod 禁止保存。实际进入 Ruins_Bathhouse，触发交谈范围和向上输入，对端真实挥刀，通过复制刀光与银行家的碰撞验证首次、再次对话及原生结束，最后验证对话后本机攻击和原生掉钱。复现及最终哈希记录见 docs/BANKER-DIALOGUE-FIX-2026-10-25.md。

## 假骑士两人／三人铁门专项

构建一次 Smoke，以 rescue 模式运行。设置 `HOLLOWCONNECT_FK_GATE_CHECK=standard|late-trigger|handoff|schema`、`HOLLOWCONNECT_FK_SHARED=0|1`、`HOLLOWCONNECT_FK_AUTHORITY=host|guest`；`Run-Two.ps1 -Players 2|3 -NoBuild -RunName <新目录>` 选择人数。三端使用现有 guest 副本启动第二个独立进程，各端独立身份、日志、结果；所有 DLL 在任何游戏启动前准备，启动按探针加载边界串行。三端选项目前用于此人数感知夹具，其它专项仍按既有双端范围执行。

用原生触发器、HealthManager 破甲／露头伤害走完整演出，终局要求每端本地 Battle Enemies=0、Battle Control=End、五处原生门无阻挡碰撞，并用真实水平输入逐一穿门。`schema` 只检查三端场景结构、预战计数及第一次破甲，不能作通关验收。`late-trigger` 让最后一名角色在终局后才触发本地战斗；`handoff` 在第一次破甲后让权威离场再回来。可设置 `HOLLOWCONNECT_NETWORK_FAULTS=1` 检查真实数据报丢失、重排和重复。使用新鲜内存角色，禁止保存，不强制 Boss FSM 状态、不发送开门事件。持久运行器与本次选择见 `.analysis/false-knight-gate-fix-20261026/`。
