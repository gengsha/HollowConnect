# 2026-10-14：同一数据报在同一 Unity 回调中应用

生产修复提交及构建来源：`dc0a4f54ea67ebf40f4a3ec161c55f43921303a3`。确认并修复接收端同一数据报被拆成多个主线程回调的问题。原两次接管后 `Thrown` / inactive 飞针失败仍保留，尚不能唯一归因于这个缺陷。

`PacketManager.HandleClientUpdatePacket` 原先在接收线程逐项排队。`ThreadUtil.Update` 取走当时的队列后，接收线程可能才排入剩余子实体更新。于是父体的新状态已在这一帧可见，子体激活仍留到下一帧；如果此时断线，剩余旧会话回调会被正常丢弃。

现在每份收到的数据报只排入一个 Unity 回调，在其中顺序处理普通和 addon 数据。初次连接仍等待主线程登记处理器；如果某个处理器触发断线，剩余旧会话更新仍被丢弃。沿用既有会话代次和异常处理，没有新同步层、锁、超时或生产 catch。此保证只覆盖同一数据报，不保证服务器已把相关实体放入同一转发数据报。

## 确定性对照

在现有 `ClientSessionProbe` 加一项测试：接收线程首次排队后暂停，主线程执行一次，要求父体、子体及 addon 同帧应用；另检查处理器内断线不会继续消费旧会话数据。测试没有真实时间相位或 Boss 状态假设。

| `.test-results/` 下日志 | 结果 |
| --- | --- |
| `datagram-frame-baseline-unit-20261014.log` | 142 passed / 1 failed；精确拒绝父体已应用、子体和 addon 尚未应用。 |
| `datagram-frame-fixed-unit-20261014.log` | 提交前 143 passed / 0 failed。 |
| `datagram-frame-final-unit-20261014.log` | 提交后构建 143 passed / 0 failed。 |
| `datagram-frame-final-log-gate-20261014.log` | 14 项严格日志门槛检查通过。 |

候选 DLL 位于 `src/HollowConnect/bin/Release/net472/`，HKMP ProductVersion 为 `1.0.0+dc0a4f54ea67ebf40f4a3ec161c55f43921303a3`。实机目录的 `binaries.json` 与生产输出及两端隔离副本一致。

| DLL | SHA-256 |
| --- | --- |
| HKMP.dll | `7E108A45D458DC5946DB8BB857E89A65CD574BB468482D84AD9A740353254925` |
| HollowConnect.dll | `AFCE1CBF777D03C173987D8F4AC47F0175FFC42E5AE12ABAF722B2FD1F95956D` |
| HollowConnect.Core.dll | `4ABC9BEF0C7669AE2F85246B964AFA53CBAC325453460884AEBAB3CFDB4EE616` |

## 实机追踪

旧 HKMP `44ae5d1` 的周期正向及固定种子反向两组带追踪对照各八轮通过。其复现条件与原失败一致，但新增追踪会影响时序；通过不解除原失败。旧三个候选 DLL 保存在 `.test-results/inactive-takeover-baseline-binaries-20261014/`；对照启动只覆盖旧 HKMP，其他两个 DLL 从相同未改生产源码重新构建，实际加载哈希见各目录 `binaries.json`。

在现有可选包追踪中补充 ACK、实际激活应用、组件导致的激活变化和接管前快照，便于后续区分未送达、已收到未应用及应用后被覆盖。没有新建汇总脚本或扩大 Boss 验收矩阵。

当前构建的两组针对性实机验证均通过。每组两端各通过九项现有 Unity FSM 夹具、四次实际接管和四次自然回收；合计八轮，并覆盖苔藓到大黄蜂原生房间切换、同房重连、A Dash 登记暂停恢复、静态身份和 tween 克隆。丢包、乱序、重复计数均非零，双端 `.done` / `FINAL PASS` 完整，零 `.failed`，停机后严格日志复扫通过。

| `.test-results/` 下目录 | 故障 / 先入端 | 实际交换 / 自然回收 | 结果 |
| --- | --- | --- | --- |
| `datagram-frame-final-periodic-host-20261014` | 原周期 / host | 8 / 8 | 双端通过 |
| `datagram-frame-final-seeded-guest-retry-20261014` | 固定种子 / guest | 8 / 8 | 双端通过 |

原反向目录 `datagram-frame-final-seeded-guest-20261014` 的日志停在完成第 4 轮后，检查时两个进程均已退出，没有 `.done`、`FINAL PASS` 或 `.failed`；退出原因未知，记为未完成并保留原证据。上述反向重跑使用相同配置和全新目录，未覆盖它。两个隔离进程已停止，Smoke 改名为 `HollowConnect.Smoke.dc0a4f5.dll.disabled`，三份生产 DLL 哈希保持不变。

旧 HKMP 对照也通过，因此这些实机结果不能证明此前两次飞针回收失败已解除，或唯一根因就是本次修复。服务器转发是否把关联实体分到不同数据报仍未确认，不据此追加补偿。完整发布仍未就绪；旧假骑士再次破甲和历史镜头 FROZEN 也仍待定位。此前周期消息持续未送达的修复与原始接管失败见 [选择性 ACK 专项](SELECTIVE-ACK-RECOVERY-2026-10-14.md)。
