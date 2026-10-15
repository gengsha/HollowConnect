# 测试专用 Unity 探针

此项目不进入运行安装包，不能放在日常游戏的 Mods 目录中。

`progress` 模式用无存档写入的双客户端检查遗忘十字路鹿角站共享：购买者由 `HOLLOWCONNECT_PROGRESS_BUYER=host|guest` 选择，`HOLLOWCONNECT_PROGRESS_PROMPT=1` 让接收者正在原生购买对话内。探针程序化进入原生交互入口，随后通过原生 Yes/No 控制 FSM 的选择事件支付，保留真正的 TakeGeo、授权和开站动画。断言当前房间无需重新加载即可使用、仅购买者扣 50 吉欧、对话结束、控制恢复、重复进度 offer 不改变钱或原生动作。`HOLLOWCONNECT_PROGRESS_BASELINE=1` 在旧实现上确认接收者还能再次付费并明确失败；不能计入验收。之后实际购买科尼法首张地图并跨 Crossroads_08、Crossroads_30 检查双端角色/快照，`HOLLOWCONNECT_MAP_BOTH=1` 覆盖双方购买；地图和吉欧仍为个人数据。`HOLLOWCONNECT_PROGRESS_INSPECT=1` 只导出原生购买 FSM，不是购买验收。成功路径使用已有 ProbeShutdown 完成网络退出后才写终态。

所有测试模式创建 SilentProbe，启动及每帧将 AudioListener.volume 设为 0；只静音隔离测试进程，不修改用户音量配置。

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

飞针回收的坐标断言从原生 Throw 的世界坐标 SetPosition 动作读取并保存本轮发射目标；原生 Throw 随后缩小 Boss 碰撞体，Boss 可在飞针返回期间下沉，不能将完成时的 Boss 坐标当成发射点。日志同时记录发射目标、返回实际值及完成时 Boss 坐标；仍要求飞针无父物体且距原始目标不超过 1 单位，未放宽距离或完成超时。

格林运动夹具每帧维持原定固定无敌靶子在场内 `(80,8)`，防止零重力靶子被固体碰撞推出场外，令原生 AD Pos 永远无合法落点。靶子还须距气球中心 `(85.77,13.5)` 至少 6 单位；先前 `(85,8)` 导致原生 Balloon Retry，因此该轮失败保留。此条件仅约束测试角色，不修改 Boss 的 FSM 或位置。

`HOLLOWCONNECT_CONTINUATION_PACKET_TRACE=1` 仅在隔离 Pantheon 探针中记录原始发送及合并接收的可靠 active/FSM 状态、序列号、实体 ID 和 visit。追踪通过临时 detour 观察生产包，不修改包内容或测试断言，用于区分未送达与旧重发覆盖。

`Summarize-Selective-Ack-Recovery.ps1` 锁定 `44ae5d1` 构建、142 项回归、14 项日志门槛、九组当前构建运行及一组旧发送器诊断。要求先停止隔离客户端并停用 Smoke，再核对 DLL 和复扫日志；两组独立周期入场必须双方通过全部三次房间检查。完整接管压力失败仍记录为拒绝，不因入场通过、后续重跑通过或旧发送器诊断通过而解除。它输出专项证据，不授予发布通过。
