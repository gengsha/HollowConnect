# 无上辐光平台与爬升补充审计（2026-10-18）

用户反馈：后进入者二阶段只见两三个平台，之后爬升台阶不生成。本报告为继续实施前的只读检查；此前请求已授权修复辐光及同类问题。

沿用同日 [Boss 静态审计](RADIANCE-BOSS-NET-AUDIT-2026-10-18.md) 的 Cecil 扫描，不重复扫描相同程序集。隔离副本 `.test-results/game-host/hollow_knight_Data/Managed/` 已重新核对：`Assembly-CSharp.dll` SHA256 为 `5944411BD93830369390A4B51766EE68C4AB26195B299E25A07B5E7D0E00086D`，`PlayMaker.dll` 为 `0EF0E7829D125E1F632C8A189260EC6C6882630BE6932C8C7AE032EFBC53469A`，与原扫描完全相同。未修改个人游戏或存档。

| 优先级 | 触发与风险 | 现有证据 | 判断 | 最小补测 |
|---|---|---|---|---|
| P1 | 错过平台组激活或事件，后入平台实体存在但不可见或无碰撞 | 注册表只列 `Radiant Plat/radiant_plat`、`Abyss Pit/Ascend`；Boss 的 `Control` 用 `Plat Sets`、`Plats P2A`、`Plats Ascend` 等外部引用；CombatState 仅记录当前实体拥有的子树，不包含这些外部父组 | 确认的静态快照缺口；与本次症状关系待实机定位 | 原生推进 Idle 4 后，双方对比每个平台 ID、父组、可见性与有效碰撞；离房再迟入后重测 |
| P1 | 原生 `Plat Setup` 后缺少上升台阶 | 原运行记录显示 `Ascend Tele → Plat Setup → Ascend Cast`，`Plat Setup` 包含组激活及事件；旧专项结束于 Idle 4 和迟入观察，没有进入此段 | 强候选 | 用正常 Hit 推进原生 Set Ascend，记录外部组控制器、每级平台出现及两端有效承重 |
| P1 | 只补显示而漏掉 collider、镜头或复位 | 平台 native/replica 分离，HazardRespawn 另有触发进度，镜头现为本地 | 需要运行时验证 | 既有落点/深渊专项继续执行，新增完整平台与爬升检查；不执行远端 Boss AI 状态来掩盖问题 |

覆盖边界：本报告不将上次三组通过视为完整平台或爬升验证；本次应保留旧构建失败对照，再按新 DLL 哈希记录结果。未覆盖最终击杀、全部 Boss、双机 FRP。

后续运行确认：旧构建的后入者残留 `CamLock Main`，而先入者已切换到 `CamLock A2`；普通辐光也存在迟入后 `CamLock Challenge` 未退出。暂未复现真实平台实体缺失。普通辐光活动场景／平台归属为 `Dream_Final_Boss`，GM 字段为 `Dream_Final`，原 F8 到达判断确实会遗漏其收尾。修复、失败归因及最终矩阵见 [平台修复记录](RADIANCE-PLATFORM-FIX-2026-10-18.md)，不以本段追加替换原始只读审计。
