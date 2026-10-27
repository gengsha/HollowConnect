# 关闭共享后的本地世界：实施前审计

用户要求：关闭共享永久进度后，普通怪、机关、奖励、剧情和存档各自推进；保留同房 Boss 合作及队友位置。传送、暗影救援按各自独立开关使用。

沿用同一游戏程序集的只读 Cecil 审计；Assembly-CSharp SHA256 `5944411BD93830369390A4B51766EE68C4AB26195B299E25A07B5E7D0E00086D`，PlayMaker SHA256 `0EF0E7829D125E1F632C8A189260EC6C6882630BE6932C8C7AE032EFBC53469A`。本次另只读检查了 DamageEnemies.DoDamage、HealthManager.Hit、Breakable.Hit、BreakablePole.Hit、BreakableObject.OnTriggerEnter2D，以及 PlayMaker Fsm 的二维碰撞/触发入口。

| 风险 | 静态证据 | 实施约束与验证 |
|---|---|---|
| 附加层关闭共享，但完整存档同步仍可修改世界 | ClientManager 在连接时独立按 FullSynchronisation 注册 SaveManager | 连接握手先确定房主模式；本地世界模式强制关闭完整存档发送和接收，检查关闭共享时主动请求完整同步的负对照 |
| 普通实体仍被替换为复制体 | EntityProcessor.ShouldNetwork 只排除战斗挡板 | 在原对象停用/克隆前限制登记；Boss 支持物与可信 Boss 生成物保留，普通怪和环境保持原生运行 |
| 普通怪生成物被错误归给任一 Boss | EntityManager.OnGameObjectSpawned 对未识别来源有全房 HealthManager 回退 | 本地世界模式拒绝这种不明来源回退；单独检查普通怪与 Boss 同房时的生成归属 |
| 远端刀光仍直接命中本地对象 | 原生 DamageEnemies.DoDamage 先发事件；环境还可从 PlayMaker Trigger2D 和 Breakable.Hit 收到攻击 | 按远端视觉对象身份和目标所有权过滤，不修改本地攻击；共同 Boss 和救援另测 |
| 房主已完成剧情，布局覆盖另一人的本地场景 | BossRoomSynchronizer 自动套用 SceneLayout、EnemyRoster、BossSceneContext | 不用房主已完成布局替换个人世界；初始快照不匹配的本地 Boss 恢复原生模拟，防止永久休眠 |
| 本地模式断开、重连或切回共享时遗留标志 | 模式影响实体登记和 SaveManager 钩子 | 握手按房主固定，清理后重置；验证不同本机偏好、重连与切换模式，不改旧回执和个人存档 |
| 新旧端误读握手 | 新模式必须在读存档/注册实体前到达 | 独立网络版本拒绝不匹配附加端；检查新握手与旧拒绝格式及包边界 |

静态路径不能证明所有 Boss 及所有原生过场可用。实现后的双端证据另记；本审计本身不是功能通过，也不沿用旧 DLL 的实机认证。

## 本次实现与验证

房主在启动服务器时固定 `LocalWorldOnly`，握手在客户端注册存档和实体钩子前带下该模式；附加网络标识改为 `0.1.26-l1`，引擎为 `3.0.0-hc12`。关闭共享的连接即使请求完整同步，也不会注册完整存档同步钩子。普通实体在克隆、停用原对象之前过滤，远端攻击按来源视觉对象和目标网络所有权隔离；共同 Boss 的支持物及具有真实来源的生成物继续同步。远端 Boss 回放不能广播或直接写入个人机关。

双方进度不同时保留原生场景；权威没有对应静态 Boss 身份时释放本地原对象。实机曾确认：使用普通权威移交 `MakeHost` 恢复这种无匹配 Boss，会把未启用的暂存副本状态复制到本地原对象，使战斗触发后仍不启动。现在这条路径使用本机保留的原始启用状态恢复，随后销毁网络登记和副本。真正拥有快照的断线接管继续使用最新快照。

此前构建的核心回归为 **220 通过、0 失败**。本地双端专项 `r` 至 `v` 已通过：不同通关进度的四种先入组合，以及普通怪/远端攻击/捷径墙/原生二段跳/面具碎片/重连/切换模式的完整流程。运行目录分别为 `.test-results/local-world-20261020-r-diff-host-first-host`、`s-different-host-guest`、`t-different-guest-host`、`u-different-guest-guest`、`v-world-host`（后四项均使用相同 `local-world-20261020-` 前缀）。每组有双方完成标记、严格日志检查和生产二进制哈希。这些结果对应旧 HKMP SHA256 `E062E4113DD92421548C83344E22ACEB8F6B6D91EB0F3D30D72CB02F54C09E1F`、HollowConnect SHA256 `910B82B1F4A240E48BD7B3E6AA32B0E5E0384E2B0E11DA0F11F282E35F7D340F`，不作为后续简化构建的认证。

中间失败 `.test-results/local-world-20261020-q-diff-host-first-host` 保留，用于证明无匹配 Boss 的恢复缺陷；此前定位失败也保留，不计入通过。综合矩阵第一轮 `local-world-20261020-matrix-final-native-host-013` 的夹具仍要求镜头锁区注册网络实体，已因本地化规则过时而失败；当前测试改为要求原生锁区存在且无网络所有者，同时保留镜头恢复检查，不恢复锁区同步。

后续 `matrix2-final-native-host-013`、`matrix2-final-native-guest-013`（均带 `local-world-20261020-` 前缀）通过了原生一门前五 Boss 至休息房的两轮流程，仍对应上述旧构建。`matrix2-final-lifecycle-host-013` 因 Modding API 同时轮转日志而未加载房主探针；错开启动后，`matrix3-final-lifecycle-host-013` 暴露了真实的大黄蜂回针失败：独立场景物体 `Needle` 被通用危险物过滤，接管后 Boss 停在 `Thrown`。两组失败均保留，不计入通过。

按用户要求收敛实现：删除 C# 中逐类列举 Boss 附属物的分支，将分类移入现有实体登记表的 `boss_support` 字段，包含独立的 `Needle`。所有实体在构造前共用一条准入规则：Boss、登记的战斗支持物、可信 Boss 生成物；本地挡板仍优先排除。共享模式不安装本地攻击隔离钩子，卸载钩子不依赖当前模式值。保留攻击隔离是因为远端刀光/法术本身有原生碰撞，单纯停止登记普通怪仍会伤到私人世界。

简化后构建及 `tests/local-world.selection.txt` 的 **6 项定向核心检查通过**。新版双端本地世界复测 `.test-results/local-world-20261020-w-simplified-world` 通过双方完成标记、严格日志及生产哈希核对，覆盖普通怪零登记、远端刀光/法术隔离、本机攻击和机关有效、个人原生奖励过场、重连和切回共享。受测 HKMP SHA256 `CCDDF40DE5052E83CAFE0EA99C7853CE26BCC62516E920AD818E2D05C5FABB25`、HollowConnect SHA256 `CFD08435544380E99AD7E8534A65BF73F58EDAABE54FA203A88CC2EE8E1EF47C`、Core SHA256 `64661526E34F5EE64BD33C343E10AFE823DCC056671564582585D9E8C0A82787`。

相同生产哈希的 `.test-results/local-world-20261020-x-simplified-needle` 通过：沿用现有生命周期用例，仅开启 `LIFECYCLE_CHECK`、`COLLIDER_RECONNECT_STRESS`、`FSM_HIERARCHY_CHECK`，房主先入；完成原生草团死亡/音频销毁清理、大黄蜂初次重连及后续六轮投针期间断线重连。六轮均观察到原生投针、回到发射位置及 `Throw Recover`，双方 Boss 血量和静态身份一致。双方完成标记、严格日志和生产哈希核对通过。没有重跑全部核心用例、不同剧情的四种组合或完整游戏矩阵；不据此声称全部 Boss 已通过。

同机双进程 UDP 和自动原生流程不等于跨机器 FRP、全部 Boss 或完整人工战斗操作验收。Boss 终结伤害由测试注入；私人 Boss 不会被另一人的剧情布局覆盖，也不表示不匹配场景能强制合作。未打包。

## 0.1.27 交付复测

上述实施记录保留原版本及哈希。0.1.27 升版后的最终 DLL 另行通过了本地世界 `world`、不同进度 `different-host / host-first`、大黄蜂六轮投针重连，以及共享进度完整入队与四组蘑菇精英房检查；相关核心 38 项通过。共 8 组双端，逐组检查完成标记、严格日志和生产哈希。其余三种不同进度先入组合未在本次最终版本重跑。交付范围见 [0.1.27 更新日志](UPDATE-0.1.27.md)，新证据见 [0.1.27 运行记录](evidence/runtime-room-0.1.27.json)。历史失败与旧哈希结果保留，不标记为全 Boss 验收。
