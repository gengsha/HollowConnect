# 2026-10-14：原生 FSM 接管后的未完成动作

状态：本轮修复的五组专项验收完成，全部通过；不等于完整发布验收。生产修复分别提交为 `39a0cc5`、`6021cf7`、`6658cb7`、`e4ad923`、`31e33d9`、`c5079af`、`38d7480`、`c553058`，具体作用见下文。最终生产 DLL 构建提交为 `d6992e6e2879749a947e9c47aecd623f06ef1bc5`。`1f76062` 第二种顺序第 4 轮失败，以及 `38d7480` 实机通过后仍被确定性状态入口夹具拒绝的记录均保留。机器证据见 [专项汇总](evidence/fsm-continuation-2026-10-14.json)。本次不重新打包或安装日常游戏，既有试玩包仍为旧构建。

## 已证实的缺陷与修复范围

今天的筛查失败保留在 `.test-results/today-lifecycle-host-20261014/`。其 `guest.log` 实际也在该目录；筛查入口此前误写为另一个目录。第三轮重连后原显示副本成为权威，Hornet 为 `Thrown`、Needle 为 `Notify`，针以约 38 单位/秒继续飞行；有效引用和相同静态 ID 不能保证原生完成链仍可执行。

公共 `Entity.MakeHost` 恢复快照状态时只重入 `ActionRegistry.IsActionContinuous` 选中的动作。旧表遗漏两类本来尚未结束的原生工作：

- `iTweenMoveTo` 在 `OnEnter` 创建运动和完成回调，没有 `everyFrame` 字段。恢复时被删掉，空的回程状态立即发送 `FINISHED`，尚未到达起点便进入 `Notify`。原生回收通知可早于接收方进入等待状态，随后不会再发送。
- `DecelerateV2` 在 `OnFixedUpdate` 持续改变速度，也没有 `everyFrame` 字段。恢复减速状态时只留下速度检测，速度不能下降；恢复回程时则不能消除原有发射速度。

修复把这两个原生动作类型纳入公共恢复表，所有使用它们的实体都适用，没有 Boss 名称、场景、强制状态、额外事件、超时恢复或新增异常吞噬逻辑。绝对移动从当前快照位置继续朝原目的地运行，保留原生完成回调；不声明恢复精确动画时间，也不把相对移动动作一概重放。

严格专项进一步暴露原有的另一个公共生命周期缺陷：停用快照仍保留旧 `Return` 状态，`MakeHost` 对休眠源调用 `SetState`，会在没有 Awake 的物体上创建新的 iTween。后续退出时原生 `iTween.Stop` 对未初始化的 type / method 做 Substring 而抛越界。`6021cf7` 在休眠源上恢复原 `RestartOnEnable` 并执行原生 Stop 语义，避免运行异步动作；下一次激活仍由原生 FSM 驱动。没有绕开原生停止函数或新增 catch。

这两项没有解决原故障。`reconnect-notify-trace-6021cf7-20261014` 中，原权威的 Needle 在 02:00:51.192 已进入新一轮 `Out`，另一端在 02:00:51.270 却恢复上一轮 `Return`；02:00:51.308 提前广播回收事件，Boss 仍在 `Throw Antic`，随后进入 `Thrown` 永久等待。

进一步证实并分别修复的公共路径：

- `6658cb7`：原生状态进入改由 StateChanged 即时发布，变量仍按原周期采样；接收 byte.MaxValue 时清除旧状态。激活/动作数据先发布、状态延迟采样的窗口不再保留上一轮状态。
- `e4ad923`：最终数据报先消费普通和附加模块更新，再注销断线的作者。旧协议枚举顺序把断线放在实体更新之前，导致同包最后状态在作者被删除后被拒绝。
- `31e33d9`：原生重激活时，在同一发送锁内发布 active 和新状态。旧代码会形成“新状态 + 旧 inactive”的更新。
- `c5079af`：CombatState 为独立登记实体的根也保存父引用，副本应用层级，休眠源只在接管时恢复层级；位置包统一使用世界坐标。原来独立子实体虽然拥有稳定 ID，根节点却不受父引用同步/接管恢复覆盖，接管后 local-space tween 在旧父体坐标系中运行。真实 Needle 的回程曾到 y≈56.6，而发射 y≈28.1；仅收到回收事件会漏检此错误。
- `38d7480`：可靠重发集合原先先放新更新，再追加旧重发，能将刚发布的 active/FSM 快照回滚；多个重发集合也未按原序列排序。改为按相对当前序列的无符号距离从旧到新合并，最后应用当前包；动作事件仍全部保留。缓存使用独立集合容器，避免读取合并缓存后再次序列化重复动作。真实序列化/分发回归在普通序列与 ushort 回绕两种情况下旧代码均失败，修复后 113 passed / 0 failed，含 addon 集合与标量检查；这证明传输缺陷，尚不证明其就是第 4 轮实机失败的原因。
- `c553058`：PlayMaker `Fsm.EnterState` 在 `StateChanged` 之后才执行原生 `OnEnter`；单次状态发布的锁不能阻止发送线程在两者之间发布“父体新状态、子体旧 inactive”。独立发送线程在这个精确边界尝试生产发送锁，旧构建双方稳定观察到不完整入口。公共 EnterState 钩子现在将完整同步入口放在正常 `_sendGate` → `Lock` 顺序内；嵌套状态、子体激活及其 StateChanged 都在同一个发布范围中，保持锁顺序且支持同线程重入，没有新增等待/重试/异常捕获。修复前后的真实 Unity 夹具见下表。

`7d061d2` 将重发集合克隆改为保留实际派生类型和独立列表，保留公开基类的可继承性；`d6992e6` 保存可选包时序追踪与筛查入口，生产行为修复仍由上述独立提交承担。

这些改动没有 Boss 名称分支、强制状态/广播补偿、额外重试、延长超时或新增 catch。根 CombatState 载荷和位置语义已改变，本次验证要求双方完整生产 DLL 相同，未发布可供混用的安装包。

## 修改前对照

旧引擎 SHA-256 为 `B94D1E1D354556F6A0EB2602104C5CE85994F38086AC32D28344458F9D905F5F`，与今天筛查一致。

| 对照 | 双端可观察结果 | 证据目录 |
| --- | --- | --- |
| 实际 PlayMaker / iTween 通过生产 `Entity.MakeHost` 接管 | 目标 x=6，接管立即 `Done`，位置约 x=0.2，精确断言失败 | `.test-results/reconnect-continuation-old-engine-r2-20261014` |
| 实际固定帧减速和权威速度数据 | 接管后保持 38 速度，3 秒内不能自然完成，精确断言失败 | `.test-results/reconnect-deceleration-old-engine-r3-20261014` |
| 大黄蜂原生投针、每轮交换权威 | 第 2 轮后 `Thrown / Notify`，针飞出场外并触发原 20 秒超时；客机失败，另一端等待被下一轮测试终止 | `.test-results/reconnect-forced-handoff-old-engine-20261014` |
| 仅修恢复表的 `39a0cc5` 确定性对照 | 休眠接管后仍有一个 tween，断言失败 | `.test-results/reconnect-dormant-39a0cc5-baseline-20261014` |
| `39a0cc5` 首轮完整生命周期 | 招式恢复但原生 tween 停止越界，严格日志拒绝；双方失败，不能计入通过 | `.test-results/fsm-final-lifecycle-host-20261014` |
| `6021cf7` 完整专项和追踪 | 仍出现旧 Return 回滚新 Out，提前通知后卡死 | `.test-results/fsm-accepted-lifecycle-host-20261014`、`reconnect-notify-trace-6021cf7-20261014` |
| 旧 `6021cf7` 状态发布夹具 | 双端在新状态进入、下一次采样前拒绝未发布的状态 | `.test-results/fsm-state-publication-6021cf7-baseline-20261014` |
| 仅 `6658cb7` 的完整专项 | 第 4 轮仍 Boss Thrown / Needle inactive，不能计入通过 | `.test-results/fsm-state-entry-lifecycle-host-20261014` |
| 最终数据报分发旧逻辑 | 109 passed / 1 failed，最后状态被先断线操作拒绝；修改后 110 passed / 0 failed | `.test-results/fsm-disconnect-order-baseline-20261014.log` |
| 旧 `e4ad923` 重激活夹具 | 双端拒绝新状态与旧 inactive 同包 | `.test-results/fsm-reactivation-e4ad923-baseline-20261014` |
| 旧 `31e33d9` 根层级夹具 | 双端拒绝未应用原权威的根脱离快照 | `.test-results/fsm-root-hierarchy-31e33d9-baseline-r2-20261014` |
| `c5079af` 完整专项 | 第 7 轮被测试协调文件 sharing violation 中断，失败保留；`1f76062` 仅用关闭后原子发布不可变标记修复测试竞争 | `.test-results/fsm-parent-restored-lifecycle-host-20261014` |
| `1f76062` 第一种顺序 | 双方七项夹具和完整八轮通过，含回程位置；不覆盖另一种顺序 | `.test-results/fsm-verified-lifecycle-host-20261014` |
| `1f76062` 第二种顺序 | 第 4 轮接管端 Boss `Thrown` / Needle inactive、FSM 空状态，原 20 秒回收超时；另一端也等待超时 | `.test-results/fsm-verified-lifecycle-guest-20261014` |
| 旧可靠重发顺序 | 最新状态被旧重发覆盖；110 passed / 2 failed | `.test-results/fsm-resend-order-baseline-20261014.log` |
| 旧 `38d7480` 原生状态入口夹具 | 独立发送线程观察到父体新状态、子体旧 inactive，双方失败 | `.test-results/fsm-native-entry-38d7480-baseline-20261014` |
| 原生状态入口修复夹具 | 双方通过；完整入口结束前发送线程不能取走部分更新 | `.test-results/fsm-native-entry-fixed-precommit-20261014` |

一次未强制交换权威的旧引擎六轮随机重跑通过，不能覆盖今天失败或上述确定性失败。最初两版减速夹具没有正确建立共享 FSM 浮点变量、提前结束，最初回程夹具遗漏 `PlayMakerFSM.Reset`；这些夹具失败目录原样保留，不算生产根因证据。

根层级夹具初版误用 InitializeClient 来降级已初始化的 host，而该方法只用于初始化刚登记的副本；首版修改前/后失败不算生产证据。r2 修正为在接收端保持受控状态，仅通过生产 CombatState 编码器构造来源快照；上述 r2 才是有效前后对照。状态发布夹具一次空对象清理错误也已保留，修复后使用全新目录通过，未覆盖失败。

## 最终验收结果与方法

最终五组均具备双方 `.done` / `FINAL PASS`、零失败标记、严格日志扫描通过，且三个 DLL 与生产输出的哈希一致。最后两个隔离进程停止后再次扫描，避免将完成后的错误漏掉；测试副本中的 Smoke DLL 已移除。113 项回归与 14 项日志门槛自测通过，保留 14 组原始失败及其摘录。

| 当前构建运行目录 | 验证内容 | 结果 |
| --- | --- | --- |
| `fsm-atomic-entry-lifecycle-host-20261014` | 房主先入，双方八项夹具，八轮交替权威、自然恢复与回程位置 | 双端通过 |
| `fsm-atomic-entry-lifecycle-guest-20261014` | 客机先入，同样八项夹具与八轮交替权威 | 双端通过 |
| `fsm-atomic-entry-boss-late-20261014` | 假骑士迟入、露头、阶段移交、后续 AI/血量和重返 | 双端通过 |
| `fsm-atomic-entry-grimm-host-20261014` | 格林房主先入，火球/火蝠运动与副本匹配 | 双端通过 |
| `fsm-atomic-entry-grimm-guest-20261014` | 格林客机先入，同样运动与副本匹配 | 双端通过 |

最终生产 SHA-256：

- HKMP：`541E0A345914B9DCAB49800DC2FE4F586D73F226AF55543517D6768494561A5C`
- HollowConnect：`F25FD953672C5A4EE026416D8E12035D40A0E64AC8F06572949615838B5EFE83`
- HollowConnect.Core：`A4BFE22ACD87D24C56C6106E9C4BDF31C236DD9B8E73622221664026E786525C`

只使用 `.test-results/game-host`、`game-guest`，SilentProbe 每帧静音，内存角色和三个 SaveGame 重载拦截保持启用。没有读写玩家存档或向日常游戏部署。

两种先入顺序均运行真实回程/减速/休眠激活、状态即时发布、空状态、重激活一致性、根父体/世界坐标、完整状态入口共八项确定性检查，以及父子运行中 tween 克隆、销毁音源退出、原生苔藓到大黄蜂转场、原生 A Dash 登记暂停恢复、六轮投针断线和两轮自然 Needle `Decel` / `Return` 断线。每轮都强制交换权威，并要求有效引用、双方静态身份、血量、可见性、镜头、自然 `Throw Recover` 和回到原生发射位置附近。没有延长原超时或放宽失败标记；任一模组异常、HKMP ERROR、Spawn expired、原生崩溃或完成后的失败标记仍拒绝。

补充执行了假骑士迟入场/阶段接管和普通格林两种先入顺序的火球、火蝠运动专项。核心回归与日志门槛重新运行。每组必须具有双方 `.done`、`FINAL PASS`、零 `.failed`、严格日志扫描通过且三个 DLL 哈希与本次构建一致，才写入通过证据。

## 针对性审计与覆盖边界

采用 `hollow-knight-boss-net-audit` 技能做只读核查。生成的静态审计位于 `.test-results/reconnect-fsm-audit-20261014/`；本机仅安装 .NET 9，隔离审计助手将目标框架从 net8 改为 net9 后运行，未改变游戏文件。受审查的是精确测试副本：

| 文件 | 大小 | SHA-256 |
| --- | ---: | --- |
| `E:/projects/hollowConnect/.test-results/game-host/hollow_knight_Data/Managed/Assembly-CSharp.dll` | 4,064,768 | `5944411BD93830369390A4B51766EE68C4AB26195B299E25A07B5E7D0E00086D` |
| `E:/projects/hollowConnect/.test-results/game-host/hollow_knight_Data/Managed/PlayMaker.dll` | 212,480 | `0EF0E7829D125E1F632C8A189260EC6C6882630BE6932C8C7AE032EFBC53469A` |

| 范围 | 判断与本次检查 |
| --- | --- |
| FSM 阶段、完成回调、权威接管 | 已证实的公共遗漏；旧引擎确定性失败，新构建验收以专项 JSON 为准 |
| 动态物体和脱离层级子体 | 验证 Needle ID / 引用以及格林运动样本；所有 Boss 的飞行中弹幕接管仍待实机验证 |
| 血量、死亡和阶段 | 本次生命周期转场和假骑士阶段专项；不外推到全部 Boss |
| 场景和行程 | 苔藓到大黄蜂的原生完成链；完整一至四门未验收 |
| 救援、共享进度、普通奖励 | 本次不重跑完整实机覆盖；113 项回归不等同这些流程的实机验收 |
| 镜头与输入 | 本次各房正常状态检查；历史瞬时 FROZEN 原因仍未查明 |
| 网络 | 同机双客户端、本机 UDP；真实双机樱花 FRP 仍未验收 |

可靠合并回归覆盖单个数据报内的普通/附加模块重发顺序、ushort 回绕、事件保留和缓存不污染原发送集合；没有据此声明独立数据报乱序、嵌套重发来源归属和重发去重全部正确，也未执行网络丢包/重排专项。

本次专项不等于新 DLL 的完整 12 组发布验收，也不解除历史镜头阻塞。正式发布仍未就绪。
