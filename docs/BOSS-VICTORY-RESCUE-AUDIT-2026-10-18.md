# Boss 击败时解除暗影：实施前审计

本次为用户明确要求的新规则：所有 Boss 真正死亡／战斗结束时，同房间暗影恢复为一血。沿用同日 Cecil 扫描；重新核对隔离副本 Assembly-CSharp SHA256 为 `5944411BD93830369390A4B51766EE68C4AB26195B299E25A07B5E7D0E00086D`，PlayMaker 为 `0EF0E7829D125E1F632C8A189260EC6C6882630BE6932C8C7AE032EFBC53469A`，对应原始结果 `.test-results/boss-audit-radiance-2026-10-18/audit.json`。只读补查原版 `BossSceneController.CheckBossesDead` 在 bossesLeft<=0 时调用 EndBossScene，后者先触发 OnBossesDead，再执行延迟退场。

| 风险 | 证据与等级 | 实施约束／最小验证 |
|---|---|---|
| 本机解除 Down 后被服务器重新盖回 | Party.Presence 明确禁止 Alive 心跳复活 Down，确认的静态风险 | 服务器接受当前死亡序号并增加 LifeVersion；双端核对人物、暗影和生命状态 |
| 将阶段结束当 Boss 击杀 | HealthManagerComponent 区分 SpecialDeath 与 Death；SoulMaster 有独立第二阶段，确认的静态风险 | 使用原生战斗完成、真实终结死亡、原生击败记录动作；排除破甲、第一阶段和普通敌人；验证分阶段对照 |
| 旧消息或重连再次复活 | 会话、Round、LocalVisit、DownSerial 各有独立生命周期，确认的静态风险 | 每次胜利只允许当前房间访问兑换一次，复活必须对应当前 DownSerial；测试重复、旧轮次、换房、新死亡及放弃竞态 |
| Boss 死亡与 Down 消息交错 | 不同客户端的可靠消息没有共同全序，强候选 | 胜利通知到本机时捕获当前 DownSerial，再可靠请求服务器解除；不能只查询服务器收到击杀时的 Down 列表 |
| 平台消失导致复活落空 | 既有普通救援会重新验证 DeathPosition 并寻找原生支撑点，确认的静态风险 | 共用已验证的安全落点／控制解除；无安全点保留个人原生返回保障 |

覆盖计划：纯核心验证票据、顺序、同房范围和生命版本；隔离双 Unity 交换房主／客机，覆盖普通 Boss、分阶段 Boss、原生完成及暗影恢复后的操作和镜头。全 Boss 使用公共入口不等于已逐个通关；未实际运行的 Boss、全部重连与跨机器 FRP 组合不作通过声明。个人安装与存档不改动。
