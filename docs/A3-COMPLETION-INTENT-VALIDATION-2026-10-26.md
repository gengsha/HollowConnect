# A3 完成意图定向验证（2026-10-26）

结论：A3 修复有效覆盖了多条原有缺口，但尚未闭环。当前交付哈希的生产方法边界探针为 **10 通过、2 失败**，相关核心测试 **29 通过、0 失败**。两项失败均为受控条件下的生产代码执行结果；尚未证明玩家实际卡关、提前离场或救援丢失。

本次只增加 Smoke 验证夹具与报告，没有修改生产源码、版本或打包。A2/A4 现有实现不重复改写；本轮也不把其旧哈希实机记录转为当前构建的通过记录。

## P1：已保留的完成意图仍可能静默丢失

位置：[BossRoomSynchronizer.cs:217](E:/projects/hollowConnect/src/HKMP/Game/Client/Entity/BossRoomSynchronizer.cs:217)，后续执行与清理在同文件 `255–278`。

复现 H4：

1. authority 的 `End` 遇到 `CaptureBossContext=null`、`BossTransitionReady=false`，创建 `pending=""` 与 8 秒 deadline。
2. 截止前让 readiness 恢复，context 仍为 null；调用生产 `Tick`。
3. `pending.Length==0` 跳过 Apply，调用结束入口，随后清空 pending 并设置 sent=true。
4. 再让 capture 返回非空 context，再次调用 Tick。

实测结果：`nativeCalls=1, sends=[], pending=null, markedSent=true, completionApplied=true, diagnostics=[]`。后续 context 没有补发，且没有明确失败日志。这与初次 `ready=true/null` 的显式报错策略不同；此处是在已创建重试任务后静默销毁任务。

建议修复：将“本地可以结束”和“context 已成功发送或已明确超时失败”分别作为条件。已经排队的空 context，在 deadline 前继续保留并重试；deadline 后记录一次明确失败，再在 readiness 允许时本地结束。清理 pending 或设置发送成功标志前，必须有对应的发送成功/显式失败结果。保留初次 ready=true/null 的既定产品策略也可以，不必为了这项修复一并改动它。

验收补项：readiness 早于 context、context 早于 readiness、两者跨 deadline、重复 End 不延长既有 deadline，以及永久发送异常时不能标记为成功发送。

## P1：实体回退绕过 readiness

位置：[BossRoomSynchronizer.cs:48](E:/projects/hollowConnect/src/HKMP/Game/Client/Entity/BossRoomSynchronizer.cs:48)。实际 readiness 的定义见 [CoopRuntime.cs:269](E:/projects/hollowConnect/src/HollowConnect/CoopRuntime.cs:269)。

复现 G2：非权威、普通非序列房间、无 room pending，将 `BossTransitionReady` 设为 false，然后执行生产 `ApplyEntityCompletion`。

实测结果：`nativeCalls=1, completionApplied=true, awaits=0`。该方法没有调用 readiness 或 settlement await，直接把 applying=true 后调用结束入口。实际 readiness 包括 Down、Returning、changingScene 和 BossSettlementPending 等限制，因此这条回退没有保留主完成路径的等待条件。

建议修复：实体完成应先记录属于当前 visit/epoch 的待处理意图，并经过统一的 settlement/readiness 门槛；未 ready 时保留意图，在 Tick 恢复执行。不能只加 `if (!ready) return`，否则可靠消息被消费后可能再没有触发机会。去重、Clear、换房/epoch、room completion 到达与序列模式变化都要处理；序列仍交由 room context 推进 BossIndex。

验收补项：普通房间 alive/down/returning/settlement pending 的实体消息；单次消息在 readiness 恢复后恰好执行一次；期间换房、重置或转入序列时取消旧意图。原生 Pantheon 的 BossIndex、原生结束回调与救援时序还需实机验证。

## 通过与失败的边界清单

| ID | 条件 | 结果 |
|---|---|---|
| H1 | capture 先 null 后成功，context 先于 readiness 恢复 | 通过：等待期间发送一次，ready 后调用结束一次 |
| H2 | 初次 ready=true 且 context=null | 通过：明确 Error，本地结束，无 room completion 发送 |
| H3 | 永久 null、deadline 过期、随后 ready | 通过：明确 Error 一次，ready 后本地结束 |
| H4 | readiness 先恢复，context 稍后才可用 | **失败：静默清理意图且不补发** |
| H5 | 有效 context、readiness 延迟 | 通过：可提前发送，恢复后结束一次 |
| H6 | 传入的 orig 委托抛异常 | 通过：原异常重新抛出，Error 含 scene/visit/epoch |
| H7 | 发送短暂抛错、仍未 ready 时恢复 | 通过：诊断后重试发送，再本地结束 |
| G1 | 普通房间的实体完成回退 | 通过：结束入口调用一次，重复回退不再调用 |
| G2 | 普通房间实体完成且 readiness=false | **失败：仍调用结束入口** |
| G3 | 实体完成时注入序列标志 | 通过：不结束、不改 BossIndex |
| G4 | 已有 room pending 时收到实体回退 | 通过：room pending 优先，已有 settlement await |
| G5 | Apply 返回 false 超期，稍后成功 | 通过：错误一次，仍能重试并结束 |

## 证据范围

这是一组同机双端 Unity 探针，产品共享进度 ON，使用已有隔离 game-host/game-guest 和无保存的新角色，真实连接并原生进入 GG_Gruz_Mother，预检各自的原生 BossSceneController 与真实权威。

每个测试创建独立、未注册的 BossRoomSynchronizer，执行交付 DLL 中的生产 End/Tick/ApplyEntityCompletion/Receive。capture/readiness/apply 等回调受控；发送和 native End 调用被拦截计数；超期分支通过注入过期 deadline 触发；序列检查通过注入 IsInSequence 标志触发。因此，结果证明控制流边界，不证明实际网络传递、原生结束回调、8 秒墙钟时序、原生 Pantheon 或玩家后果。H6 注入的是 orig 委托异常，并非自然发生的游戏异常。

预期诊断按当前测试的指定前缀定向捕获到 boundary.json；其它错误继续走原有严格日志门禁。两端完成、正常断开，未匹配错误的严格日志检查通过。不能将这里的严格日志通过理解成故障注入没有产生 Error。

第一轮 a 因夹具未把写包转换为接收读包而在 G4 中断，H4/G2 在中断前已复现。仅修正夹具 Receive 的 Packet 初始化，保存 a 的失败记录，再在新目录 b 完成 12 项；生产哈希不变。b 结果为最终边界证据。

独立的相关核心检查选中 BossVictoryRecoveryTests、RescueRulesTests 共 29 项，全部通过；未重新运行或宣称通过其它 293 项，也未重新判断 grimmFlames2 全套夹具问题。由于直接边界失败，未继续扩展到依赖这些边界的长游戏矩阵。

| 文件 | SHA256 |
|---|---|
| HKMP.dll | F5B370FE78F3BF542A02CDF55F008C7964E053752A6DE907AA8F13C70EF71906 |
| HollowConnect.dll | 3A1786569F656505AAD3B39F2DCC764A8AD9A1AD335BF464095A3AEAB7684C86 |
| HollowConnect.Core.dll | 746C12EF77F5097A57B7416CCA5BE728BE33F3163FEAF527A2804AA6852426F6 |

执行后核对上述二进制和 A3 的三份相关源码均与 frozen.json 一致，隔离进程已结束，Smoke 已停用。

证据：

- [选择、范围与尝试说明](E:/projects/hollowConnect/.analysis/a3-validation-20261026/SELECTION.md)
- [持久运行器](E:/projects/hollowConnect/.analysis/a3-validation-20261026/Run.ps1)
- [最终执行状态](E:/projects/hollowConnect/.analysis/a3-validation-20261026/a3-validation-20261026-boundaries-b.json)
- [Host 逐项观测与诊断](E:/projects/hollowConnect/.test-results/a3-validation-20261026-boundaries-b/host.boundary.json)
- [Guest 逐项观测与诊断](E:/projects/hollowConnect/.test-results/a3-validation-20261026-boundaries-b/guest.boundary.json)
- [核心测试结果](E:/projects/hollowConnect/.analysis/a3-validation-20261026/core.log)
- [本次测试夹具](E:/projects/hollowConnect/tests/Smoke/BossCompletionIntentChecks.cs)

其它静态保留：Tick 中 readiness、周期性 Capture/BossDefeated 仍使用直接回调，并非全部覆盖 End 中的独立异常边界。本轮没有对这些 Tick 异常注入，不将其列为已复现玩家影响。

后续（2026-10-26）：上述两项边界已由本任务继续修复，29 项核心、22 项边界与 4 组实机通过，见 [修复与验证记录](E:/projects/hollowConnect/docs/A3-COMPLETION-INTENT-FIX-2026-10-26.md)。新增可达性核对发现：当前 Entity.CheckGodhome 没有调用点，BossSceneComponent 的构造在该方法内。因此 G2 证明的是回退入口的控制流缺陷，不证明当前普通 Boss 会自然触发该入口；仅实体消息的原生实机覆盖仍未建立。此处保留原审查和旧哈希失败证据，避免把新结果追溯套用到旧构建。
