# 链在一起验证

先运行增量构建和核心测试，再构建一次 Smoke。直接用例通过后，按 `DEVELOPMENT.md` 的重大协议／生命周期变更范围执行 12 项房间矩阵。

```powershell
.\scripts\Build.ps1
.\scripts\Build.ps1 -Target Core -RunTests
.\scripts\Build.ps1 -Target Smoke

# 选一个独立的运行名；只使用现有隔离游戏副本。
$env:HOLLOWCONNECT_LINKED_CASE = 'on'
$env:HOLLOWCONNECT_LINKED_BONUS = '2'
.\tests\Smoke\Run-Two.ps1 -RunName 'linked-on-2-唯一运行名' -Mode progress -NoBuild
```

`Run-Two.ps1` 启动后返回；外部运行器仍须等待双端 `.done`，检查 `.failed`、严格日志及实际 DLL 哈希，并负责有界超时和限定路径的进程清理。完成一个用例后才能启动使用相同游戏副本的下一个用例。

| 参数 | 必须覆盖的行为 |
|---|---|
| `on`，额外面具分别为 `0`、`2`、`10` | 晚加入继承当前血量、个人蓝血与穿透伤害、真实深度聚集按键及 33 点灵魂消费、双方同时受伤、原生第四碎片拾取及完整面具动画、来源去重、保存快照隔离、连续全队死亡与复活、断线清理、离线期间受伤后的重连 |
| `on`，额外面具 `2` | 额外覆盖已捕获死亡但尚未开始全队复活时断线，检查个人原版死亡继续完成、剩余成员可完成复活 |
| `off` | 共享永久进度及护符均开启，模式关闭时仍为个人血量，原有救援及观战开关有效 |
| `no-charms` | 模式配置开启、护符共享关闭，模式必须不生效 |
| `no-progress` | 模式配置开启、永久进度共享关闭，模式必须不生效 |
| `on`、额外面具 `2`，另设 `HOLLOWCONNECT_LINKED_REST=on` | 原生双伤和过载四伤时蓝血溢出、五个护符 ID（心脏包含脆弱／坚固版本）的装备拦截及原始字段注入、生命茧蓝血保留、强制原生蜂巢回血动作无效、等待完整蜂巢恢复周期仍不回血；不同场景的椅子／温泉及混合就位、单人等待不回血、起身和出泉取消、未就位名单与加载状态；野兽巢穴陷阱与返回椅子的区别；暂时卸下护符的槽位、过载、保存快照和退房恢复 |
| `on`、额外面具 `2`，另设 `HOLLOWCONNECT_LINKED_SPECIAL=save-off` | 入房前心脏暂存、生命绑定入口阻止；按原生 JSON 设置序列化／反序列化快照，退出并关闭模式后经原生复活载入；个人血量、原搭配、生命血／心脏效果及真实能力进度保留 |
| `HOLLOWCONNECT_LINKED_SPECIAL=host-first` | 客机先连接且主动提交不同初始化，仍按房主旧档的血量／永久上限／零来源和未满碎片基线初始化 |
| `HOLLOWCONNECT_LINKED_SPECIAL=steel` | 同类型钢魂入房，两端均完成原生钢魂死亡、进入 PermaDeath 并离房，不残留复活屏障 |
| `HOLLOWCONNECT_LINKED_SPECIAL=steel-mixed` / `steel-dream` | 现实／梦境混合时各自完成原生死亡或梦境返回并离房；全员梦境时维持钢魂存档类型、完成复活屏障并重试 |
| `HOLLOWCONNECT_LINKED_SPECIAL=mismatch-normal` / `mismatch-steel` | 不同类型存档的两个入房方向都实际关闭传输；不转换类型、不改变个人血量，房主房间继续运行 |
| `HOLLOWCONNECT_LINKED_SPECIAL=zero-drop` | 接收权威零血回调内立即断线，尚未等到下一次运行时 Tick，也必须完成原生死亡，剩余房主可重试 |
| `HOLLOWCONNECT_LINKED_SPECIAL=host-swap` | 服务器存活期间替换房主存档身份，旧房间自动关闭并清除运行时血量限制 |

后三个不生效用例仍设置 `HOLLOWCONNECT_LINKED_BONUS=2`，以验证不生效的模式不会偷偷增加个人生命，并实测生命血之心仍能装备、提供蓝血，普通原生回血仍有效。

`LinkedHealthTests.cs` 还覆盖混合休息、加载成员分母、失联心跳、起身、进出房间、温泉固定频率，以及休息回血不能重置伤害报告或复活零血团队。游戏用例在禁用存档写入的临时内存档上执行；保存检查使用 `SaveGameData` 快照，不读写私人存档。`HOLLOWCONNECT_LINKED_REST=inspect` 只用于读取原生场景／FSM 架构，不作为休息玩法通过证据。

初版证据在 `.analysis/linked-health/`，休息与护符补全的独立选择、最终生产哈希、逐项状态及失败证据在 `.analysis/linked-rest/`。旧哈希结果保留为历史证据。两处目录均采用先专项、后 12 项房间矩阵的顺序。同机双端 UDP 验证与异机／FRP 验证分别记录，不能相互代替。

2026-10-28 补全验收完成：311 项核心检查、7 组直接双端专项及 12 项房间矩阵全部通过。双端均有 FINAL PASS、严格日志通过，实际生产 DLL 与 `.analysis/linked-rest/frozen-hashes.json` 一致；逐项目录见该处 `final-summary.md`。测试进程已退出，未打包或安装。

随后「允许卸下禁用护符」修正单独通过 12 项相关核心与 4 组双端专项，证据在 `.analysis/linked-rule-audit/`。它改变了生产 DLL；此前 19 组仍绑定各自原哈希，不追认为新构建的全矩阵结果。补充审查见 [规则审查](LINKED-HEALTH-REVIEW.md)。

本轮规则补全与存档隔离的选择、开发失败、最终构建哈希及逐项状态记录在 `.analysis/linked-isolation/`。新增真实按键蓄力中队友掉血检查、入房生命茧蓝血保留、运行中注入禁用标志不能扣世界蓝血、心脏禁用、加载名单显示，以及旧休息／重复伤害消息的代次检查。保存专项使用原生 JSON 序列化设置及原生复活载入，仍拦截游戏的全部 SaveGame 文件写入；临时快照只写入本次结果目录，绝不读取私人存档。验收结论以该处最终结果为准。

最终构建验收：316 项核心检查通过，28 组同机双端全部通过（16 项直接专项、12 项房间矩阵）。每组双端 FINAL PASS、严格日志及生产哈希均核验，证据见 `.analysis/linked-isolation/final-summary.md` 与 `accepted-results.json`。完整面具动画还覆盖了保存个人血量精确增长，避免临时 UI 环境漏更新个人基线。全部测试进程退出，未打包或安装。
