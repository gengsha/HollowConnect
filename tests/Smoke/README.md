# 测试专用 Unity 探针

此项目不进入运行安装包，不能放在日常游戏的 Mods 目录中。

所有测试模式创建 SilentProbe，启动及每帧将 AudioListener.volume 设为 0；只静音隔离测试进程，不修改用户音量配置。

0.1.13 增加房间生命周期专项：`HOLLOWCONNECT_LIFECYCLE_CHECK=1` 在真实苔藓冲锋者显示副本执行原生持续音效动作，控制其音源先销毁，然后击杀权威 Boss 并沿原生行程进入大黄蜂；验证旧实体清除、唯一模拟权、Boss / 队友可见、镜头锁一致，并在大黄蜂房内断线重连再次检查。原始 0.1.12 DLL 在该销毁顺序下清理抛错、后续登记失效。故障注入不是完整自然游玩复现。

`HOLLOWCONNECT_NATIVE_ENTRANCE=1` 让内存角色经实际一门选单、入口动画和对白进入战斗。测试仅启用新角色锁住的门，并调用门选单状态；不强迫任何 Boss FSM。对白通过原生 NEXT 输入事件推进。主客两种先入顺序各跑两轮，完成大黄蜂后进入温泉；大黄蜂另检查双方实际骨钉伤害进入同一权威血量。其他房通过 HealthManager 额外伤害推进，迟转场条件仍由测试 Down 标志控制，不能当作全程实际操控通关。

`Run-Two.ps1` 仅启动已准备的 `.test-results/game-host` 和 `game-guest` 隔离副本，记录实际生产 DLL 哈希；不会创建游戏副本。`Run-Room-Suite.ps1` 顺序执行本轮专项并核对双端终态与最终 DLL。任一失败记录不得计入通过。

`boss` 模式新增脱离层级的子物体断言：露头后 FSM 的子物体引用仍指向同一对原生/副本；移交时仅原生地形块参与碰撞，恢复战斗后地形块全部停用；断线移除实体登记表后，脱离层级的显示副本仍必须销毁。`HOLLOWCONNECT_SMOKE_BOSS_LATE=1` 让客机在原生 Boss 已露头且经过两秒层级刷新后才进房，核对初始快照和接管。`HOLLOWCONNECT_SMOKE_BOSS_CONTROL=stay` 是不移交权威的诊断对照，不属于最终验收。`Summarize-Hierarchy-Diagnostics.ps1` 保留修改前失败和中间构建哈希；正式记录由 `Summarize-Room-Suite.ps1` 核对 12 组最终构建。`Run-Room-Suite.ps1 -Only <case-name>` 可补跑指定场景，汇总仍要求全部记录与当前生产 DLL 一致。

0.1.11 新增 `HOLLOWCONNECT_SMOKE_MODE=death`。两个内存角色先分别验证 Town / Crossroads_47 的真实 RestBench 标记，通过真实 UDP 连接后错开入场。检查就绪前免伤、就绪后的 1.5 秒窗口、恢复正常受伤；普通房间另检查地形危险不扣血但完成原生危险重生。注入列表早于可靠 Down、重复 Down 和复活后旧 Down，核对反馈只一次、旧事件不能覆盖复活、粒子按真实时间清理。

普通模式从真实致命 TakeDamage 倒地，核对无死亡动画、无丢钱、一次本机受击声与有界原生粒子，然后授权救援、一血复活。通过 F6 原生死亡回客机自己的长椅，检查动画、吉欧池、灵魂限制与暗影场景；再进房找到原生 Hollow Shade(Clone)，使用原生 HealthManager.Die 回收自己的吉欧和灵魂。全队失败验证不同个人长椅、原版动画与一次个人处罚。`HOLLOWCONNECT_DEATH_FIRST=host/guest` 交换全队倒地顺序。实际骨钉击破救援暗影和存活者继续造成敌人伤害由完整 Probe 另验，不把直接授权请求当作骨钉碰撞验证。

`HOLLOWCONNECT_DEATH_SCENE=Dream_01_False_Knight/GG_Grimm/White_Palace_01` 切换原版特殊区域，验证相应死亡分支、不掉钱/不生成暗影和个人隐性返回或长椅。梦境使用实际 door1，神居使用实际 door_dreamEnter；先访问 GG_Atrium 并读取原版 Start 重命名后的个人挑战返回门，再进入战斗。`HOLLOWCONNECT_DEATH_SEQUENCE=1` 使用三门内置行程从格林段开始验证全队失败；这不等于完整三门通关。白宫原版采用 Death Dream 动画但走个人长椅返回分支。`HOLLOWCONNECT_DEATH_DISCONNECT=1` 在普通房间接受 F6 正式死亡动画后断线，要求原版死亡仍到达个人长椅、只扣一次吉欧，存活端继续保有房间权威和原轮次。

批处理探针仅对 Respawn、HazardRespawn 与 LoadFirstScene 的渲染帧等待改为下一模拟帧。生产模块不替换这些原生协程。

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
## 救援反馈专用探针

`HOLLOWCONNECT_SMOKE_MODE=rescue` 使用两个隔离客户端与真实 UDP 连接，交替由主机/客机用实际骨钉命中救援暗影，验证只有服务器确认的救人者增加 33 灵魂、受救者一血不获奖励、满槽/储备/个人灵魂限制以及旧暗影记录保留。通过生产接收函数注入合成事件，检查名单先于救援、重复确认、旧死亡、错误会话/轮次和全队重试中的拒绝。暂停时检查原版 Death/Death Glow、粒子波、两段音效资源与原生混音通道以及 2.5 秒清理。`shade-inspect` 仅在未保存新角色中反序列化原版素材以查看资源，不执行暗影回收 FSM。

