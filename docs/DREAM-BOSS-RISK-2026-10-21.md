# 梦境 Boss 扩展风险审查（2026-10-21）

后续源码修复与复测另见[梦境Boss与螳螂爪报告](DREAM-CLAW-FIX-2026-10-21.md)。下文保留修复前的发现、原始失败和当时二进制范围；后续通过没有追溯改写本报告或现有ZIP。

结论：**不能保证其他梦境 Boss 没有风险。** 当前 0.1.29 的无眼空文本保护适用于七位墓碑梦战士；现在七位都有“未战胜主机＋已领奖客机”的战后交互证据。但扩大检查实际发现了失败冠军本体未同步，以及戈布战后切房失败。这两项尚未修复。失落近亲、灵魂暴君、白色防御者、灰色王子的场景结构观察完成，不等于完整战斗、返回和领奖通过。

本轮是补充审查与隔离运行，修改测试探针及记录，生产源码没有修改，也没有重新打包。现有 Windows ZIP 的 SHA-256 仍为 `B9A43AFCA9C41ADDA8FD1BAB2E73F23C859D198FABA2ED0E2F050CCF70CC16DF`。本报告晚于该包生成；包内原验证记录保留原时点范围。可核对的运行、文件及日志哈希见 [补充证据](evidence/dream-boss-risk-2026-10-21.json)。

## 审查输入与方法

使用 `hollow-knight-boss-net-audit` 技能的风险分类，复用相同游戏程序集哈希的 `.analysis/shop-shard-fix-20261020/static-audit/audit.json`，未重复全量扫描。游戏目录为既有 `.test-results/game-host`、`.test-results/game-guest`；内存角色禁止存盘，没有载入或改写私人存档。双端使用真实 localhost UDP，未注入丢包。

| 输入 | 字节 | SHA-256 |
|---|---:|---|
| `.test-results/game-host/hollow_knight_Data/Managed/Assembly-CSharp.dll` | 4064768 | `5944411BD93830369390A4B51766EE68C4AB26195B299E25A07B5E7D0E00086D` |
| `.test-results/game-host/hollow_knight_Data/Managed/PlayMaker.dll` | 212480 | `0EF0E7829D125E1F632C8A189260EC6C6882630BE6932C8C7AE032EFBC53469A` |
| 固定 payload/HKMP.dll | 1787904 | `9299654248CA2AC35CDD40D2E0A3E8C70F78548C2C2AE5AB6FB2923CABE82D3E` |
| 固定 payload/HollowConnect.dll | 150528 | `C8599F4686B04C84194198E27D3428AAE7C1CBC88B267E3FE1E21E61A85772DB` |
| 固定 payload/HollowConnect.Core.dll | 32768 | `5E1CB8E0FBA13550BADF07FE638D0AD58D8B4524570BD90B96E5C676EB9D2BC8` |

固定 payload 为 `.analysis/noeyes-reward-20261021/tested-payload`。首套观察探针 SHA-256 为 `FD5EAB4BFBB0760051A0B8FAD21951FA9A4F0C989223618CDE41C0F72648B831`；失败冠军有效伤害探针为 `AEBF7D1EF39E078CB80D9456A1F37D9968CB44E0F8D423AF7E185783A5FE95EA`。两者分别保留，不把新探针哈希追记到先前运行。

## 已复现的问题

### P1：剧情失败冠军未注册网络本体

`Dream_01_False_Knight` 的原生本体名称为 `False Knight Dream`，控制 FSM 为 `FalseyControl`。`src/HKMP/Resource/entity-registry.json` 只有 `False Knight New` 的 FalseKnight 条目，`EntityRegistry.TryGetEntry` 按对象名包含关系筛选，FSM 相同无法补上名称不匹配。`EntityProcessor` 没有给这个本体创建网络实体；同房的桶、地板等辅助实体存在，不能代替本体血量和状态同步。

有效复现：`dreamaudit-0129-trigger-damage-20261021-dream-01-false-knight-guest`。双方直入实际剧情场景、提交房间快照后，将角色移动到真实 `Battle Scene` 触发区域；原版从 Dormant 进入 Start Fall 并解除无敌。两端本体 `registry=False`、`registered=0`、原生 FSM 均 enabled。只给主机本体一次原生 17 伤害 `HealthManager.Hit`，3 秒后主机 HP=343，客机 HP=360，且两端原生攻击状态已经不同。断言与严格日志检查均失败，证据保留。这是**确认的运行缺陷**，不是从名称猜测问题。

限制：没有通过原生梦钉进入，也没有测试多次破甲、露头、最终死亡、返回和精华结算；本次证明的是本体注册及伤害同步缺失。不能把它归为旧 `false-knight-repeat-head-unresolved` 的已知根因，那项旧失败的具体触发尚未证明相同。

首个伤害尝试 `dreamaudit-0129-damage-20261021-dream-01-false-knight-guest` 在 Dormant 状态直接 Hit，没有减少主机血量，属于**无效伤害探针**；保留失败，未用来证明血量分叉。有效复现改为先触发真实开战，没有直接修改 HP、无敌标记或 Boss FSM 状态。

### P0：戈布战后切房留下客机附加场景

先前连续五位补测 `dreamwarriors-0129-remaining-claimed-20261021-guest` 在第一位戈布战斗／战后交互通过后，客机去 Town 失败。加入只读加载跟踪后，`dreamaudit-0129-gorb-elderhu-sequential-20261021-guest` 再次失败，停止在第一个缺失条件，没有继续把后续级联等待算作独立缺陷。

两端均从 Cliffs_02 请求 Town；卸载主场景时，仍加载的 `Cliffs_02_boss` 暂时变为 active scene。主机随后正常 active=Town，附加场景卸载。客机原版 `GameManager.LevelActivated → SetupSceneRefs → SetupHeroRefs` 抛出 NullReferenceException；45 秒后 active 与网络场景仍为 `Cliffs_02_boss`，GM sceneName 仍为 Cliffs_02，Town 与附加场景同时加载。主机和客机已进入不同网络房间。

**确认的是当前隔离直切入口下两次相同的切房失败。** 客机附加 Boss 场景经同步补加载，`BossSceneLayout.LoadChoice` 的原生清理衔接是强候选；尚未证明唯一路径，不能仅凭候选修改全局场景生命周期。最小下一验证是通过真实出口／F8 分别离开，记录附加加载来源与卸载调度，并对照同一原生领奖历史的单机退出。未验证用户实际出口也同样触发；与历史 `cliffs-room-overlap-unresolved` 不合并、不关闭。

## 七位墓碑梦战士的限定结论

`DreamWarriorSynchronizer.Keys` 仅覆盖这七个场景。七位共用 `Ghost Warrior NPC/Conversation Control`；0.1.29 的保护保留入房时已经领奖的 NPC 原生终态，而网络实体照常参战。无眼问题与修复过程见 [先前专项报告](NO-EYES-COLLECTED-2026-10-21.md)。

| Boss | 已领奖客机战后交互 | 证据 |
|---|---|---|
| 无眼 | 通过；另测双方未战胜、客机打过未领奖 | `noeyes-0129-claimed-final-20261021-guest` 等，先前最终探针 F38E…，生产 DLL 相同 |
| 泽若 | 通过 | `xero-0129-claimed-final-20261021-guest`，先前最终探针 F38E… |
| 戈布 | 单场通过；连续离房失败另记 | `dreamaudit-0129-gorb-claimed-20261021-guest` |
| 胡长老 | 单场通过 | `dreamaudit-0129-elderhu-claimed-20261021-guest` |
| 马尔穆 | 单场通过 | `dreamaudit-0129-marmu-claimed-20261021-guest` |
| 加利安 | 单场通过 | `dreamaudit-0129-galien-claimed-20261021-guest` |
| 马科斯 | 单场通过 | `dreamaudit-0129-markoth-claimed-20261021-guest` |

新增五组均双方完成、严格日志通过：主机个人完成值0、客机2，主机原生发起挑战和致命 Hit，主机正常领取一次精华；客机等主机领取后才交互，墓碑文本有效、NPC 保持终态、精华不重复增加，控制恢复。还检查非权威不能提前宣告胜利、只有一个原生战斗权威，以及重复完成／过时开战消息不重开或重领奖。

这些是特定领奖历史路径通过，**不是七位所有招式、弹幕、晚加入、断线接管或连续旅行全部通过**。

## 五个梦境变体的独立风险

五个实际场景都未发现该 `Ghost Warrior NPC`，也不在上述同步器白名单。因此无眼的“已领取 NPC 被强推 Ready，缺少对话键”路径不直接作用于它们；这并不能保证其各自死亡和返回流程安全。

五组 `*-schema-20261021-guest` 仅观察已载入的实际场景、原生 FSM 和网络实体。主机未战胜／未领奖，客机已战胜／已领奖、白防与灰王子次数2；这些是禁止存盘的内存夹具。探针直切场景，并非原生梦钉入场；没有用 schema pass 冒充战斗／领奖验收。

| 优先级／Boss | 触发及可能分叉 | 直接证据／置信度 | 最小缺失验证 |
|---|---|---|---|
| P1 失败冠军 | 本体与头部未共享，双方独立扣血／阶段，可能独立收尾 | 上述注册与17伤害分叉已确认 | 注册修复前后对照，再测多轮破甲与头部、原生精华和返回 |
| P1 灵魂暴君 | 一阶段结束不能当作整个战斗结束；二阶段尸体负责精华演出 | 观察到 SoulTyrant、SoulTyrantPhase2；`BossDefeatSignals` 排除一阶段终结；原生二阶段 corpse/Soul Breath 创建 Dream Orb Get。**需运行验证** | 原生梦钉进入，两阶段伤害与终结、已领客机陪打、双方返回及各自奖励 |
| P1 失落近亲 | 客机移除本体独立 EnemyDeathEffects／尸体，收尾依赖注册的 LostKinCorpse；尸体缺失或重复会影响精华演出 | `Entity.HandleEnemyDeathEffects`、注册表与原生 corpse/BG Open 创建 Dream Orb Get。观察到网络 LostKin。**强候选** | 实际击杀至同步尸体、返回；未领与已领个人历史分别检查 |
| P0/P1 白色防御者 | 原生入场控制锁、战后尸体返回、不同挑战次数；不能用神居胜利替代剧情梦境 | 本次双方网络 WhiteDefender=1600；原生尸体写 defeated、递增次数并转 Waterways_15；已有特定入场恢复。**需运行验证** | 从 Waterways_15 真实梦钉入场，完整胜利返回，再挑战一次，对照个人次数及精华 |
| P1 灰色王子 | 不同次数影响等级、称号、伤害；Zoteling 动态对象与 Bretta 演出另行收尾 | 网络 GreyPrinceZote=1200；原生 PlayerData 状态递增 greyPrinceDefeats；Bretta Dream/Dream End 创建 Dream Orb Get Long。**需运行验证** | 次数不同的两端实战、召唤物与最终演出、重复挑战、返回和个人奖励 |

`save-data.json` 虽含 defeated／OrbsCollected 的全档同步配置，当前路径 `ClientManager._fullSynchronisation` 为服务器全档开关且受 LocalWorldOnly 限制；本次测试服务器未启用全档同步，不能因 JSON 的 Sync=true 就声称正在覆盖个人领奖历史。HollowConnect 的 `ProgressCatalog` 另共享 `falseKnightDreamDefeated`，其余四变体 defeated 和五个 OrbsCollected 不在该白名单。观察中主机 falseKnightDreamDefeated 收到 true，其余 defeated 和个人 OrbsCollected 仍 false；客机精华230不变。此处记录真实行为，不将世界胜利旗标取并集直接判为 bug，也不据此证明最终领奖去重安全。

## 覆盖矩阵与后续验收边界

| 审查项 | 七梦战士 | 五梦境变体 |
|---|---|---|
| 血量／死亡 | 致命 Hit 与原生死亡收尾，特定历史路径通过 | 失败冠军本体漏注册／非致命伤害分叉确认；其余仅实体与 FSM 观察 |
| FSM／奖励 | 已领陪打与战后交互七位有证据；未领／初战仅无眼补全 | 原生结构已读，完整演出／领奖未验收 |
| 动态弹幕／子对象 | 未全面验证 | 静态注册与结构候选，未实战全面验证 |
| 场景／返回 | 戈布连续切房两次失败；其余不据单场通过推断离房 | 本次直入场景；原生梦钉进入和胜利返回未完成 |
| 接管／重连／晚加入 | 本轮未测 | 本轮未测 |
| 玩家数据 | 已领不重复精华、未领各领；内存夹具 | 加载期个人旗标／精华观察，未验证最终奖励 |
| 网络拓扑 | 同机双客户端 UDP | 同机双客户端 UDP |

生产行为未变，因此本轮没有运行无关核心套件或全 Unity 矩阵，也没有重新生成交付 ZIP。测试探针构建均0警告／0错误。历史10项未解记录继续保留，本报告新增的两项失败不因单场交互通过而关闭。全部原始失败、探针、日志留在对应隔离结果目录；审查结束后停止隔离进程并停用测试插件。
