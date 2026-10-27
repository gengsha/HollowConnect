# 粪球守卫者挡板与迟入专项（2026-10-20）

当前生产 DLL 下新增四组同机双端专项全部通过；相关核心回归 4 passed / 0 failed。本轮补充探针和证据，生产代码保持此前共用挡板修复后的实现，未升版、打包。

## 先纠正对象分类

`Waterways_05 / One Way Wall` 是可用骨钉打破的永久捷径墙。实机场景的 `break_floor` FSM 包含 `NAIL HIT`、打击计数、`PlayerData` → `Break`，写入 `dungDefenderWallBroken=true`，位置约 `(114.0, 7.9)`。它不负责 Boss 开战关门，应保留世界进度同步。

真正参与战斗的是左侧 `Battle Gate (1)`、右侧 `Battle Gate` 及 `Battle Scene/Wall Saver`。原版碰撞使 `Battle Scene` 从 Idle 进入 Battle Start，激活 Wall Saver、广播 `BG CLOSE`，再进入 Dung Start；尸体完成事件使其进入 End 并关闭 Wall Saver。后进的已完成场景走 Activate，不再接收开战碰撞。

本轮早期误把捷径墙归为战斗门，曾增加精确场景的本地例外；看到原版 FSM 后已完整撤回。最终测试明确断言该墙的永久条目仍允许同步，且未被识别为本地战斗挡板。不得把早期例外实验计为产品修复或最终验收。

## 当前构建的实际验证

每组均交换先入者所拥有的场景权威，等待真实场景和网络快照就绪，通过原版碰撞触发战斗。终结伤害由 `HealthManager.ApplyExtraDamage` 注入，随后等待原生死亡、尸体动画、结算事件和开门，不设置 Boss 完成状态或强制打开门。

| 场景 | 先入者 | 运行目录（`.test-results/` 下） | 结果 |
|---|---|---|---|
| 一人在同房门外，另一人触发战斗 | 房主 | `defender-barrier-20261020-e-outside-host` | 双端通过，严格日志通过 |
| 一人在同房门外，另一人触发战斗 | 访客 | `defender-barrier-20261020-f-outside-guest` | 双端通过，严格日志通过 |
| 击杀者留在房间，另一人结算后才进入 | 房主 | `defender-barrier-20261020-g-late-host` | 双端通过，严格日志通过 |
| 击杀者留在房间，另一人结算后才进入 | 访客 | `defender-barrier-20261020-h-late-guest` | 双端通过，严格日志通过 |

门外两组要求：远端开战后，另一端的 Battle Scene 仍为 Idle，Wall Saver 未激活、左门碰撞关闭；角色以原生移动实际穿过入口后，自己的碰撞才触发本地关门。死亡后两端均进入 End、恢复控制，实际穿过左右出口。

迟入两组要求：进入后不存在活着的剧情 Boss，两项死亡标记已收敛，门开放、Wall Saver 未激活；再碰战斗触发区并等待两秒，仍为 Activate，不重开战、不关门。两端实际穿过左右出口。右门在完成后迟入时可被原版移除，探针同样检查该门洞可通行。

通行探针将免伤角色放在实际门洞高度并关闭重力，再通过原生输入移动和碰撞横穿；它验证门的阻挡，不能当作完整自然平台路线或人工全招式战斗通过。

同一生产哈希下更早的 `dung-defender-20261020-a-defender` 已覆盖原生死亡进度进入房主内存账本、双方离房重进不复活、原版下砸进入隐藏房、梦钉进入白色防御者及断开后的控制恢复。该旧探针未实际碰撞启动 Battle Scene，所以由上面的四组补齐，不能拿旧死亡标记检查代替挡板验收。

## 不只这一场战斗存在同类风险

| 机制 | 已知证据或风险 | 当前覆盖边界 |
|---|---|---|
| 远端 Boss 广播或外部 FSM 动作触及本地门 | 灵魂大师已实际复现；共用事件重放和动作入口已有保护 | 见 [本地挡板修复](LOCAL-BARRIER-EVENTS-2026-10-20.md)，其中列有最终哈希下的实机与边界夹具 |
| 迟入者错过早期死亡，自己的敌人数稍后才初始化 | 真菌荒地双蘑菇精英战已复现并修复 | 见 [关门战结算](LOCAL-BATTLE-COMPLETION-2026-10-19.md)；其它控制器不能仅凭名称认定受覆盖 |
| 血量归零后仍有阶段、尸体、子怪或奖励流程 | 灵魂大师、假骑士、格鲁兹之母及分波战斗需分别观察原版完成 | 本轮只新增粪球守卫者；其它场景证据见各自报告，旧哈希不能替代新哈希 |
| 梦境门、螳螂笼、竞技场墙的本地开关时序 | 可能受同类事件/动作影响，不能从一个 Boss 推定全部通过 | 共用保护有边界夹具；不等于本轮完成所有原生战斗、神居档位或梦境流程 |
| 永久捷径墙被误当作临时挡板排除同步 | 本轮的 `One Way Wall` 分类错误已撤回 | 按实际 FSM、永久标记和碰撞行为判断，不扩大为所有叫 Wall 的物体纯本地 |

因此，本轮结论是粪球守卫者这四种进入/结算顺序通过，不是所有 Boss 均无风险。真正中途断网接管、死亡暗影救援、F8 在死亡瞬间传送、第三方模组、真实双机 FRP 以及个人存档写盘后重读，不在这四组验收内。

## 复跑与证据

```powershell
./scripts/Build.ps1 -Target Smoke -NoRestore
./tests/Smoke/Run-Defender-Barrier.ps1 -RunName <新的唯一运行名> -Case outside -First host -NoBuild
# 等上一组完整退出，再换独立运行名，分别运行 outside/guest、late/host、late/guest。
./scripts/Build.ps1 -Target Core -TestSelection tests/barrier-sync.selection.txt -NoRestore
```

`-Case inspect` 仅导出原版结构，不能算挡板功能通过。各组保存双方探针、FSM、状态轨迹、Unity/引擎日志、生产 DLL 哈希和 Smoke 哈希；两端结束断开后只停止对应隔离进程，再执行 `Assert-ProbeLogs.ps1 -RequireRuntimeFiles`。使用现有隔离 `game-host` / `game-guest`、真实 localhost UDP、内存角色 99，探针拦截保存，不加载或修改个人存档。

最终生产 SHA-256：

| 文件 | SHA-256 |
|---|---|
| `HKMP.dll` | `03ED66E2BD52A47D064662242F36129F45519A2BF872FD5EBC4228AAF523F5F7` |
| `HollowConnect.dll` | `222B5D57407955784F4A8FDB7437D5CD08F715755946FB6CD48B6422B756E02D` |
| `HollowConnect.Core.dll` | `64661526E34F5EE64BD33C343E10AFE823DCC056671564582585D9E8C0A82787` |
| `HollowConnect.Smoke.dll` | `58BF4D38AA1B59063B80045272972B9FA41A0CA763E945EE81D499D636E73040` |

机器证据及游戏程序集身份见 [专项证据](evidence/dung-defender-barriers-2026-10-20.json)。核心日志为 `.test-results/defender-barrier-20261020-core.log`。

历史诊断全部保留：`defender-wallfix-*` 属于错误捷径墙例外实验；`defender-barrier-20261020-a-inspect` 仅作结构诊断；`b-outside-host`、`d-late-host` 用的是中间构建；`c-outside-guest` 在尸体结算期间被下一轮启动中断，没有 done/严格通过，不能计为通过或已确认产品失败。最终验收仅使用上表 e/f/g/h 四组。
