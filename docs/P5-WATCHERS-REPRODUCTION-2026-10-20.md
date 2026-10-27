# 五门守望者骑士：0.1.27 双端复现记录

已在真实交付 DLL 上复现“正常换房回退行程序号 → 新房权威发送空行程 → 对端误走神像战返回”的异常链。主客角色互换后各出现一次；六组对照正常到达乌姆。本轮未完整重现用户这次“一人回五门入口、一人留在守望者房不能动”的最终分布，不能把局部复现写成现场终态已完全确认。

用户本轮要求是“先试试复现”。只新增/修改测试工具、报告和隔离副本的测试插件，没有修改生产源码、交付 ZIP、正式安装或私人存档，没有实施修复。历史 blocker `p5-watchers-premature-return-unresolved` 保持未关闭。此前的[只读审计](P5-WATCHERS-RECURRENCE-AUDIT-2026-10-20.md)是运行前快照，本报告补充后续运行证据，不改写原有结论。

## 受控环境与实际二进制

- 仅运行现存 `E:/projects/hollowConnect/.test-results/game-host` 和 `game-guest`，同机双 Unity 客户端、真实 UDP；没有网络故障注入。
- 使用全新内存角色，profile 99；测试插件拦截三个 `SaveGame` 重载。免伤避免死亡，关闭测试角色的 F8 输入；日志终态均为 `deaths=0`、`down=False`。
- 格林和六只守望者由 `HealthManager.ApplyExtraDamage` 注入终结伤害；Boss 死亡、原生完成回调、温泉/乌恩出口和后续载入实际执行。未直接调用完成函数或强设 Boss FSM 来制造故障。
- 对照夹具从指定行程序号开始；格林组实际走索引 28 格林 → 29 温泉 → 30 乌恩 → 31 守望者，预期下一项为 32 `GG_Uumuu_V`。没有连续打过前 28 场。
- 生产 DLL 从 `E:/projects/hollowConnect/dist/HollowConnect-0.1.27-windows.zip` 提取；不能用当前工作区资源同步等未交付输出替代。

| 对象 | SHA-256 |
|---|---|
| 0.1.27 ZIP | `05F97A2D7762C5276E87E508D18E6A509A291AB8FB329A9B2EA518A18EFE89FC` |
| `HKMP.dll` | `CCDDF40DE5052E83CAFE0EA99C7853CE26BCC62516E920AD818E2D05C5FABB25` |
| `HollowConnect.dll` | `28BB9DC70EFFA16F20E899C9D3AE0559EDD230B3408DB184C0DB8A5C1BD4E75C` |
| `HollowConnect.Core.dll` | `64661526E34F5EE64BD33C343E10AFE823DCC056671564582585D9E8C0A82787` |

每组 `binaries.json` 和 fixture 的 `origin.json` 保存实际二进制及 Smoke 哈希；运行后逐端核对生产 DLL。详细结果和证据索引见 [机器记录](evidence/p5-watchers-reproduction-2026-10-20.json)。

## 八组局部行程结果

运行目录均位于 `E:/projects/hollowConnect/.test-results/`。间隔为首次观察到守望者 activeScene 的双端时间差，分辨率约 0.1 秒，并非报到包的精确时间。

| 运行后缀 | 起点 / 先入顺序 | 守望者实际先入 | 结果 |
|---|---|---:|---|
| `a-direct-guest-200` | 直接守望者，副机先 | 副机 112 ms | 两端乌姆、控制恢复、严格日志通过 |
| `a-direct-guest-600` | 直接守望者，副机先 | 副机 594 ms | 同上 |
| `a-unn-guest-500` | 乌恩起，副机始终先 | 副机 485 ms | 同上 |
| `a-grimm-guest-500` | 格林起，副机始终先 | 副机 479 ms | 同上 |
| `a-direct-host-500` | 直接守望者，主机先 | 主机 462 ms | 同上 |
| `b-grimm-host-to-guest` | 格林/温泉/乌恩主机先，守望者副机先 | 副机 442 ms | **复现异常链**；未到乌姆，严格日志失败 |
| `b-grimm-guest-to-host` | 格林/温泉/乌恩副机先，守望者主机先 | 主机 446 ms | **角色互换也复现**；未到乌姆，严格日志失败 |
| `b-unn-host-to-guest` | 仅从乌恩开始，主机先入乌恩、副机先入守望者 | 副机 455 ms | 两端乌姆、控制恢复、严格日志通过 |

完整运行名前缀为 `p5-repro-027-20261020-`。两次复现的 runner 原始结果保留为 `needs-inspection`，`.failed` 和失败状态 JSON 原样保留。这里的“复现”指人工核对确认缺陷出现，不表示功能验收通过；没有统计概率或宣称每次都会触发。

## 第一组复现的直接证据

目录：`E:/projects/hollowConnect/.test-results/p5-repro-027-20261020-b-grimm-host-to-guest`。以下时间均为 UTC。

| 时间 / 日志位置 | 实际状态 | 含义 |
|---|---|---|
| `10:27:47.738`，`guest.log:80` 附近 | 乌恩载入时本地索引曾为 31，`RestoreTransientContext` 恢复到 30；随后主机上下文又同步为 31 | 正常换房已经留下旧的临时恢复点 |
| `10:27:50.625`，`guest.log:99` | 守望者新控制器已存在，索引 31，`guard=True` | 正确的新房行程已经建立 |
| `10:27:50.627`，`guest.log:100` | 同一次 Restore 后索引变为 **30**，`guard=False` | 临时恢复覆盖了原生正常推进 |
| `10:27:51.403`，`guest.log:106` | 副机已是新房权威；场景守望者、索引仍为 30；Capture JSON 的 `BossSequence` 为空、`BossIndex=0` | 权威没有收到另一权威的正确心跳来纠正自己；无效采集被编码成非行程 |
| `10:27:51.410–425`，`host.log:65–66` | 主机收到空行程后清空第五行程，原生完成回调改为 `BossSceneController:DoDreamReturn` | 有效五门被误改为神像战返回 |
| `10:27:59–10:28:23`，`guest.log:109–114` | 六个不同的 `Black Knight` 通过 HealthManager 原生死亡 | 完成来自真实 Boss 死亡流程 |
| `10:28:33.296`，`guest.log:118` | 副机原生推进到 31，目标却是 **再次进入守望者** | 正常应从 31 推进到 32 乌姆 |
| `10:28:33.320`，`host.log:155` | 主机执行 `DoDreamReturn`，目标为夹具默认的 `Dream_NailCollection` | 提前退出五门 |
| `10:28:33.656–36.164`，`guest.log:121–129` | 副机重进守望者后也收到空行程，随后执行梦境返回 | 错误上下文继续传播，两端最终都错误离开 |

两端最终 `.failure-state.json` 显示无死亡/倒地，停在夹具默认梦境场景的载入状态。此处默认返回场景/空入口门属于内存夹具准备限制，**不能把该卡住现象当成用户守望者房内卡住已被复现**。

反向组的主机在 `host.log:97–98` 同样发生 31→30，随后 `host.log:116` 错误重载守望者、`:126` 梦境返回。因此“谁开房”不是必要条件；关键是前一房副本变成下一房权威。始终同一人先入的格林对照也观察到旧索引恢复，但后续权威同步会纠正，最终正常通过；只测直接守望者会漏掉这个跨房状态。

## 原生入口补测与排除项

额外的 `*-grimm-host-to-guest-native` 仅用于核实原生返回上下文，没有一组完成目标守望者检查，不能计入六个通过或两个复现。

| 组 | 实际结果 | 如何处理 |
|---|---|---|
| c | 新入口探针编译失败后曾启动旧 Smoke，已标记 `runner.failed` 并停止 | 无效实验；原始日志保留，不作原生入口证据 |
| d | 夹具用了不存在的 `door_dreamEnter`，在 Roof 入口搜索失败 | 准备失败，排除为本次生产故障 |
| e | 夹具用了不存在的 `left1`；运行时枚举取得真实门名 | 准备失败，排除 |
| f | 命名的原生梦境返回门存在，但新角色下未激活，入口搜索失败 | 准备失败，排除 |
| g | 改用实际激活的 `bot1`，两端经原生五门菜单、动画到首 Boss；之后人为快进到格林时副机控制器完成回调为空，未到守望者 | 返回值有效；快进后的失败触发含夹具操作，保留为待区分问题，不计本次守望者复现 |

g 组 `host.log:33–35`、`guest.log:36–38` 读到原生设置：`dreamReturnScene=GG_Atrium_Roof`，`bossReturnEntryGate=door_dreamReturnGG_GG_Final_Challenge_Door`。这支持错误神像战返回与五门入口返回的联系，但没有证明用户现场完整时序，也不能替代一次完整连续五门运行。

测试工具现在拒绝使用比 Smoke 源码旧的插件；实验性入口项不在默认八组中，必须明确 `-Only` 选择。每组均保留自己的 Smoke 哈希，不用后续构建追认之前的运行。

## 本次可以确认及尚缺的范围

已确认这条具体链的触发不需要死亡、救援、F8、故障注入或结算后旧包晚到：正常自定义出口换房的临时快照回退，加上下一房权威切换，已足以制造空行程并破坏另一端原生完成回调。此前审计中的“结算后旧上下文晚到”仍是独立未验证风险，不是本轮复现的必要条件。

尚缺用户双机现场的精确 DLL/状态、从真实入口连续推进的五门运行、两台电脑网络与三人场景，以及“一人返回入口、一人留在原地”的完整终态。未验证守望者全部招式、动态弹幕、晚入、断连、弱网等其他专项。本轮是局部行程复现，不能认证整个五门或所有 Boss。

## 工具、复跑与收尾

```powershell
# 固定交付版八组；套件构建 Smoke 一次。
.\tests\Smoke\Run-P5-Repro.ps1 -Prefix 'p5-repro-new-run-a'

# 仅复跑用户方向与角色互换的两组。
.\tests\Smoke\Run-P5-Repro.ps1 -Prefix 'p5-repro-new-run-b' -Only grimm-host-to-guest,grimm-guest-to-host
```

新增 `P5WatchersReproProbe.cs`、`Run-P5-Repro.ps1`，`Probe.cs` 注册模式；`Run-Two.ps1` 支持固定完整 payload，启动前只按两个明确隔离 executable 路径清理进程。最后一次 Smoke 编译为 0 警告、0 错误；没有运行无关的全矩阵、打包或安装。

收尾的五个相关生产源码哈希与各 fixture 的运行前记录逐一核对；隔离游戏进程停止、两份 Smoke 插件停用，具体记录见机器记录的 `cleanup` 与 `productionSourceVerification`。
