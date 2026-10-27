# 五门守望者骑士结算复发：0.1.27 只读审计

本次仍未修复、未关闭。已确认交付版存在会破坏行程上下文的具体路径，但缺少双方故障现场状态，不能认定哪条路径触发了这次故障。没有修改生产源码、游戏、安装包或私人存档，没有启动新的游戏测试。

## 用户确认的复现条件

- 双方版本均为 0.1.27；用户为副机，朋友为开房主机。
- 用户先进入五门守望者骑士房，朋友晚不到一秒；这段时间为用户估计。
- 没有死亡、倒地或 F8，只正常进房并打完。
- 打完后主机回五门入口，副机留在 Boss 房不能动。此前一次为三人全部回入口。

因此死亡、暗影救援、F8 都不是必要条件。原版第五行程共 53 项，守望者为零基索引 31，下一项 32 为 `GG_Uumuu_V`；依据为已有原生行程专项，见 [此前报告](BOSS-MOTION-P5-AUDIT-2026-10-16.md)。

## 审计对象与限制

对照当前 `BossTravel.cs`、`BossRoomSynchronizer.cs`、房间权威与网络快照代码，以及交付包 `E:/projects/hollowConnect/dist/HollowConnect-0.1.27-windows.zip` 内的真实 DLL。使用 Mono.Cecil 只读解析，没有加载 Unity 程序集执行游戏逻辑。

| 对象 | 字节数 | SHA-256 |
|---|---:|---|
| ZIP 内 `payload/HKMP/HKMP.dll` | 1787392 | `CCDDF40DE5052E83CAFE0EA99C7853CE26BCC62516E920AD818E2D05C5FABB25` |
| ZIP 内 `payload/HKMP/HollowConnect.dll` | 147968 | `28BB9DC70EFFA16F20E899C9D3AE0559EDD230B3408DB184C0DB8A5C1BD4E75C` |
| ZIP 内 `payload/HKMP/HollowConnect.Core.dll` | 32256 | `64661526E34F5EE64BD33C343E10AFE823DCC056671564582585D9E8C0A82787` |
| 仓库引用 `vendor/references/Assembly-CSharp.dll` | 4064768 | `5944411BD93830369390A4B51766EE68C4AB26195B299E25A07B5E7D0E00086D` |
| 仓库引用 `vendor/references/PlayMaker.dll` | 212480 | `0EF0E7829D125E1F632C8A189260EC6C6882630BE6932C8C7AE032EFBC53469A` |

用户尚未提供本次故障安装的精确目录、两端 DLL 哈希或双方 F9 导出，因此没有运行要求明确游戏目录的安装审计 helper。上表游戏程序集是仓库引用，不能替代本次实际安装的核验。工作区已有资源同步等未交付改动；交付包与当前源码分别记录，不能用当前构建的通过记录认证交付版。

只读检查了现存 `Player.log`、`Player-prev.log` 的相关片段。前者有 28 条原生 `Boss scene ended`、26 条 `Continuing boss sequence...`，但缺少这些事件的房间名、行程序号及权威身份，无法归属本次故障。启动附近的一条空引用异常也不能归因到守望者结算。当前 `ModLog.txt` 头部明确指向此前隔离 Smoke 运行，排除为本次玩家故障证据。没有复制私人日志内容、玩家名、网络地址或存档。

机器记录：[evidence/p5-watchers-recurrence-2026-10-20.json](evidence/p5-watchers-recurrence-2026-10-20.json)。原生与交付版 IL 摘录分别在 `E:/projects/hollowConnect/.analysis/p5-watchers-recurrence-20261020/reference-sequence.il.txt` 和 `delivered-0.1.27.il.txt`。

## 关键发现

| 编号 / 优先级 | Boss / 触发条件 | 两端分歧与可能后果 | 证据 | 置信度 | 最小运行验证 |
|---|---|---|---|---|---|---|
| P5-01 / P0 | 神居行程中，采集时当前房与 `bossIndex`、`bossIndex - 1` 均不匹配 | 发送端仍保持本地行程；接收端得到空 `BossSequence`，会重置当前行程并配置单独神像战返回。可产生错误退房；具体目标需读取本地返回 FSM | `BossTravel.Capture`、`CaptureContext`、`Prepare`、`ConfigureHall`；交付 IL `Prepare` 的 `IL_00ee` 至 `IL_018e` 包含 Reset 与 ConfigureHall | **confirmed static risk**；与本次故障的因果关系待验 | 保留有效第五行程，只令一次采集的房间/索引不匹配；检查接收端是否退出行程、替换完成回调，记录实际原生目标 |
| P5-02 / P0 | 可靠结算已启动原生延迟协程，旧房间上下文随后到达 | 接收端仍会应用旧上下文、重新建立回滚快照；之后正常换房清理可能恢复旧索引与旧 SetupEvent。权威端已推进而副本可回退或失去下一房回调 | `EntitySnapshotHistory.Filter` 对 `Id == 0` 直接返回；`BossRoomSynchronizer.Receive`、`Tick`、`Clear`；`BossTravel.SaveState` | **confirmed static risk**；实际错误目标/卡住待验 | 在本地真实 `EndBossScene` 启动后、延迟完成回调前，投递一条尚未消费的旧 `BossSceneContext`，记录正常换房前后索引、SetupEvent 与回滚状态 |
| P5-03 / P0 | 静态行程名/约束/门标记正确，但当前控制器未安装行程完成回调 | 接收端 `Prepare` 返回成功，却不修复当前控制器；原生淡出结束后可能无下一房回调而留在原地 | `BossTravel.Prepare` 的 `changed` 与 `sameRoom` 判断；原生 `BossSceneController.Awake`、`BossSequenceController.CheckLoadSequence` | **strong candidate**；需观察实际控制器为何缺回调 | 构造“静态第五行程存在、当前控制器缺原生完成回调”的夹具；确认同步能识别并恰好修复一次，不重复推进 |

### P5-01：采集失败与单独神像战没有被区分

`Capture` 先填充 Boss 等级，再检查行程和当前房是否匹配。如果已有行程但场景/索引不匹配，它直接返回，消息中的 `BossSequence` 仍是默认空字符串。`CaptureContext` 无成功标志，照常把消息序列化为非空字符串，房间同步器也会发送。

接收端把空 `BossSequence` 当成确切的非行程上下文。如果本地正在五门，会先 `RestoreBindings`、`Reset`、清空 `currentBossSequence`；Boss 等级有效时，`ConfigureHall` 移除原行程回调，改挂 `DoDreamReturn`，事件为 `DREAM RETURN`。

这证明有“采集无效 → 对方退出行程”的路径。它尚不证明本次发送了无效消息，也不能仅凭事件名称断言实际目标一定是 `GG_Atrium`。

### P5-02：可靠传输没有替房间上下文建立完成后的保护

通用快照版本过滤明确跳过房间虚拟实体 `Id == 0`，Boss 上下文和完成事件正通过该 ID 传输。可靠批次去重会拒绝已消费批次，但仍允许尚未消费的较老批次；因此不能假定所有旧上下文都已失效。

`completionApplied` 阻止再次排队完成，也阻止重铺布局，但不阻止 `Receive` 把上下文放入 `latest`，`Tick` 的 `latest` 分支也没有完成状态判断。同时，原生 `EndBossScene` 只启动延迟协程：它先等死亡停留、淡出事件和控制状态，最后才执行 `OnBossSceneComplete` 并推进序号。

一条可由代码推出、尚未执行的时序是：

1. 副本消费完成消息，调用原生 `EndBossScene`，立即提交已有临时上下文并标记完成。
2. 一条较老的上下文到达；副本仍应用它，并再次保存序号 31 和当时的 SetupEvent 作为回滚快照。
3. 原生延迟完成回调推进到 32，设置下一场 SetupEvent，开始正常载入乌姆。
4. 场景清理调用 `RestoreBossContext`，把较早快照写回静态行程和 SetupEvent。

具体载入与 `Awake` 的先后仍需游戏验证，不能把这个静态时序写成已经复现。本房从副本接管成权威时也需要检查：权威 `End` 的直接原生完成分支没有与副本 `pending` 分支相同的上下文提交调用。

### P5-03：静态行程正确不代表当前控制器已配置

`Prepare` 仅在跨房或 `changed == true` 时配置原生完成回调。`changed` 比较静态行程、约束和门标记，没有检查当前控制器的回调；`configured` 字段被赋值、保存及恢复，却未用于判断当前控制器是否配置完成。

原生 `Awake` 若遇到空 SetupEvent，会尝试 `CheckLoadSequence`；后者在静态 `currentSequence` 已存在时不会重新配置。于是“静态行程存在、SetupEvent 丢失”的状态具备缺失当前完成回调的可能。原生完成协程在发出淡出后，若 `OnBossSceneComplete` 为空，就直接结束。是否符合此次副机现场，要看回调列表及原生等待条件。

## 已排除的过度推断

服务器 `ServerManager.cs:614` 在房间无人时把最先报到的玩家设为房间权威；开房主机身份不决定这一角色。用户观察到的先进入，不等于已取得服务器报到顺序证据，但不到一秒的先后不能被排除。

原版行程完成闭包保存控制器和 SetupEvent，读取的是静态 `bossIndex`，没有捕获一份独立的旧序号。因此不能把问题直接解释为“闭包缓存了以前那一关”。原版 `EndBossScene` 也有 `endedScene` 防重，不能仅凭六只守望者会多次死亡就认定行程推进六次。

没有死亡时，Boss 胜利救援的接收流程不会恢复玩家；本轮未发现必须先触发救援才能进入上述风险路径的条件。

## 可审阅的最小实施方案（未实施）

1. 区分有效行程、有效单独神像战、无效采集。已有行程但采集不匹配时，拒绝用空消息重置对方，保留当前原生行程并记明原因。
2. 为同一次房间访问建立从“开始结算”持续到“原生完成/换房”的状态保护；旧上下文不能在结算开始后重新打开回滚快照。核对正常载入、接管与真正取消时各自的提交/恢复时机。
3. 按当前控制器验证完成回调；缺失时恰好安装一次，保留原生推进与返回规则，避免重复推进。
4. 诊断至少记录房间 visit/epoch、房间权威、sequence/index、采集结果、回调类型、实际下一场目标、完成协程等待条件、临时快照提交/恢复原因。现有 F9 只有行程名/是否在行程等摘要，不能充分区分这些路径。

不建议用“守望者死后强制所有人跳乌姆”作为修复，它会绕过原生约束、退出与控制恢复，也无法解释此前的全员返回。

## 所需专项及覆盖矩阵

首先用局部行程夹具分别验证 P5-01、P5-02 和 P5-03，随后交换房间权威：副机先报到 0–1 秒、主机后报到，以及相反顺序；全程不死亡、不倒地、不使用 F8。包含格林 → 温泉 → 乌恩 → 守望者 → 乌姆的原生出口，然后补一次从真实五门入口连续行进到守望者的检查。每次同时记录双方原生目标、控制恢复与实际 DLL 哈希；一次正常通过不能关闭概率性复发。

| 范围 | 已有证据 | 本次新增 | 尚缺 |
|---|---|---|---|
| 六只守望者生命/最终死亡 | 旧局部双端探针经 HealthManager 致死并到乌姆 | 原生死亡/完成代码只读分析 | 本次故障中的六只生命周期及阶段事件 |
| FSM 阶段、动态内容及弹幕 | 此前有通用/其他 Boss 检查，不能认证本次守望者 | 无运行验证 | 本次守望者各阶段、生成物身份与副本对应 |
| 场景与行程完成 | 旧 0.1.18 和 10 月 16 日局部行程未复现 | 交付 0.1.27 IL 与引用程序集对照 | 旧上下文晚到、无效采集、缺回调三类专项 |
| 房间权威/接管 | 服务器代码确认首报到规则 | 只读分析 | 副机先入不足一秒、实际接管时刻与结算状态 |
| 晚入/重进/断连 | 旧局部晚入基线 | 无运行验证 | 守望者结算中晚入、正常离房触发的权威迁移、断连取消 |
| 全五门累积状态 | 未覆盖 | 无 | 从入口连续推进到守望者及后续乌姆 |
| 两台电脑、FRP/真实网络 | 用户报告发生异常 | 无受控运行 | 双端现场诊断与弱网/乱序验证 |

现有 `P5MotionProbe` 直接设守望者索引，或从格林索引开始；直接守望者场景还固定让主机先入。旧通过记录不能覆盖本次“副机先入、无救援、完整五门累积”的条件。历史 blocker `p5-watchers-premature-return-unresolved` 保持未关闭。

本轮止于审计与实施方案。使用的 [hollow-knight-boss-net-audit 技能](C:/Users/STARA/.codex/skills/hollow-knight-boss-net-audit/SKILL.md) 明确要求："Do not edit the game, save files, installed mod, or project source unless the user separately asks for implementation after reviewing the report."
