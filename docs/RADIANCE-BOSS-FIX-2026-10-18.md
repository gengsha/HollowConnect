# Radiance / Absolute Radiance 修复记录（2026-10-18）

本次改动针对辐光房间入场镜头错位、Absolute Radiance 二阶段坠落复活点错误，以及同类 Boss 场景的局部触发和全局 HazardRespawn 误判。

## 已修复

- `CameraLockArea` 在注册表中明确为 `networked:false`，并纳入本地触发白名单。每个客户端只根据自己的原生位置触发镜头锁区，远端玩家进入不会替本机启动入场锁定。
- `Absolute Radiance` 注册 `HazardRespawn` 组件，接收平台/深渊阶段复活点。
- HazardRespawn 全局触发 hook 现在只接受属于当前 Boss 的 trigger，并对缺失 FSM、复活容器、P2A Hazard 和 marker 做安全保护。
- 触发器编号同时检查上下界，避免负编号转成无符号包值；先校验目标 marker，再改变较早 trigger 的状态，跳过已失效的 Unity 对象。
- 平台阶段优先选择当前激活且可站立的 `Radiant Plat`，排除深渊、伤害碰撞体和平台内部几何。第一阶段保留近处普通安全地面的射线落点；没有安全承重点则拒绝传送。
- 跨房传送成功后，完成本机 `Boss Control/Control` 的入场展示。执行前校验原生 `Battle Start` 仅包含恢复本机控制、关闭 Intro Wall/CamLock Intro、解除暂停限制的动作；不跳转 Boss 本体的 Control/Phase Control，不激活或重置 Boss。
- 补齐跳过入场后的原生音乐与 HUD 恢复。
- 根据传送落点一次性释放旧镜头锁区并注册本机实际重叠的锁区，再启动一次镜头归位。撤掉每帧调用 `PositionToHero` 的临时方案：该原生方法是协程，每帧调用会竞争冻结/恢复状态。

## 验证

- 生产模块构建：通过；仅有既有 `ClientUpdateManager` XML 注释警告。
- Core 回归：`200 passed, 0 failed`。
- Smoke 工程：通过构建。
- 静态注册表断言：`CameraLockArea networked=false`，`AbsoluteRadiance components=HazardRespawn`。

## 双端实机方法与证据

在已有隔离 `game-host`、`game-guest` 中启动两个实际 Unity Player，使用本机 UDP。测试拦截存档写入，使用内存角色；未修改个人存档。当前电脑控制服务不可用，以下属于实际游戏进程中的自动探针，不是手工窗口操作或双机 FRP 验收。

`CoverageRadianceChecks.cs` 覆盖：

1. 原生长椅起身、原生 Boss 入场结束后，两端分别走生产 F8/UDP，在初始竞技场地面落地。
2. 通过 `HealthManager.Hit` 正常受击事件推进 Phase Control 到第一轮 Stun，由原生 FSM 生成平台、抬升深渊，不直接改 Boss HP/FSM。
3. 核对两端复活点为原生 P2A marker；让站在初始地面的玩家被真实上升深渊碰撞，执行原生 hazard death/respawn；要求实际平台承重、角色在镜头内、恢复控制、生命减少一次，随后 5 秒不回血且不连续扣血。
4. 副本端先离开到 Town，再通过生产 F8/UDP 返回已经开战的二阶段；核对原生入场展示进入终态，持续观察 28 秒，角色保持可见可控，权威端 Boss 阶段和血量不重置。

坠落与展示断言中隔离普通弹幕受伤；深渊碰撞和原生复位仍执行。正常 hazard recovery 自带短暂冻结，不按入场死锁判定。`LOCKED` 是竞技场正常镜头模式，验收依据是本机可控与 viewport，不要求强行 FOLLOWING。

早期 coverage PASS 只证明同步采样，不代表平台复位、迟入或全招式通过。

## 最终版本运行验收

| 隔离运行目录后缀 | 先入权威 | 本次验收 | 结果 |
|---|---|---|---|
| `final-host` | host | 两端初始地面 F8、原生二阶段平台/深渊复位、生命稳定、guest 从 Town 迟入后 28 秒可见可控、Boss 阶段/血量保持 | 双端通过；停止进程后严格日志通过 |
| `final-guest` | guest | 相同检查，交换权威与迟入角色 | 双端通过；停止进程后严格日志通过 |
| `warp-regression` | host | Crossroads_01 通用传送资格、过场目的地拒绝、等待超时/中断清理、两端真实 UDP F8 | 双端通过；停止进程后严格日志通过 |

目录前缀为 `.test-results/radiance-runtime-20261018-`。三组均使用相同生产 DLL，具体哈希和结果见 [实机证据](evidence/radiance-runtime-2026-10-18.json)。
两端原生深渊碰撞后生命从 9 到 8，复活点为 `(60.5,34.4)`，实际站立在平台约 `(60.5,34.7)`；未靠持续回血、手动设置复活点或强行跳 Boss 阶段取得通过。

`Radiance Roar` 道具仍有一次 FSM 名称不匹配告警并被排除注册；本次没有静音该告警或据此修改 Boss 状态映射。

## 保留的失败与纠错

- `phase-host-r1`：测试使用 ApplyExtraDamage，未发原生阶段事件；属于夹具错误，改用 Hit 后原生阶段推进。
- `fall-host-r1`：两端已实际复位成功，但计数把同次 hazard death 中重复碰撞回调计为多次；改为只计算 false→true。
- `fall-late-host-r1` / `late-trace-host-r2`：确认迟入时仍使用 CamLock Intro，平台上的骑士 viewport.y≈1.1。迟入一段时间后会再播放本机标题。这是实际生产问题。
- `late-trace-host-r1`：测试在 Town 原生 transition 完成前按下一次 F8，未获传送；补全 requester 的 CanStartWarp 前置。
- `fix-host-r1`：镜头落点恢复，但持续展示断言把正常战斗击退后的 hazard freeze 算为入场死锁；补充展示伤害隔离与原生 hazard 恢复区分，不把这一轮记为完整通过。
- `accept-host-r1`：早期夹具的一次 F8 未在 50 秒内获授予，缺少完整请求/授予诊断，不能只凭超时判定生产原因。最终夹具在两端隔离普通战斗击退，并在按键实际被消费的同一帧检查本机传送资格；保留原失败，重新完成生产 UDP 传送验收。

以上完整运行目录均在 `.test-results/radiance-runtime-20261018-*`，未覆盖或删除失败。

本次未覆盖原版剧情辐光、爬升后续阶段、最终击杀结算、断线接管、双机 FRP 和目标分辨率长期压力；不据本次专项宣称所有 Boss 或整条流程已经通过。
