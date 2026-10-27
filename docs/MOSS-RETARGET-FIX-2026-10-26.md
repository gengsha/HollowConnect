# 巨型苔藓冲锋者死亡回椅后停在地下

用户确认对象是巨型苔藓冲锋者 Boss，房主索敌模式为“灵活”。本次已在旧生产 DLL 上复现：负责运行 Boss AI 的玩家死亡并回到自己的长椅后，留下的队友接管了 Boss，但它连续 16 秒停在 `Hidden`。该行为与截图描述一致。

## 根因与修改

Boss 的 `Mossy Control/Init` 先查找 `Attack Range`，随后通过原生 `SetParent` 把范围对象从 Boss 身上分离。接管端会恢复当前状态并重绑引用；原来的 `FindAlertRange` 重绑只查直接子节点，此时已找不到分离的范围对象。它的组件引用为空，`Hidden` 中的 `CheckAlertRange` 每帧返回 false，因此没有下一次出土。

运行日志同时确认：`FindChild` 的本机 `Attack Range` GameObject 缓存仍然存活。因此修改只落在 `EntityInitializer` 的引用重绑：优先原生直接查找；查找失败时，从同一 target、同一 childName 的 `FindChild` 缓存恢复本机 `AlertRange`；保留仍有效的组件引用，并在子节点引用绑定完成后再做一次范围绑定。缓存对象使用 Unity 的有效性判断，避免已销毁对象触发 `MissingReferenceException`。

没有新增索敌 hook。旧 DLL 的另一组对照确认：真正选中了远端玩家后，该玩家死亡回椅、原权威留场，Boss 仍能继续攻击。这个反例把本次已证实的问题收敛到接管时的范围引用丢失。原生碰撞范围、出土状态转换和既有目标资格筛选继续工作。

代码：[EntityInitializer.cs](../src/HKMP/Game/Client/Entity/EntityInitializer.cs)。原生双端回归：[MossRetargetChecks.cs](../tests/Smoke/MossRetargetChecks.cs)。新增探针开关只用于隔离测试，不进入交付 DLL。

## 只读审计与复现证据

使用 `hollow-knight-boss-net-audit`，读取两份现有隔离游戏的精确 DLL，审计结果保存在 `.analysis/moss-retarget-20261026/native-audit/`。原生代码与 FSM 的证据分级如下。

| 发现 | 证据 | 结论 |
|---|---|---|
| `Hidden` 依赖 `CheckAlertRange` | 原生 `Mossy Control` FSM dump；原生 `CheckAlertRange.Apply` IL | 已确认原生路径 |
| 接管端 `Attack Range` 组件为空，但 GameObject 缓存存在 | `baseline-e` 接管前后 `MOSS BIND`，以及 16 秒 `MOSS OBSERVATION states=Hidden` | 已运行复现的根因 |
| `CheckAlertRange` 没有单独的位置重定向 hook | HKMP 只读代码比较 | 静态覆盖缺口，不能据此认定本次根因；远端目标死亡对照正常 |
| 用户提及竞技场类似现象 | 截图类比 | 本次没有取得同因证据，仍未确认 |

修复前依赖保存在 `.analysis/moss-retarget-20261026/baseline/`；真实死亡复现为 `.test-results/moss-retarget-20261026-baseline-d/` 与 `baseline-e/`；远端目标反例为 `baseline-remote-h/`。后者通过表示观测完成，不是修复前异常用例通过。

## 验证范围

最终冻结生产 DLL：

| 文件 | SHA-256 |
|---|---|
| HKMP.dll | `627ED7346EF231D3BF07B6C6214B568074A06F2F093A2C081D90961AEC58A270` |
| HollowConnect.dll | `63DBE2A8DC1AA6B092B05C7F5535B6D79B7D87E374D016CA01415DCF66F9659D` |
| HollowConnect.Core.dll | `746C12EF77F5097A57B7416CCA5BE728BE33F3163FEAF527A2804AA6852426F6` |

增量生产构建通过，现有 5 项 `BossTargetCommitment` 核心检查通过。最终 9/9 组双端回归通过，两端完成标记、严格日志及生产哈希均通过。完整清单与日志哈希见 [机器可读证据](evidence/moss-retarget-2026-10-26.json)。

| 范围 | 结果 | 验证内容 |
|---|---|---|
| 灵活模式，初始 AI 权威死亡 | 2/2 通过 | 房主、客机分别为初始权威；真实死亡回 Town、队友接管、自然攻击、离开范围隐藏、重新进入后出土 |
| 灵活模式，已选远端目标死亡 | 2/2 通过 | 两种初始权威；原权威留场，目标失效后战斗继续 |
| 原版模式，初始 AI 权威死亡 | 2/2 通过 | 两种初始权威，确认通用引用重绑不依赖灵活模式 |
| 既有 Hornet 灵活索敌 | 2/2 通过 | 自然目标选择、滞回、移动/朝向、无远端持久引用、接管连续性 |
| 既有 Hornet 原版救援索敌 | 1/1 通过 | 真实倒地、存活队友换边、实际网络救活后恢复目标 |

最终目录前缀为 `.test-results/moss-retarget-20261026-`：`fixed-remote-host-final3`，以及 `fixed-host`、`fixed-guest`、`fixed-remote-guest`、`native-host`、`native-guest`、`flex-host`、`flex-guest`、`rescue-host` 各自的 `-final4`。最终 9 案合计约 9 分 51 秒，不含预检和开发失败。四组灵活模式 Moss 用例另核对日志：死亡者确实等于已提交目标，回椅后目标被存活队友替换。

每案由持久 runner 顺序启动两份现有隔离游戏，检查两端完成、严格运行日志及实际生产 SHA-256；每案 8 分钟超时。原生进房流程在 batch mode 仅把渲染帧末等待替换成下一模拟帧；没有强制 Boss 状态、发送出土事件或绕过开战流程。角色位置与致命伤害由探针准备，排除额外接触伤害；这不是手动全流程战斗或两台机器的公网联机测试。

保留的开发失败：`baseline-b` 错误要求副本在接管前已有组件绑定；`baseline-c` 在两个玩家等距时预设了目标；均已修正测试前提。`fixed-remote-host-final2` 已通过战斗继续，但战后范围外停留时角色受原生 Roar 恢复的重力影响落回范围，属于 fixture 失败；战后几何检查现重新固定角色位置。`final1`、`final2` 及更早修复后结果的生产哈希不同，只作开发证据，不用于最终验收。

未覆盖：竞技场特定敌人、Boss 伤害/死亡结算、其他 Boss 家族、重连/晚加入、公网 FRP/UDP。未把静态扫描和单个 Boss 的通过推广为全 Boss 安全性结论。本次不改协议或生命周期调度，因此按明确名单回归，没有启动所有 Unity/Boss 套件。

本地版本仍为 0.2.1，构建目录 `src/HollowConnect/bin/Release/net472/`。本次未打包或安装，保留此前观战显示与传送避障修复。运行清单、哈希与每案状态集中保存在 `.analysis/moss-retarget-20261026/`。
