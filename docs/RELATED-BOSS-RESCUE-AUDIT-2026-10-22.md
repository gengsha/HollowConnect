# Boss 附加场景同类问题审查

后续已按用户要求完成针对性修复与最小验证，见[修复记录](RELATED-BOSS-SCENE-FIX-2026-10-22.md)。以下保留修复前的审查结论与证据。

本轮对叛徒领主修复后的源码做只读审查，找到两处尚存的场景判断遗漏。它们是代码与原生资源对照确认的静态风险，尚未通过双端运行复现玩家可见故障。本轮未改生产源码、测试源码、游戏或存档，未构建或启动游戏。

使用 `hollow-knight-boss-net-audit` 的只读流程，原生程序集审计保存在 `.analysis/related-boss-rescue-20261022/native/`。另用 UnityPy 和现有 TypeTreeGeneratorAPI 只读解析明确选定的七个场景文件，没有把资源名称或静态审查当作实机验收。字段读取阶段七个场景均无解析错误。

## 发现

| 优先级 | 对象与触发 | 可能后果 | 精确证据与置信度 | 最小运行验证 |
|---|---|---|---|---|
| P1 | 剧情守望者骑士，任意一只真正死亡而其他骑士仍活着 | 过早发送 Boss 胜利，从而在整场未结束时免费复活暗影／观战者 | `BossDefeatSignals.cs:68` 的存活同伴检查仅接受 `SourceScene == CoopContext.LocalScene`。原生 `level115` 的六只 `Black Knight 1…6` 全在 `Ruins2_03_boss`，各220 HP，`hasSpecialDeath=0`、`deathReset=0`；主场景 `level113` 的 BossLoader 原生加载该附加场景。注册表将 `Black Knight` 映射为 WatcherKnight。**确认静态风险**，未实机复现 |
| P1 | F8 跟随落点靠近附加场景的战斗门／平台 | 落点可能被误判安全，或遗漏有效支撑；实际是否卡位取决于坐标和原生门状态 | `CoopRuntime.cs:1100` 仅用主场景地面，`:1163` 的 `IsWarpBlocked` 直接忽略不同 scene 的碰撞体。资源确认 `Ruins2_03_boss` 有两个 layer8 非触发 Battle Gate；`Fungus3_23_boss` 有 Battle Gate (2)，均启用。**确认静态检查缺口**；“落进门里”等表现仍需运行验证 |

第一项调用链为：`HealthManagerComponent.OnDie/OnUpdate/OnExtraDamage` → `BossDefeatSignals.HealthDeath` → `CoopRuntime.ReportBossDefeated` → 服务端验证权威和房间访问 → 建立免费复活票据。服务端不再次统计原生骑士数量，因此没有发现能补回这个存活同伴判断的下游检查。该结论针对剧情 `Ruins2_03`；神居 `GG_Watcher_Knights` 不能代替此验证，也不能据此断言原生奖励或下一房会提前推进。

第二项是同一种“只看主场景”的检查缺口。碰撞体在资源中启用不等于测试时门已关闭；必须用原生开战后的实际门状态、身体重叠和落点计算验证。普通流程暗影落位走 `StoryRescuePlacement.StoryScene`，已经接受主场景与附加场景，不能把 F8 的风险扩写成所有骨钉复活都有缺陷。

## 已处理或未发现相同遗漏的路径

- 叛徒领主刚修复的 `InBossEncounter()` 是通用 `_boss` 场景识别，不是写死叛徒领主；同约定的已登记、存活 Boss 都会受益。本轮资源还确认戈布原生 `Cliffs_02_boss` 使用该约定，但没有新增戈布救援实测。
- Boss 房同步器的归属判断本来包含主场景、`_boss`、`_boss_defeated`。原生补加载场景已经绑定到加载器的卸载生命周期，未发现与旧戈布残留场景相同的遗漏仍存在。
- `EntityManager.OnSceneUnloaded` 精确匹配被卸载 scene 的 `SourceScene` 是正确的实体清理粒度，不能把所有场景等号统一改成前缀。
- 观战每帧筛选同房、近期有状态且对象有效的 Alive/Loading 玩家；选中者倒地后会继续选其他合格存活者。代码没有固定锁死原选中者，但三人实机接续尚未覆盖。

## 覆盖矩阵

| 范围 | 本轮证据 | 运行覆盖边界 |
|---|---|---|
| 多 Boss 健康／死亡／免费结算 | 当前源码调用链与六骑士原生资源 | 未运行单骑士死亡与最后骑士死亡对照 |
| FSM 与波次 | 读取注册表、原生健康标记和结算路径 | 未覆盖所有阶段或原生波次 |
| 动态子体／弹幕 | 对源实体归属兜底做源码检查；未列为确认故障 | 无新增实战验证 |
| 附加场景／传送碰撞 | 原生 BuildSettings、加载器、门碰撞体与落点代码 | 未运行门边 F8 对照 |
| 离房／清理／权威 | 检查 native owner 绑定、sceneUnloaded 清理及结算权威验证 | 无新增断线、晚加入或接管测试 |
| 救援额度／观战 | 复用上一轮报告描述，未冒充本轮测试 | 前一轮4组最终哈希双端通过；三人及真实双机未测 |

上一轮叛徒领主修复与运行证据见 [EXHAUSTED-BOSS-RESCUE-2026-10-22.md](EXHAUSTED-BOSS-RESCUE-2026-10-22.md)。本轮精确输入路径、大小、SHA-256、提取结果及代码哈希见 [evidence/related-boss-rescue-audit-2026-10-22.json](evidence/related-boss-rescue-audit-2026-10-22.json)。后续优先验证守望者骑士，再根据运行结果修正；不将这次静态审查记为游戏通过。
