# 已领取精华的客机参与梦战士：无眼战后交互修复（2026-10-21）

用户确认：客机此前已经领取无眼 200 精华；本次陪未战胜的主机正常打完，客机按交互之后才出现 `#!##!#` 对话和 `SUPER / MAIN / SUB` 占位标题。上轮共享提灯修复仅覆盖双方未战胜，漏掉个人领奖历史不同的路径。

## 实现前审查与固定基线

实现前报告保存在 `.analysis/noeyes-reward-20261021/prechange-audit.md`。复用相同程序集的 `.analysis/shop-shard-fix-20261020/static-audit/audit.json`，本轮重新核对已有隔离副本：

| 文件 | 字节 | SHA-256 |
|---|---|---|
| `hollow_knight_Data/Managed/Assembly-CSharp.dll` | 4064768 | `5944411BD93830369390A4B51766EE68C4AB26195B299E25A07B5E7D0E00086D` |
| `hollow_knight_Data/Managed/PlayMaker.dll` | 212480 | `0EF0E7829D125E1F632C8A189260EC6C6882630BE6932C8C7AE032EFBC53469A` |

使用上一轮实际交付的合并 0.1.29 固定 payload `.analysis/no-eyes-20261020/tested-payload` 复现。旧 Windows ZIP SHA-256 为 `E05CB69128CAEFA80AE565D2DEC246F51C25B25AB185CF22394755C52A832AE5`，旧包和证据完整保留在 `.analysis/package-0129-pre-claimed-20261021`。旧生产 HKMP SHA-256 为 `9027678C2CEBE532C988D96D117E3E3C0FAC965CFD07A580D68E575467FDED0C`。

| 范围 | 实现前信号 | 置信度及最小探针 |
|---|---|---|
| 初始化与同步阶段 | 原生 Conversation Control/Init 检查个人完成值为 2 就进入 Inert，跳过 Warrior／Inspect Region 引用和 Set Convos 文本初始化；同步 Tick 仍直接推 Ready | 已确认静态风险。用主机0／客机2加载真实 NPC 再开战，检查演出和字符串 |
| 死亡广播与个人奖励 | 全局 GHOST DEFEAT 可将早退的已领 NPC 推回 Idle／Reappear；Tick 的完成演出分支又只处理个人值1 | 强候选。战后先不交互，再执行原生交互并记录实际 UI |
| 实体及权威 | 已领客机仍需看到／参与共享 Boss，不能为此创建第二套原生 AI 或复制领奖记录 | 需运行验证。双方活跃网络实体、原生权威唯一、实际 Hit 和死亡收尾 |
| 其他梦战士 | 七战士共享这个 NPC 模板和同步器，字符串键不同 | 静态适用。选泽若作运行对照，不能由此宣布全部七位通过 |

## 复现与根因

`noeyes-0129-claimed-baseline-20261021-guest`：入房前在受控内存角色准备主机 noEyesDefeated=0、客机=2；客机精华230为固定夹具，不读取或修改私人档。主机发起原生挑战并通过 HealthManager.Hit 致命伤害触发死亡。客机全程不交互直到主机正常领取完毕。

客机原本 Conversation Control=Inert，随后被同步推入 Start Fight；原生死亡广播后变为 Idle、Appear=Reappear。战后实际原生交互产生空文本键，日志记录 `Text active=True renderer=True text=#!##!#`，以及标题 `#!#_SUPER#!#`、`#!#_MAIN#!#`、`#!#_SUB#!#`，与用户截图吻合。个人完成值2、精华230均未变，因此问题是未初始化 NPC 被唤醒，不是重新获得精华。基线失败标记与严格日志失败保留。

首轮修复 `noeyes-0129-claimed-fix-20261021-guest` **仍失败，未交付**：只替换 EnterState 的 state 参数，阻止了部分演出动作，却保留了已赋值的 ActiveState=Idle 和对应交互转换。重新检查 PlayMaker IL，SwitchState 在调用 EnterState 之前已经写入 ActiveState。第二次修复通过原生 SetState 完整切换状态，不再只替换动作参数。

## 最终修改

仅修改生产 `DreamWarriorSynchronizer.cs`：记录 NPC 原生初始化时已经领取的个人终态；收到队友开战状态时保留该 NPC，不强推 Ready；全局死亡广播试图唤醒时，通过原生状态切换保持 Conversation Control/Inert 与 Appear/Fighting。网络 Boss 实体照常参与战斗。

标记跟随当前 NPC／房间生命周期，Clear 清除；重新发现已为 Inert 的已领 NPC 可恢复保护。不会仅凭正在领奖过程中的完成值2就中断新一轮原生消失／领奖动画。离线、独立本地 Boss 场景沿用原行为；没有变更网络包、存档格式、资源并集、精华余额或个人骨钉。上轮 `ProgressPickupService.cs` 与 `CoopRuntime.cs` 行为源码未改。

## 最终验证与限制

最终探针 `F38E95987106DF9E86A90825C2A5FF4BA279F2E96F333693B067871D6C98D7A5` 在固定最终生产 payload 上执行4组双端检查，均双方完成且严格日志通过。完整记录见 [运行证据](evidence/noeyes-collected-2026-10-21.json)。

| 用例 | 结论 |
|---|---|
| 无眼：主机0、客机2 | 主机正常获得200精华；客机战后 NPC 保持 Inert，原生墓碑显示正常本地化文本，关框恢复控制；客机精华保持230，重复消息不重开或重领奖 |
| 无眼：主机0、客机1 | 客机保留未领奖状态，战后双方各领一次200精华 |
| 无眼：双方0、客机共享提灯给主机并发起挑战 | 正常召唤、原生死亡和双方各领一次200精华，提灯修复未回退 |
| 泽若：主机0、客机2 | 同模板已领参与者墓碑交互和去重通过，主机正常领取个人奖励 |

另有第二次修复的首个探针运行通过，之后仅加强 UI 文本断言／整理测试日志，不改变生产源码。该运行不计入上述最终探针4组。27项既有进度／资源核心回归通过；生产重编译有1项既有 CS1573 XML注释警告，最后增量构建为0警告／0错误，详见更新日志。

| 审查区域 | 本轮覆盖 |
|---|---|
| 血量／死亡 | 原生致命 Hit、单一原生权威、死亡收尾；非全攻击／分阶段实战 |
| FSM／演出／奖励 | 三种个人领奖历史、战后交互、实际空文本检查、控制恢复、重复／过时消息 |
| 动态对象／弹幕 | 网络 Boss 激活检查；未完整覆盖弹幕与召唤池 |
| 场景／序列 | 正常双方同房进入；未执行 F8 完整绕法或五门序列 |
| 权威／生命周期 | 正常房间清理与禁止重复权威；未测战斗接管／断线、晚入场或同房重连 |
| 玩家状态 | 提灯真实 UDP 共享、个人精华不复制；无私人存盘，未验证磁盘旧档 LoadGame |
| 拓扑 | 同机双客户端真实 localhost UDP；未测双机 FRP／三人 |

历史10项未解记录全部保留；本次梦战士通过不关闭其他 Boss、五门或挡板问题。版本按用户要求仍为0.1.29，旧哈希的通过不冒充本次新 DLL 通过。
