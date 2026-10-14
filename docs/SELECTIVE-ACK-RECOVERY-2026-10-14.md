# 2026-10-14：周期丢包下的可靠消息送达

生产修复提交 `44ae5d17537f4f257c7d553e11694dc0c57abe98`。确定性对照先复现双方入场消息持续未送达，修复后 142 项回归和 14 项严格日志门槛通过。原周期模式两种顺序的入场专项通过；当前构建九组实机测试七组通过、两组接管回收失败，完整同步验收未通过。两个隔离进程已停止，Smoke 已停用，停机后复扫和三个生产 DLL 哈希核对完成；未打包、未安装到正常游戏。详细计数与保留失败见 [机器记录](evidence/selective-ack-recovery-2026-10-14.json)。

## 根因与公共修复

原发送端只按 RTT 超时识别丢包。在 17ms 发送间隔、200ms 最小预期 RTT 下，超时检测与下一包携带可靠数据的组合可形成每 13 包一次的重发。原周期注入每 13 包连续丢两包，导致来源 90 的载体 90、103、116、129……一直被丢弃。普通心跳仍有往返，因此不会因整个连接失活而中止等待。

修改 `UdpCongestionManager` 的共同 ACK 处理：先移除已确认数据报，再检查仍未确认的可靠数据报。如果同一 ACK 及其位图确认至少三份较新的、不同的数据报，则将缺口标记为丢失，让下一包携带其可靠内容。只统计不同包，重复收到同一 ACK 不会累计次数；序列比较考虑 ushort 回绕，旧 ACK 不提供新送达证据。每个载体只标记一次，仍沿用已有的来源去重与嵌套重发逻辑。

这是根据已送达后续包识别缺口的公共机制，不按 Boss 分支。没有新增发送计时器或固定补发次数；原 RTT 超时兜底、连接心跳、接收超时和游戏断言保持原值，故障插件的周期规则也未修改。重排超过三份后续已确认包可能触发一次提前恢复，接收端仍按来源去重；不能宣称任意丢包分布均可保证送达。

## 有效失败对照

`tests/SelectiveAckProbe.cs` 同步驱动真实 `CreateAndSendUpdatePacket`、编码、分片、解码、ACK 构造、接收过滤和主线程分发。只在 DTLS 边界截获应用帧，并虚拟化发送队列 Stopwatch 的经过时间，避免系统计时抖动改变丢包相位。双方方向都保留每 13 包丢两包、每 9 包延后一包、每 17 包重复一包，包括反向 ACK 流量。

- `.test-results/selective-ack-baseline-v2-20261014.log`：132 passed / 10 failed。八个入场场景均持续未送达；普通来源 90 的双方载体均为 `90,103,116,129,142,155,168,181,194,207`。另两项拒绝旧发送器不识别三份后续确认的缺口。
- `.test-results/selective-ack-final-unit-20261014.log`：142 passed / 0 failed。普通与回绕序列、丢包窗口两个位置、来源 90/91、拥塞发送间隔均双方恰好交付一次；确认完成后不持续重发。重复/旧 ACK、短暂乱序、已确认原包以及缺少后续 ACK 时的原超时兜底也通过。
- 对照发送器 DLL 保存在 `.test-results/selective-ack-old-binaries-20261014/HKMP.dll`，构建来源为 `92a7822`，生产发送逻辑尚未修改。早先真实游戏失败保留于 `.test-results/reliability-loss-lifecycle-host-trace-20261014`，原始来源与 DROP 记录同确定性对照吻合。

首版夹具忘记初始化入场位置，导致编码拒绝；`.test-results/selective-ack-baseline-20261014.log` 不作为生产失败证据。第一次修复编译中的泛型推导错误也保留在 `.test-results/selective-ack-fixed-build-20261014.log`，不算运行证据。有效对照均使用初始化完整的消息。

## 实机验收

完整周期压力测试出现一个保留失败：`selective-ack-final-periodic-lifecycle-host-20261014` 已通过双方入场、原生房间切换、同房重连和前两次飞针自然回收；第 3 轮（`collider-cycle.2`）在双方立即断线后，新权威保留 Boss `Thrown`，但飞针 inactive / FSM 空状态，20 秒回收断言失败。旧权威断线前的轨迹为飞针 `Out`，这不是入场来源持续未送达；此样本没有实体包级追踪，尚不能唯一归因于最后可靠数据丢失、交接顺序或初始/实时快照交错。该完整组被拒绝，不算通过，也不能将后续成功重跑当作已解除。

固定种子反向组 `selective-ack-final-lifecycle-guest-20261014` 又在第 5 轮（`collider-cycle.4`）出现同样的 Boss `Thrown` / inactive 飞针，回收超时。该组也拒绝，证明症状并非只见于周期模式；仍没有足够包级证据唯一定位原因。

反向先入的原周期压力组 `selective-ack-final-periodic-trace-lifecycle-guest-20261014` 通过八轮权威交换、八次飞针自然回收及双方九项 Unity 夹具，带现有实体包级追踪，未重现上述失败。固定种子正向八轮、假骑士迟入交接和格林两种顺序均通过。两种顺序的独立周期入场组均通过苔藓房、大黄蜂房、同房重连及双方九项 Unity 夹具；这些入场组没有请求八轮压力检查，其原有入场断言和超时不变，不能将它们计为完整八轮通过。

| `.test-results/` 下目录 | 结果与范围 |
| --- | --- |
| `selective-ack-final-periodic-lifecycle-host-20261014` | 拒绝；第 3 轮交接后回收超时，之前完成两次自然回收。 |
| `selective-ack-final-periodic-trace-lifecycle-guest-20261014` | 双端通过；原周期反向八轮交换/回收，每端九项 Unity 夹具。 |
| `selective-ack-final-lifecycle-host-20261014` | 双端通过；固定种子正向八轮交换/回收，每端九项 Unity 夹具。 |
| `selective-ack-final-lifecycle-guest-20261014` | 拒绝；固定种子第 5 轮交接后回收超时，之前完成四次自然回收。 |
| `selective-ack-final-boss-late-20261014` | 双端通过；假骑士迟入、阶段自然超时后的接管、再次破甲和返回端收敛。 |
| `selective-ack-final-grimm-host-20261014` | 双端通过；固定种子原生弹幕与运动检查。 |
| `selective-ack-final-grimm-guest-20261014` | 双端通过；反向同一检查。 |
| `selective-ack-final-periodic-entry-host-20261014` | 双端通过；原周期入场、房间切换、同房重连，每端九项 Unity 夹具。 |
| `selective-ack-final-periodic-entry-guest-20261014` | 双端通过；反向同一入场范围，每端九项 Unity 夹具。 |

为核对新接管失败，另用保留的 `92a7822` 旧 HKMP 发送器搭配相同候选 HollowConnect/Core 和相同固定种子、反向八轮断言，运行 `selective-ack-old-sender-seeded-trace-guest-20261014`。该诊断八轮通过，未重现回收失败；它不属于新构建验收。不能据此将新失败断言为旧问题，也不能仅凭一次对照通过认定快恢复是唯一原因。机器记录保留两组实际失败及此项归因未定的对照。

每轮全新目录，失败原运行器为 `.test-results/run-selective-ack-final-20261014.ps1`，后续全部结果（包括拒绝）由 `.test-results/run-selective-ack-followup-20261014.ps1` 保存；旧发送器诊断为 `.test-results/run-selective-ack-old-sender-contrast-20261014.ps1`。最后两个隔离 PID 22916 / 29664 已停止；测试插件改名为 `HollowConnect.Smoke.44ae5d1.dll.disabled`，并将隔离目录三个生产 DLL 恢复成候选哈希。没有触碰正常游戏或玩家存档。`tests/Smoke/Summarize-Selective-Ack-Recovery.ps1` 在停机后重新核对全部九组和旧发送器诊断；拒绝组保持 `bothPassed=false`，没有最终故障计数的失败组记为 null，不补造通过计数。

候选构建三个生产 DLL 的 SHA-256：

| DLL | SHA-256 |
| --- | --- |
| HKMP.dll | `07032B3EE9DD2E82E46E3FACB176CEA632A8FD7560006279D7725EDF1CEF0EF1` |
| HollowConnect.dll | `6DEB846A19487957F1D234F48498C1FCDE5E2C0E85B551EEFA01F7A9F6503B12` |
| HollowConnect.Core.dll | `5EC3F4236D772D30FF04D972B201EB007224E226AF39CCA783CB934F493D23E3` |

## 未解除的范围

本轮完整周期正向 / 固定种子反向压力中的 `Thrown` / inactive 飞针失败未解除；需补抓交接前后原始可靠载荷、激活发布、最后发送与 ACK、恢复顺序，不能断言为既有问题或已由本次修复解决。假骑士旧样本再次破甲未露头缺少受伤后的阶段轨迹，后续通过不能唯一归因或解除它；历史镜头 FROZEN 来源也未查明。其他 GenericData 快照和初始/实时快照交错、真实双机 FRP、当前构建完整 12 组发布矩阵、全部 Boss/梦境/飞行中弹幕接管、完整一至四门、普通流程奖励与三人以上仍待验证。之前五项修复及旧哈希验收见 [网络专项](NETWORK-RELIABILITY-2026-10-14.md)。仍 `releaseReady=false`。
