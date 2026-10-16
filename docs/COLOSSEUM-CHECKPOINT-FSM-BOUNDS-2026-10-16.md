# 竞技场个人复活点与 FSM 快照边界修正

2026-10-16。按用户“把能修复的都修复了”实施；未升版本、打包或安装。生产构建提交 `498daad`，包含 `ad652ce` 的竞技场入场修正和 `520af8c` 的快照边界保护。

## 已修正的行为

正常报名进入试炼时，后入非权威玩家的原生 `Colosseum Manager / Manager` 不执行入场初始化，个人复活点仍留在之前的长椅。`colosseum-normal-bronze-host-final-20261016` 已通过实际报名、原生门和双方正式死亡复现，详见[原始调查](COLOSSEUM-ENTRY-INVESTIGATION-2026-10-16.md)。

现在只在该实体首次确定为副本、且原生对象原本激活时，从原版 `Set Respawn` 的启用动作读取四个个人复活字段，并按原生动作调用 `SetCurrentMapZoneAsRespawn`。不启用整套副本 Manager。再次初始化同一实体不重复写入；离场后仍由各自的原生长椅管理复活点。

动作表中的 SendMessage 实际调用 **SetCurrentMapZoneAsRespawn，不是 SaveGame**。程序集 IL 确认该方法把当前场景的 MapZone 写入本机 PlayerData.mapZone。早期候选的 SaveGame 条件已替换为这个确切原生调用；本修正不额外存档。

另一个独立漏洞是 `UpdateHostFsmData` 只检查实际变量表长度，就先改变量、再写登记时的快照；副本或原生表后来扩展时，快照仍短，会越界并中断当次更新。现在写入前同时核对两端长度，保留同批有效字段，对无法映射的字段输出实体、场景、FSM、类型、下标及长度诊断。六类标量均覆盖；采样和接管恢复也只使用实际表与快照共同存在的范围，避免表扩大或缩短后越界。

这只是已证明的公共边界漏洞修正。**用户 Mines_11 的实际布局变化来源仍未定位，不能把该历史问题关闭。** 不制造变量、不扩容猜测映射，也未改变协议。

## 验证

最终同一生产构建的 **14 组同机双客户端专项全部通过**，含 12 组竞技场联网流程、1 组断网原版入场对照、1 组独立 FSM 夹具。生产 DLL 固定为 `498daad` 构建；之后仅以 `BuildProjectReferences=false` 修改测试插件。最近 100 项按当前 Git 记录选取，另加 22 项相关检查：**122 通过、0 失败**；14 项严格日志门槛通过。记录、逐组 DLL/日志哈希及完整选择名单见[机器证据](evidence/colosseum-checkpoint-fsm-bounds-2026-10-16.json)。

`fsm-bounds-string-baseline-20261016` 使用未修边界的旧 HKMP，在双方真实 PlayMaker 夹具里确认副本新增 String 和原生/副本运行时扩展两种越界。该完成标记表示旧漏洞复现成立。`fsm-bounds-final-20261016` 使用最终生产 DLL，拒绝无映射字段而不发生部分写入，同包有效的六类变量及接管恢复通过；原生表缩短后采样/接管不越界；null 字符串赋值、改变、清空、去重、迟入缓存和接管旧回归通过。夹具 NetClient 未连接，不能作为 Mines_11 自然复现或字符串实际双机传输验收。

竞技场用不同个人长椅的两个内存角色，经过休息区但不先坐椅，再实际报名并走原生门。测试角色已解锁对应试炼；不覆盖首次付费解锁。报名牌附近和入口附近由探针定位；向上交互经原生输入监听，确认使用原生 UI 选择事件，入门时注入横向意图，碰撞与 TransitionPoint 均由原版执行。正式死亡通过原生 Die、协作倒地与 F6 放弃，核对实际到达场景、原生处罚一次、暗影在休息区以及长椅起身后的控制。

| 范围 | 本轮对应运行 |
|---|---|
| 铜、银、金各两种先入顺序与双方正式死亡 | `colosseum-final-{bronze,silver,gold}-{host,guest}-20261016` |
| 金级延后 12 秒入场 | `colosseum-final-late-20261016` |
| 关闭共享永久进度 | `colosseum-final-shareoff-20261016` |
| 断开联机的原版入场对照 | `colosseum-final-native-20261016` |
| 休息区实际坐椅后报名 | `colosseum-final-bench-20261016` |
| 双方轮流真实断线、接管、同房重连、换个人长椅、重新报名 | `colosseum-final-lifecycle-host-r6-20261016`（铜）、`colosseum-final-lifecycle-guest-20261016`（金） |
| 不按 F6 的实际全队死亡与自动重试 | `colosseum-final-wipe-20261016`，金级副机先入；双端各处罚一次、暗影在休息区、一次重试轮次、起身可操作 |

所有计入完成的联网组都已双方完成、正常断网、服务器停机；停止隔离进程后通过严格日志与源输出/两种测试输出/双隔离副本共 15 处生产 DLL 哈希核对。断网原版对照不要求联网退出标记。所有最终组停机后再过一次严格日志检查；两个隔离副本的 Smoke 均已重命名停用。

## 保留的失败与边界

`colosseum-checkpoint-*` 与 `fsm-snapshot-bounds-final-20261016` 是补齐区域动作之前的 `520af8c` 构建，单独保留，不移记成最终哈希验收。`colosseum-fixed-bronze-host-20261016` 是更早候选，同样分开。

早期 lifecycle 的 5 组失败均保留。状态追踪及原版动作表确认：未到过城镇的副机被探针直接放到长椅旁，触发首次区域标题；标题的 BIG TITLE START 把正在坐下的长椅转到 Inert，BIG TITLE END 再回 Idle。后续两种等待尝试分别早于标题触发、或停在入口未进入标题触发区，均不算通过。最终探针先接近长椅、观察其原生 Inert 周期结束，再按向上；不强制恢复控制、不放宽原超时或日志门槛。失败终止时产生的 UDP ConnectionReset 也保留，不能算正常退出。

本轮全部实机运行只用 `.test-results/game-host` 和 `game-guest`，静音、内存角色，并拦截三个 SaveGame 重载。正常游戏目录只读，不读写个人存档。

原生程序集：Assembly-CSharp SHA-256 `5944411BD93830369390A4B51766EE68C4AB26195B299E25A07B5E7D0E00086D`；PlayMaker SHA-256 `0EF0E7829D125E1F632C8A189260EC6C6882630BE6932C8C7AE032EFBC53469A`。

最终生产载荷 SHA-256：

| DLL | SHA-256 |
|---|---|
| HKMP | `527FC05CEB793BF058C7F682713B844099BB01C505590E2415FAC8B4CF993A88` |
| HollowConnect | `D7322496FBFADAC384F1FA121EF83EFDE9ECF11F9A31D66B3711C1ED97241E2A` |
| HollowConnect.Core | `E7897EAE1977ECBFB45C1B4F27F3AD34599FFDD09F093ED49AE097CBEFD29685` |

未逐波通关三试炼，未验胜利奖励、波次进行中迟入、首次付费、真实双机 FRP 或全部特殊死亡。旧虫王瞬时镜头冻结、大黄蜂接管飞针、假骑士迟入再破甲、买地图后持续不可见、Mines_11 原始布局及悬崖叠图仍未关闭；辐光刚回长椅立即开包的时机现象和历史神居单次 F8 测试超时也不宣称修复。当前交付仍为 0.1.17，不含尚未打包修正，`releaseReady=false`。
