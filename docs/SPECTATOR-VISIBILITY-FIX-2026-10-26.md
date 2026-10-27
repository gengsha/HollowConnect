# 0.2.1 观战名字残留与返回后角色隐身

截图反馈：队友死亡后没有可救援暗影，只剩名字跟随存活玩家；本场失败退出后角色仍无模型。用户确认版本为 0.2.1。

## 已确认原因

1. HKMP 的远端 `Username` 与角色身体 `PlayerObject` 同属 `PlayerContainer` 的直接子对象。旧观战逻辑只隐藏身体及其后代，名字仍会跟随观战者的同步位置。观战者为跟随镜头移动到存活队友位置，因此会出现名字跟随活人的画面。进入观战后没有可救援暗影属于既有规则；显示名字是缺陷。
2. 暗影 `ShadeView` 与观战分别记录 `Renderer.forceRenderingOff`。不可靠 Roster 先报告观战、可靠事件尚未移除旧暗影时，观战记录到的“原值”已经是暗影设置的 `true`。暗影退出又可能短暂解除仍在观战的隐藏；观战结束后恢复先前记录的 `true`，远端对象池中的身体便继续隐藏。

原版证据使用交付 0.2.1 的生产 DLL，四个依赖哈希与 `.analysis/spotcheck-0.2.1-20261026/frozen.json` 完全一致。其中原版 `HollowConnect.dll` SHA-256 为 `DDEFBF501E177C2D36450ECF711637CBBF687004E342A78C8A77CE97A223268B`。

在实际双端游戏的远端角色上，用同步夹具控制“旧暗影尚未移除时，本地先收到观战生命状态”的显示顺序。原版得到以下预期失败：

```text
spectator name remains visible
retiring shade reveals active spectator
body or name remains hidden after both owners release
```

这是显示状态交接的确定性复现，没有声称复现截图中完整的传送和受伤时间线。截图不足以确认“传送时秒死”的具体伤害来源。

## 修改

- `PlayerVisualHide.cs` 为暗影与观战共同保存渲染器最初的隐藏值；全部隐藏持有者释放后才恢复。保留原本就被其它逻辑隐藏的值。
- `ShadeService.cs` 使用共同的隐藏持有逻辑；延迟销毁、重复清理均可安全释放。
- `RescueOptions.cs` 对远端完整 `PlayerContainer` 隐藏，覆盖名字和身体；对象更换、离场或回收时释放对应持有者。
- `CoopRuntime.cs` 在整队重试与销毁时显式释放远端观战显示状态。

未改变网络协议、死亡/救援次数规则、原生死亡返回路径、Boss 生命或阶段逻辑。源码仍标识 0.2.1；本次只生成本地修复构建，没有覆盖历史 ZIP 或安装到个人游戏。

## 验证记录

增量生产构建及 Smoke 构建成功，0 警告、0 错误；相关既有核心用例 29/29 通过。

双端专项由 `.analysis/spectator-visibility-20261026/Run.ps1` 串行管理。完整选择、每项状态、PID、实际生产哈希、测试源码和游戏依赖哈希分别在同目录 `SELECTION.md`、各用例 JSON、`frozen-a.json`、`inputs-and-environment.json`。基线预期失败证据保留在 `.test-results/spectator-vis-20261026-baseline-a/`。

首轮团灭检查中，两端原生回椅及本机模型可见断言已通过，但新增的远端断言错误要求 `Life.Alive`，坐椅期间实际应为 `Loading`。保留 `wipe-a` 的失败证据，仅修正夹具允许两种存活显示状态，继续要求角色处于活动状态且身体、名字渲染已恢复。生产 DLL 未改变；`wipe-b` 补跑通过。直接复现不使用该辅助方法；主动观战胜利在之前更严格的断言下已通过，因此保留其结果。

最终固定生产哈希上的双端专项 **6/6 通过**，两端完成标记、最终 PASS、严格运行日志及生产 DLL 哈希均核对通过：

| 用例 | 覆盖 | 结果 |
|---|---|---|
| direct-a | 暗影/观战重叠、两种释放顺序、名字及身体恢复 | 通过 |
| voluntary-a | 原生暗影死亡、主动观战、胜利复活及双端可见状态 | 通过 |
| wipe-b | 救援次数耗尽、全员失败、原生回椅及远端对象池复用 | 通过 |
| off-b | 关闭救援时自动观战、名字隐藏、胜利免费复活 | 通过 |
| pure-host-b | P5 纯粹容器，主机房间权威，观战名字、胜利恢复和下一房间可见状态 | 通过 |
| pure-guest-b | 相同纯粹容器流程，客机房间权威 | 通过 |

纯粹容器沿用既有 P5 选定原生房间/序列上下文夹具，验证结算回调和进入下一房间，不代表完整 P5 通关或所有攻击机制验收。以上均为本机双进程 UDP 检查，不代表异地 FRP/网络测试。

最终 `HollowConnect.dll` SHA-256：`E2BD3B90D938AF026C3D8D18B30DA16378BF6EEA1B6F22F3E4C4B7B15FFC3FED`。汇总证据为 `.analysis/spectator-visibility-20261026/evidence.json`。实际游戏用例约 30–49 秒/组；原版基线与首轮失败证据另行保留。两份隔离游戏的 Smoke 测试插件已停用，未留下游戏进程。
