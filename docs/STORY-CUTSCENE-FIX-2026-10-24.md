# 个人剧情触发隔离与守梦人计数

用户要求先复现、再修复，保留永久进度共享，限定验证范围。首次守梦人平台本地化见 `DREAM-STORY-LOCAL-2026-10-24.md`。本次调查发现另一条实际路径：原生剧情物件即使没有网络实体，也可能被真实复制出来的队友刀光碰撞触发。

## 实际复现

| 问题 | 证据目录 | 观察 |
|---|---|---|
| 队友刀光触发本地守梦人 | `.test-results/story-attack-baseline-20261024-a1-dream-guardian-lurien` | 本地角色离 NPC 约 38 单位；另一端实际调用原生攻击，本端 NPC 进入 `Get Damager Parameters`。观察状态进入，未以最终回到 Detecting 误判通过。 |
| 共享击败标记后原生计数遗漏 | `.test-results/story-progress-fixed-20261024-a3-dream-guardian-hegemol`、`story-progress-fixed-20261024-a3-dream-guardian-monomon` | 原生聚集完成后，来源端计数 1；接收端标记 true，但 `guardiansDefeated=0`。 |
| 队友刀光触发本地白王遗体 | `.test-results/story-palace-attack-20261024-a1-white-palace-09` | 本地角色仍在入口 `(147.8,9.4)`，白王在 `(60.3,99.7)`；队友原生攻击令本地遗体进入 Hits。 |

三个守梦人吸收场景原本就没有网络实体，本次没有把这些场景或整个白宫加入实体排除。首次 Fsm.OnTrigger 拦截仍复现失败，证据保留于 `story-attack-fixed-20261024-a1-dream-guardian-lurien`。反编译确认原生自定义 `Trigger2dEvent` 通过 `PlayMakerUnity2DProxy` 的委托调用 `DoTriggerEnter2D/Stay/Exit`，因此应在该实际回调过滤队友来源。

另两次夹具失败也保留：`story-attack-fixed-20261024-a2-*` 是空入口参数；`story-progress-fixed-20261024-a1-*` 是未等待原生重新接受攻击，实际有效命中不足。修夹具未设置剧情状态或直接写入完成标记。白宫最初 `left1` 入口不存在，通过单独 gates 诊断得到原生 `right1`；gates 仅算入口诊断。

## 修改边界

`LocalStoryScene` 在三个 `Dream_Guardian_*` 场景，以及 `White_Palace_09/White King Corpse` 的原生动作触发回调中拒绝带远端影像标记的碰撞源。自己的骨钉、原生 FSM 和奖励流程照常运行。白宫其他物件不受这个新增条件影响。

`CoopRuntime` 按三个永久击败标记补齐 `guardiansDefeated`，不向协议增加可重复相加的计数消息。原生个人吸收在写击败标记后会执行 IncrementInt；共享开启且在线可玩时，该写入取已有计数与唯一击败标记数的最大值，防止较慢玩家再次吸收同一守梦人重复累计。收到标记时立即补齐，周期检查也修复已有标记但缺少计数的情况。已有较高计数不下降；关闭共享或离线时保留原生写入。

## 冻结构建与限定验收

生产构建位于 `src/HollowConnect/bin/Release/net472`。最终哈希：

| DLL | SHA-256 |
|---|---|
| HKMP.dll | E8A8D8DB1C2D24404554F02D780A6DB50A42C78CEE1D54F173946CEEB06467A9 |
| HollowConnect.dll | 4D0B70CE849291D5921BDB576A1B8E72B1602539A45961B622D3598C3345E079 |
| HollowConnect.Core.dll | 20D8D11C22ADE2F4661A1FCE915543F77905ABE4CD0EC678E0496D1D0627CD69 |

最终 6 组双端检查全部通过，实际生产哈希一致，双端完成标记、严格日志与原生剧情异常检查均通过；汇总在 `.analysis/story-cutscenes-20261024/final-results.json`。28 项现有进度核心检查通过，输出在 `.analysis/story-cutscenes-20261024/core-final.log`。精确用例、脚本状态和游戏引用哈希在同目录的 `selection.md`、`current.json`、`game-references.json`。每组双端结果记录受测生产 DLL 和 Smoke 哈希。

| 最终检查 | 结果目录 | 状态 |
|---|---|---|
| 卢瑞恩原生攻击、聚集、共享标记及计数；较慢玩家再次亲自吸收仍计数 1 | `story-final-20261024-a1-dream-guardian-lurien` | 通过 |
| 赫拉、莫诺蒙相同原生流程及共享计数 | `story-final-20261024-a2-dream-guardian-*` | 通过 |
| 白王遗体远端攻击隔离；自己的原生击打破坏并生成王魂 | `story-final-palace-20261024-a1-white-palace-09` | 通过 |
| 出生地原生结束触发区与真实远端身体重叠 | `story-final-abyss-20261024-a1-dream-abyss` | 通过，未复现误触 |
| 首次守梦人本地平台显隐、真实落地、无克隆与迟到接管 | `story-final-platform-20261024-a1` | 通过 |

所有游戏检查使用现有两份隔离游戏、禁止保存的内存角色与 localhost UDP，串行执行。Lurien 之后只新增白宫掉落和出生地重叠断言，原有 Lurien 测试逻辑及生产哈希未改，可复用通过记录。未打包、安装、改版本或写个人存档。

严格日志之外另检查原生异常：仅保留隔离启动器在进入 Town 前既有的一次 `GameManager.SetupHeroRefs` 空引用；其后剧情流程不接受任何原生异常。启动问题在修复前已存在，不能宣称整个启动日志零异常。

出生地检查将角色定位至原生结束触发区，只覆盖实际碰撞接触；未完成开场输入及回忆攀爬，不等于完整文本、虚空之心领取和返回。白宫检查不等于完整王魂领取、文本和坐王座。首次平台检查不等于完整梦钉奖励流程。没有把这些有限检查推广为所有过场、双机 FRP 或全部 Boss 已验证。


