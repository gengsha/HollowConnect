# 苦痛之路末尾封门与 F6 暗影退出

范围：基于本次两张截图、当前源码、White_Palace_20 原生对象/FSM 和同机双端定向复现。无本次用户日志；此前五门诊断不作为本次证据。版本保持 0.1.34，不打包、不安装、不发布。

## 原因与修改

| 问题 | 本次证据 | 修复 |
|---|---|---|
| 客机先触发双守卫、主机传送加入后无法通过右侧封印 | 原生 Battle Control 没有 Battle Enemies 整数；Start 收到 ROYAL GUARD KILLED 后进入 Check Pause / Check，GameObjectHasChildren 检查 Wave 1 子物体。修复前客机进入 Open，主机仍在 Start；主机 Wave 1 的两个 Kingsmould 均 hp=0、isDead=true，却因同步保留的原始对象仍有子物体。 | LocalBattleCompletion 仅对 White_Palace_20 的本地 Battle Control / Check / Wave 1 排除已确认终结死亡的联网 Kingsmould 根对象；死亡快照应用后及本地进入 Start 时，两个守卫均已死亡才投递原生 ROYAL GUARD KILLED。保留原生检查、延迟、音效和 Gate 的 DOWN 流程，不直接置 Open、不关闭碰撞器。 |
| 双守卫已打完才传送加入的补充排列 | 预修复补测显示快照还可能将两个副本重新挂回 Wave 1；其副本 hp 仍为初始值但已 isDead，原始对象已 hp=0、isDead。 | 子物体必须精确属于已登记 Kingsmould 的原始根或副本根，死亡证明统一来自该实体原始 HealthManager 的终结快照；不把其它子物体或单纯未激活的守卫算作死亡。 |
| 队友离开到 White_Palace_03_hub 后 F6 被拒绝 | RequestGiveUp 在开启观战时总发 SpectateRequest；StartSpectating 要求同场存活/加载队友，不满足时只返回 GiveUpDenied。普通 Kingsmould 也不触发 Boss 房间自动失败回程。 | 服务端 GiveUpOrSpectate 在相同死亡编号、轮次和场景内决定：可观战则观战，否则走已有 GiveUp / PersonalDeath 个人原生死亡回程。继续校验复活点与全队失败，保留其它队友状态、死亡编号和救援次数。 |

修改入口：`src/HKMP/Game/Client/Entity/LocalBattleCompletion.cs`、`EntityHooks.cs`、`Component/HealthManagerComponent.cs`；`src/HollowConnect.Core/RescueRules.cs`、`src/HollowConnect/CoopServerAddon.cs`、`RescueOptions.cs`。补充核心检查和 `tests/Smoke/PathOfPainChecks.cs` 定向夹具。

## 验证与证据

本任务目录：`.analysis/path-of-pain-20261023/`。选择、运行器、状态、依赖哈希、编译/核心检查记录均保留；双端只使用既有 `game-host`、`game-guest`，由路径校验后的运行器启动与清理。不触碰个人存档或个人游戏进程。

选择的 46 项核心检查通过，覆盖 F6 无同场队友回程、原有同场观战、过期/错误场景/轮次/死亡编号拒绝、无复活点拒绝、全队失败，以及相关救援锚点、本地世界隔离和个人返回规则。最终日志：`.analysis/path-of-pain-20261023/core-final-02-tests.log`。

最终双端结果见下表（生产 DLL 固定后顺序运行，结果须同时通过两端、严格日志和生产哈希校验）：

| 用例 | 结果目录 | 状态 |
|---|---|---|
| 客机先触发，主机按实际传送键加入；第一只死亡不提前结算；最终两端原生 Open、Gate 碰撞解除并步行穿过；主机暗影、客机离开 hub 后按实际 F6 回程并恢复镜头/物理 | `.test-results/pop-20261023-final-complete` | 通过，约 45 秒 |
| 客机先打完双守卫，主机再按实际传送键加入，验证提前死亡快照与副本重挂；随后同样验证通路和 F6 | `.test-results/pop-20261023-final-late` | 通过，约 48 秒 |

两个最终目录均有双端 done 与 strict-pass，生产哈希一致。最终证据汇总：`.analysis/path-of-pain-20261023/final-evidence.json`。构建目录：`src/HollowConnect/bin/Release/net472/`。

| 最终生产文件 | SHA-256 |
|---|---|
| HKMP.dll | 265A055B912EC80F75E2EC20E48DF8AFACC3CF9FD855BBF840DDE78C59128433 |
| HollowConnect.dll | 393F9CEBFA00ECAAF8B28D811A467B5C16CDB11580E1B65C1E9F22389B78C514 |
| HollowConnect.Core.dll | 5F7058EFF489CFE14A56300DB524622812C7A490302CA0A431B5E7C3257F4520 |

最终编译成功；HKMP 有一条既有 XML 参数注释警告 CS1573，与本次逻辑无关。

此前证据保留：`pop-20261023-preflight` 为启动器 PowerShell 环境失败（无游戏运行）；`preflight-02` 只做原生 schema 预检，其旧 FINAL 文案不能视为通路通过；`repro` 为夹具未开启共享世界导致无联网守卫，修正夹具；`repro-02` 为确认的产品故障；`fixed` 为第一版修复开发证据；`late` 为副本重挂分支产品失败。最终验收只使用上表目录及匹配的最终哈希。

局限：守卫由原生 HealthManager.ApplyExtraDamage 终结；入口位置由夹具放到原生触发器内，主机传送、原生战斗结算、开门、步行穿过与 F6 回程均走实际产品/原生路径。没有跑完整苦痛路线、第三名玩家或异地网络；这些仍需真人三人复现。
