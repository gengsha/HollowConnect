# 梦境 Boss 与螳螂爪回归修复（2026-10-21）

本轮针对[扩展审查](DREAM-BOSS-RISK-2026-10-21.md)确认的失败冠军漏同步、戈布离房残留附加场景，以及救援改动后综合回归的螳螂爪等待超时。生产修复只涉及 Boss 注册和附加场景生命周期；螳螂爪超时来自测试主动锁住输入，没有改生产资源发放规则。

源码仍为 **0.1.29 / l2**，没有重新生成 Windows 或源码 ZIP。原包和历史报告保留各自时点的哈希与失败。精确运行、生产 DLL、探针及日志哈希见[本轮证据](evidence/dream-claw-fix-2026-10-21.json)。

## 实际问题与改动

### 失败冠军本体与头部

剧情场景 `Dream_01_False_Knight` 的对象名是 `False Knight Dream`。原注册表只有 `False Knight New`，尽管两者使用相同 `FalseyControl`，按名称匹配仍会漏掉剧情梦境本体。之前真实开战后，主机受到17点原生伤害从360降至343，客机保持360，两端各自运行战斗 FSM。

在 `src/HKMP/Resource/entity-registry.json` 增加梦境对象的明确条目，复用已有 `FalseKnight` 与 `FalseKnightHead` 同步实现，登记其 `Head/Health Check`。原对象条目继续存在。新双端伤害检查中，两端都登记本体和头部，只有场景权威运行原生战斗 FSM；客机显示副本的 HP 同样降至343。

完整收尾已走过原版多轮破甲／露头、狂暴和最终死亡，两端自行返回 `Crossroads_10/door_dreamReturn`。按原版醒来输入起身后，未领奖主机通过灵魂对话获得300精华；已领奖客机的灵魂保持隐藏、精华保持230。最终一轮在双方都完成后继续等待并复核，主机仍为300、客机仍为230。

### 戈布战后离房

混合个人历史下，客机需要按权威房间补加载 `Cliffs_02_boss`。旧 `BossSceneLayout` 用 Unity API 加载，但未将加载结果交回原生 `SceneAdditiveLoadConditional`。原生 `OnDisable` 仅在私有 `sceneLoaded=true` 时卸载，并通过 `loadAlt` 决定卸载哪个变体；旧路径两个字段都未配对，导致客机离房后仍残留附加场景。

修复将已存在或异步加载完成的场景绑定到当前原生 owner 的 `sceneLoaded/loadAlt`，让离房继续由原生组件卸载。异步开始前检查当前房间访问；加载中离房、owner 已销毁时，清理刚完成的孤立附加场景。保留有效房间内的原生 `LoadedBoss` 通知。

三组双端检查通过：同旧复现路径从戈布切到 Town、继续打胡长老；实际碰撞 `right1 → Town/top1` 出口；客机实际调用运行时 F8 跟随已抵达 Town 的主机。每组均确认旧附加场景卸载、目标房间进入完成且输入正常。出口夹具把角色放到真实触发区，F8 用真实运行时按键入口；不等于手动走完整悬崖路线。未关闭历史 `cliffs-room-overlap-unresolved`。

### 螳螂爪超时

诊断确认：探针捕获的、全局当前的和 HeroController 使用的是同一个 `PlayerData`，主机能力为 true/true、客机为 false/false；不是旧引用。通用 `Probe.Freeze()` 主动调用了 `RelinquishControl()`，让角色长期不满足资源服务的原生空闲条件。生产 `ApplyProgress` 正确等待对话、领取 UI 与过场释放控制，因此测试自己阻止了能力发放。

从测试的 Freeze 中去掉强制交出输入，继续固定位置、关闭重力并免伤；内存角色预置已访问德特茅斯，避免首次标题影响后面的死亡返回检查。客机随后收到 `hasWalljump=true` 和 `canWallJump=true`，综合回归双方完成。本次没有绕过生产空闲门槛，也没有用直接修改客机能力作为通过条件。

## 运行结果

所有游戏检查串行使用既有 `.test-results/game-host` 与 `game-guest`，真实 localhost UDP、禁止写盘的内存角色。主客机历史由夹具准备，没有读取私人存档。不是双机 FRP 或人工完整战斗通关。

| 运行 | 结论与范围 |
|---|---|
| `dream-claw-20261021-b-full` | 双方完成、严格日志通过；螳螂爪、普通怪血量、远端骨钉、权威转移、地图、实际救援、失败返回、F7/F8 与断线清理 |
| `dream-claw-20261021-c-champion-damage` | 双方登记失败冠军、单次原生17伤害后HP一致；副本不运行原生战斗FSM |
| `dream-claw-20261021-c-gorb-sequential` | 未领奖主机＋已领奖客机，戈布战后正常离房并继续胡长老；个人奖励与墓碑交互通过 |
| `dream-claw-20261021-f-gorb-native` | 实际出口离房、旧附加场景卸载、控制正常 |
| `dream-claw-20261021-f-gorb-f8` | 客机F8到Town、旧附加场景卸载、控制正常 |
| `dream-claw-20261021-j-champion-spirit-reward` | 原生阶段、死亡、返回、起身与主机灵魂对话领奖通过；双方完成、严格日志通过 |
| `dream-claw-20261021-k-champion-final` | 双方完整收尾、各自控制恢复与个人精华结算后稳定性通过；双方完成、严格日志通过 |

`scripts/Build.ps1 -Target Core -RunTests -NoRestore`：**238通过，0失败**。首次HKMP重编译出现既有XML注释参数警告CS1573；后续增量构建0警告、0错误，没有把首次构建写成无警告。

本轮累计7组双端通过，另保留7次失败／无效夹具／诊断中止。三个生产DLL在上述通过组之间未改变；各轮Smoke探针哈希分别记录。受测生产SHA-256：

| 文件 | SHA-256 |
|---|---|
| HKMP.dll | `465575C9DC12FC775ACE815F1EDF6F178A41E3C7ABE22696CF8A4D22226766CB` |
| HollowConnect.dll | `19C20C5619BB9B60923CF7711773C0864A09992C7EFC98E8E0112B26F44264F8` |
| HollowConnect.Core.dll | `23F3A6E3DCC200D528AD0BB7C65E2853445476C201DC4B3059AEAFEE0816CF8E` |

## 实际遇到的失败与诊断

- `a-claw-diagnostic`：旧Freeze仍锁输入，客机螳螂爪超时，诊断日志保留。
- `d-champion-lifecycle`：探针在原生Idle Pause反复打掉护甲，跳过狂暴计数，未到最终阶段；下一轮启动过早中断了此轮。已明确标记无效夹具与中断，不写成自然超时或产品失败。后续严格等待包装器退出，才复用游戏副本。
- `e-champion-lifecycle`：改为只在Idle破甲后完成原生最终死亡与两端返回，但回到尚未打过原版假骑士的场景，且未发送醒来输入，等待控制超时。
- `f-champion-lifecycle`：补上正常剧情重战所需的假骑士胜利旗标后，返回的是正确已击败场景，仍等待控制超时。没有因此修改生产RegainControl。
- `g-champion-return-physics`：恢复测试角色重力不能解决等待；只读FSM导出证明英雄 `Dream Return=Ready` 正常等待按键，`GET UP` 才会进入原生起身、`DREAM WAKE`、保存及控制恢复。诊断完成后明确写入中止原因并由包装器清理，不计通过。最终探针发送实际 `ListenForJump.wasPressed` 输入事件，不直接设置英雄FSM或强行解锁控制。
- `h-champion-native-wake`：双方原生起身与控制恢复通过，主机精华自动到账断言失败；探针遗漏了原版返回后的灵魂对话领取。
- `i-champion-reward-trace`：继续保留错误的自动到账断言以导出领奖结构。实际主机 `Ghost False Knight NPC/Conversation Control=Idle` 可交互、客机因已领奖保持隐藏；原生 `Defeated → Get` 路径才发放300精华并写个人领取旗标。此诊断失败没有写成生产漏发或修改生产资源规则。

以上所有失败保留各自探针哈希、日志与受测二进制信息，未用最终探针哈希倒填旧轮次。

## 验证边界

本轮“严格日志通过”表示通过现有模组异常检查，并不等于Unity日志没有任何原生异常。隔离角色最初启动时仍有 `GameManager.SetupHeroRefs` 空引用；本轮未解决这项启动兼容性，也没有将它与修复后的戈布离房混为一谈。戈布的实际离房断言已通过。

完整剧情梦钉入场、失败冠军中途接管／断线／迟入、所有弹幕及动态对象、另外四个剧情梦境变体（失落近亲、灵魂暴君、白色防御者、灰色王子）的完整胜利返回仍未验收。本轮不能证明所有梦境Boss安全，也不关闭历史十项问题。

每轮包装器在停止精确匹配的隔离进程后核对玩家全局偏好哈希未变，Smoke拦截原生SaveGame全部入口与模组全局设置保存。结束后停用隔离副本中的Smoke插件。未改私人存档、既有交付ZIP或版本号。
