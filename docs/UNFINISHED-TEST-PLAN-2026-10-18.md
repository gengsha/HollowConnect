# 未完成测试组织计划（2026-10-18）

这份计划把 0.1.22 之后仍未闭合的测试按风险和依赖排序。每组使用新的 `.test-results` 运行名，记录 `binaries.json`、host/guest 日志、`.done` / `.failed` 标记，并在收尾执行 `Assert-ProbeLogs.ps1 -RequireRuntimeFiles`。同一历史问题不能只凭一次成功重试关闭，必须留下能解释失败原因的专项证据。

## 已通过的用户实档验收

用户已经在实际卡死存档上使用隐藏快捷键 `Ctrl+Alt+F12`，成功回到德特茅斯椅子并恢复可玩，记录见 [用户死档恢复验收](evidence/user-dead-save-recovery-2026-10-18.json)。这项标记为 **PASS（用户实档）**，不再作为“快捷键能否救回死档”的待测项。

仍保留一个独立的自动化边界：复制存档后的真实写盘、退出进程、重新读档，以及回椅后进度/资源/Boss 状态的磁盘一致性尚未跑完。它不能推翻这次真实死档恢复结论，只决定是否能把保存链路也列为自动化通过。

## 批次 A：死房和 Boss 状态连续性（最高优先级）

| 顺序 | 未完成项 | 入口和重点 | 关闭条件 |
| --- | --- | --- | --- |
| A1 | `native-reentry-camera-freeze` | `pantheon`；host-first / guest-first；`NATIVE_REENTRY_STRESS=1`、`CAMERA_RING=1`，第二次进入同一原生房间时记录镜头模式、控制锁和队友可见性 | 能说明旧 FROZEN 来源，或在来源修复后双端多轮重入均恢复，严格日志无异常 |
| A2 | `reconnect-inactive-needle-unresolved` | `pantheon`；`LIFECYCLE_CHECK=1`、`COLLIDER_RECONNECT_STRESS=1`、`CONTINUATION_TRACE=1`；两种先入顺序各做接管/断线/重连 | 每轮都记录 Needle 激活、Hornet FSM 从 Thrown 到 Throw Recover 的自然转移；不得只看最终位置 |
| A3 | `false-knight-repeat-head-unresolved` | `boss`；晚加入和权威转移，开启 `SMOKE_BOSS_LATE=1`、`SMOKE_DAMAGE_TRACE=1`；保留破甲前后 FSM、血量和头部激活轨迹 | 第二次破甲在两端都出现原生头部，或捕获可复现的具体阶段/消息原因并完成针对修复 |
| A4 | `p5-watchers-premature-return-unresolved` | `p5-motion`；`P5_RESCUE_CHECK=1`、`VISUAL_CHECKS=1`；只跑 Watcher Knights → 后续房间边界，覆盖先入顺序、互救和温泉/乌恩路径 | 双端都到达预期下一 Boss 房且不提前回入口；若复现，保留完整死亡/救援/场景快照链 |

A 批完成后再跑 `Run-Room-Suite.ps1` 的 final native、lifecycle、boss-late、rescue、death-story 选择项，确认修复没有破坏已通过的邻近流程。

## 批次 B：进度、资源和传送边界

| 顺序 | 未完成项 | 入口和重点 | 关闭条件 |
| --- | --- | --- | --- |
| B1 | `cornifer-purchase-peer-visibility-unresolved` | `progress`；`PROGRESS_BUYER=1`、`PROGRESS_PROMPT=1`、`MAP_BOTH=1`；两端同时保存房间进入/头像激活/快照提交记录 | 能定位头像消失发生在 entry、snapshot 还是 avatar lifecycle，并在跨房后持续可见 |
| B2 | `mines-fsm-string-layout-unresolved` | `coverage`；`COVERAGE_SCENES=Mines_11`、`FSM_SCHEMA_TRACE=1`、`FSM_NULL_STRING_ASSERTIONS=1`，同时记录实体和 FSM 变量布局 | 复现并解释 String index 越界的两端布局差异；修复后不得靠跳过值或放大数组掩盖 |
| B3 | `godhome-final-single-f8-timeout-unresolved` | `godhome`；开启故障断言和 request/eligibility trace，明确记录 `CanStartWarp`、请求发送、服务器拒绝、响应和到达 | 每次 F8 都能区分本地不可用、服务端拒绝、消息未达和响应不可用；两种先入顺序均闭合 |
| B4 | `cliffs-room-overlap-unresolved` | `coverage`；`SCENE_GEOMETRY_TRACE=1`、`GEOMETRY_GUEST_ALONE=1`、`CLIFFS_INTERIOR_CHECK=1`；优先检查 Fungus1_28 两井和下砸入口 | 证明几何重叠/挡板来源，或证明候选房间和边界均无阻挡；保留客机单独进入证据 |
| B5 | `radiance-immediate-inventory-timing-unresolved` | `rescue` / `progress` 手动边界：原版 Radiance 返回长椅后立即打开背包、延迟一帧/数秒重复；记录输入资格和暂停状态 | 解释输入被吞的时序条件，或证明修复后连续重复均可打开背包 |

## 批次 C：保存链和发布验收

1. 用隔离的复制存档跑回椅后的真实写盘、退出、重新读档，核对长椅、能力、Boss 击杀、暗影和共享资源回执。
2. 在 A/B 批没有新增失败后，重新构建当前生产 DLL，跑完整 12 组 final-hash 双端记录；每组必须双端 `FINAL PASS`、无 `.failed`，并通过严格日志门槛。
3. 重新生成 runtime evidence，把用户实档 PASS 与自动化保存边界分开记录；只有 12 组和保存链均有新哈希证据时才评估 release 包，之前的 playtest 包继续保持 `releaseReady=false`。

## 批次 D：覆盖债务（最后处理）

- 真实两台机器、用户 FRP、目标分辨率和 144 FPS 长时间压力。
- 全 Boss 全招式、三档竞技场逐波通关、完整五门和完整剧情长流程。
- 尚未逐项运行的资源来源和参数组合，以及“最近 100 项”自动选择器。

这些是覆盖范围债务，不应阻塞 A/B 中具体历史 blocker 的归因和修复；但在宣称完整发行验收前仍须单独列出完成范围。

## 建议执行顺序

先做 A1→A4，再做 B1→B5；每完成一个具体 blocker 就立即写入对应运行证据，避免把多组失败混在一次大套件中。批次 C 的保存链最后执行，批次 D 根据发布目标决定是否纳入本轮。
