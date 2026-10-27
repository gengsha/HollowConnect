# Boss 附加场景结算与传送修复

修复[同类审查](RELATED-BOSS-RESCUE-AUDIT-2026-10-22.md)指出的两处遗漏。用户要求时间优先、避免过度验证，本轮仅增量编译、26 项已有相关核心检查和 1 组双端最小专项，未扩展完整房间矩阵、打包或安装。

- `BossDefeatSignals.HealthDeath` 检查守望者骑士存活同伴时，纳入当前房间原生 `_boss` 附加场景，包括休眠骑士。整组结束前不发送免费结算通知。
- `CoopRuntime` 的传送地面、辐光平台和身体障碍检查使用同一场景归属条件，纳入当前房间的 `_boss` 附加场景，保留无关房间排除。

26 项核心检查通过；Smoke 和生产构建 0 警告、0 错误。双端 `related-boss-fix-20261022-direct-a` 约54秒，双方完成、严格日志和最终生产 DLL 哈希均通过。

双端实际进入剧情 `Ruins2_03/top1`，由原生条件加载 `Ruins2_03_boss`，登记6只守望者骑士；用原生触发区启动波次，只对原生已激活、非无敌的骑士调用正常额外伤害。前5只死亡时胜利通知始终为0，最后一只死亡时为1。未强制波次、HP死亡标记或战斗状态。实际收到的通知经原生产回调继续处理，但本组双方保持存活，不冒充倒地者完整免费复活测试。

传送检查在两端调用实际生产判定：原生附加场景战斗门属于障碍，同一门对无关 Town 不生效；在当前附加场景临时建立远离原生地图的单块支撑，实际 `TryWarpLanding` 能给出其上方落点，检查后销毁。这是落点算法与碰撞归属检查，未模拟用户按 F8、完整跨房加载或门边实际传送。

测试源码 `tests/Smoke/RelatedBossSceneChecks.cs`，复现入口为 `HOLLOWCONNECT_RELATED_BOSS_SCENE_CHECK=1`、`Run-Two.ps1 -Mode rescue -NoBuild`；先构建 Smoke，使用新 RunName，结束后停止隔离游戏、禁用探针并执行严格日志检查。初次 Smoke 构建遇到原生与 Unity 的 SceneManager 同名，已在测试中使用明确别名修正；该编译失败保留，未启动失败构建。生产改动无需因此调整。

显示版本仍0.1.31，构建目录 `E:\projects\hollowConnect\src\HollowConnect\bin\Release\net472`。最终哈希、日志路径和专项范围见 [evidence/related-boss-scene-fix-2026-10-22.json](evidence/related-boss-scene-fix-2026-10-22.json)。原审查与旧构建证据保留，本轮没有重跑三人、断线接管、FRP或完整F8旅行。
