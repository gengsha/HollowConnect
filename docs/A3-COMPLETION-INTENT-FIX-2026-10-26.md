# A3 完成意图与实体回退修复（2026-10-26）

本次修复上轮已复现的两项 P1：readiness 先于 context 恢复时静默丢失完成意图，以及普通房间实体回退绕过结算/readiness。生产改动集中在 BossRoomSynchronizer.cs；基线为本任务开始时的工作树，差异保存于 `.analysis/a3-fix-20261026/change.diff`，不将整个未提交工作树归为本次修改。

## 改动行为

- 已排队的完成意图在成功发送前继续保留。readiness 恢复不能清空该任务；缺少 context 或发送暂时失败时，每 0.5 秒重试，沿用首次排队的 8 秒 deadline。重复 End 不刷新 deadline，也不以 null 覆盖已有 context。
- context 一直缺失或无法发送，截止时记录一次明确失败；仅在 readiness 允许时本地结束。成功发送标志与本地已结束状态分开，失败退路不会伪装为已发送。已完成状态阻止重复 End、接管或心跳重新发起完成。
- 初次 ready=true/context=null 的既定策略保留：记录明确错误并本地结束。本次修复的是已经创建的等待任务被提前销毁，不统一改写该产品策略。
- 实体回退记录独立的待处理意图，先请求一次 settlement await，再检查统一的 readiness；消息只到达一次也能由后续 Tick 恢复执行。重复实体消息不重复建立结算等待。
- room completion 到达时接管任务；Clear、visit/epoch 变化、重置、转为权威或进入 BossSequence 时取消旧实体意图。序列仍由 room context 维护 BossIndex。
- End、Tick 与实体回退复用带异常诊断的 readiness 检查；周期性 capture/defeat 调用复用已有异常边界，避免单次回调异常直接跳过当前 Tick 的完成重试。

## 验证

相关核心检查 29/29 通过。扩展后的双端生产方法边界 22/22 通过，包含原 H4/G2、重复 End、显式超时、发送失败恢复/终止、单次实体通知恢复、room 优先、Clear/visit/重置/序列取消及 readiness 抛异常后恢复。边界夹具拦截发送与 native End 调用；其结果与下述真实 native 流程分别记录。

| 实机专项（共享进度 ON） | 当前状态 | 验证目的 |
|---|---|---|
| native-context | 通过 | 双方存活，实际 Boss 死亡；readiness 先恢复而 capture 仍 null；后来经真实网络补发，双端 native End 各一次 |
| native-entity | 未覆盖，保留失败尝试 | 当前生产的实体完成发送路径没有调用点，见下方说明；不将没有消息的超时算成回退通过 |
| godhome-guest | 通过 | guest 权威且倒地时的普通 Godhome 结算与原生控制恢复 |
| p5-pure-host | 通过 | P5 host 权威，真实 spectator/结算与原生下一房推进 |
| p5-pure-guest | 通过 | P5 guest 权威，真实 spectator/结算与原生下一房推进 |

上述均顺序使用已有隔离 game-host/game-guest 与无保存的新角色，由持久脚本记录参数、PID、生产/测试/游戏依赖哈希、结果、超时及精确路径清理。没有重新打包、安装或修改个人存档；这组针对验证不等于完整发行矩阵验收。

最终为 **29 项核心、22 项边界、4 组实机/8 个端点通过**；通过的游戏组两端完成且严格日志通过，生产哈希均与冻结记录一致。两组 P5 均确认从 GG_Hollow_Knight 原生推进至 GG_Radiance、BossIndex 只增加一次、复活/观战状态与原生输入恢复。native-entity 的失败不计入通过，单独标为发送路径前提不存在、端到端未覆盖。

## 夹具迭代与独立发现

新 native 夹具先做最小预检，失败后停止依赖批次。历史尝试 a–d 保留：a 缺少 Hall SetupEvent，原生 Awake 不调用 Setup、没有死亡订阅；b/c 的组合场景要求倒地结算在 6 秒内恢复，该前提不成立；d 的夹具写错了原生返回入口字段，导致生成半份 return route，生产 Apply 正确拒绝。最终夹具使用原生 Awake/Setup、预检 bossesLeft/bosses[]、双方 addon Alive 与完整返回场景/入口，未绕过原生终局或放宽 Apply 校验。

c 的真实回执另确认一个既有等待问题：同一死亡产生 victory 1/2，客户端处理两份通知时均 Down。ticket1 的请求复活成功并结算，ticket2 的请求抵达时成员已 Alive，未结算该票。客户端当前 token 已为 2，收到 Ready1 不解除等待，需等待 ticket2 的服务端截止。证据为该轮 host/guest 的 RECEIPT 日志。这与本次两个状态机缺陷分别记录；H4 实机用双方存活隔离 context 时序，倒地仍由其它用例覆盖，不将不满足的 6 秒前提记为通过。

native-entity-e 暴露了原审查的可达性限制：在当前源码和交付 DLL 中，BossSceneComponent 的构造只出现在 Entity.CheckGodhome，而后者没有调用点。普通 Gruz 的实际原生死亡不会发送该实体完成通知，因而丢弃 room completion 后，实验无法覆盖“只收到实体消息”。已停止该依赖实验，没有给生产或夹具额外挂接组件来取得通过。第二项修复证明了已有回退入口的控制流正确性，其当前可达性与原生端到端覆盖不作扩大声明。独立的 Godhome/P5 实机回归继续验证已启用路径。静态核对见 `.analysis/a3-fix-20261026/entity-emitter-preflight.json`，失败现场保留于 `.test-results/a3-fix-20261026-native-entity-e/`。

只改新 native 夹具期间生产 DLL 和 22 项边界逻辑不变，因此保留边界通过记录；每轮 native 使用新目录、重建 Smoke，并重新记录对应测试输入。原审查报告与失败证据不覆盖。

P5 Pure host-e 同样因旧专项夹具只配置 sequence/index、没有原生门提供的完整 return route 而失败。只补齐 P5 夹具的返回字段，并在杀 Boss 前预检真实 CaptureContext 的 scene/sequence/route，再以 f 重跑两组 P5；不改生产校验或原生推进。先前通过的 native-context-e、godhome-guest-e 与边界用例的相关逻辑未变，保留对应哈希证据。

## 当前二进制与证据

本地构建：`E:/projects/hollowConnect/src/HollowConnect/bin/Release/net472/`。

| 文件 | SHA256 |
|---|---|
| HKMP.dll | 972BC86BF44B693DCAF4096E475CD11DE5B1E36CB3C2C05BC564DCC8B48590D7 |
| HollowConnect.dll | 46E9A64D2A9A2E1C66459650CFFFAE06FC47FD1B1CE15CA9F20DC1403B4E0AA5 |
| HollowConnect.Core.dll | 746C12EF77F5097A57B7416CCA5BE728BE33F3163FEAF527A2804AA6852426F6 |

- 选择与迭代：`.analysis/a3-fix-20261026/SELECTION.md`
- 核心检查：`.analysis/a3-fix-20261026/core.log`
- 边界结果：`.analysis/a3-fix-20261026/a3-fix-20261026-boundaries-a.json`
- 边界逐项观测：`.test-results/a3-fix-20261026-boundaries-a/host.boundary.json` 与 `guest.boundary.json`
- 实机持久运行器：`.analysis/a3-fix-20261026/Run-Native.ps1`
- 最终汇总：`.analysis/a3-fix-20261026/evidence.json`
- 实机通过目录：`.test-results/a3-fix-20261026-native-context-e/`、`.test-results/a3-fix-20261026-godhome-guest-e/`、`.test-results/a3-fix-20261026-p5-pure-host-f/`、`.test-results/a3-fix-20261026-p5-pure-guest-f/`
- 原审查：[A3 定向验证报告](E:/projects/hollowConnect/docs/A3-COMPLETION-INTENT-VALIDATION-2026-10-26.md)
