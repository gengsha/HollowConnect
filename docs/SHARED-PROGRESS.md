# 共享永久进度白名单

0.2.2（2026-10-28）：幼虫按 46 个来源共享；城市纹章、典雅的钥匙、爱之钥和店主的钥匙各按一个来源共享、各自消费；七位梦战士与五位梦境 Boss 的首次精华奖励按 12 个来源共享。接收新增来源时均有左下角提示，重复领取／重连不重复奖励或提示。梦之树精华和精华总余额不进入共享，钥匙使用后的门锁与任务交付各自处理。`openedCityGate`、`openedLoveDoor`、`openedMageDoor` 因此移出永久进度并集；已有旧门状态保留。详见[追加修改与验证](HEALTH-ESSENCE-KEYS-2026-10-28.md)。下文带旧日期、版本号的记录保留各历史构建规则；精华及特殊钥匙的当前规则以本段为准。

0.1.37 当前规则：简单钥匙按四个固定来源共享，每个存档每来源领取一次；个人消费不影响其他人，后加入者按来源补齐，重连不退回自己已花掉的钥匙。吉吉、欢乐之屋、皇家水道入口和寻神者棺材各自解锁、各自扣一把。旧存档同步不能覆盖本机钥匙余额、来源和门锁。已打开的旧门保留，不自动关门或退钥匙。

布尔字段按 OR 合并；`fireballLevel`、`quakeLevel`、`screamLevel` 上限 2，按最大值合并。能力已有时不再次增加等级。字段均已与本机游戏 API 反射校验。

2026-10-23 补齐失落近亲 `infectedKnightDreamDefeated`、灵魂暴君 `mageLordDreamDefeated` 的一次性击败记录，与已有的失败冠军记录一起遵从 `ShareProgress`。队友或房间保存的并集中已有击败记录时，入队／重连可补回漏记；接收方已在对应原生入口房间，也会重新检查入口和未领取的灵魂。精华余额与 `*OrbsCollected` 始终独立，已领奖者不会重复获得。白色防御者、灰色王子的个人挑战次数与重复挑战状态不进入并集。见[梦境结算补齐记录](DREAM-SETTLEMENT-CATCHUP-2026-10-23.md)。

0.1.27 加入入队核对：每次有人加入或重连，服务器要求房间内所有人重新核对当前存档，合并下列白名单进度与 39 个资源来源。谁已经收集过，对应进度就进入房主存档中的并集，再补给其他成员。例如甲只有冲刺、乙只有二段跳，核对后双方都有冲刺和二段跳。`ShareProgress` 默认开启，已有配置显式保存的关闭仍保留；房主开房前可修改，护符遵从 `ShareCharms`。

关闭 `ShareProgress` 时，在开房握手中固定本地世界模式：普通怪、机关、奖励、剧情和存档各自推进，完整存档同步同时关闭；同房且双方具有对应实体的 Boss 战斗与队友位置继续同步。队友刀光和法术不能打击自己的普通怪或触发自己的机关。传送和暗影救援继续遵从各自独立开关。双方 Boss 剧情进度不同、原生加载不同场景时保留个人场景，未匹配的 Boss 在本机运行，不用另一人的通关布局覆盖。重新开房才改变世界模式；关闭共享不撤销已获得进度。当前 0.1.29 网络标识为 `0.1.29-l1`，旧构建拒绝混连。

核对也会检查本地来源回执对应的实际非消耗资源。旧档已经标记领取蘑菇精英战护符槽，却仍少一格的，加入开启共享的房间后会按已知来源补足；面具和容器碎片计入已经合成的整颗数量，只补差额。苍白矿石按来源首次补发，不按库存补齐，已消费的矿石不会返还，骨钉升级仍各自独立。

0.1.28 修复神居/旧满能力档缺少普通领取标记的情况：11 槽、16 个面具碎片、9 个容器碎片各自能证明该类全部来源；矿石以当前库存加骨钉累计耗矿（各级为 0/0/1/3/6）恰好为 6 时确认全部矿石来源。只恢复已经领过的本地回执，再向缺少来源的队友补发；满档自己不获返还，骨钉等级和伤害仍保持个人值。未满的总量不猜测来源，仍依赖原生标记或已有回执。旧 0.1.27 交付包不含此修改，范围与检测见[资源补发修复记录](RESOURCE-CATCHUP-2026-10-20.md)和[0.1.28 更新日志](UPDATE-0.1.28.md)。

0.1.29 补上菜单连接路径：本次连接收到的有效资源来源保留到断线，存档统计或 `PlayerData` 被载入替换后，等待可操作且原生交互结束，先导入所选档案的已有领取历史，再对本连接来源去重结算。这样先 Connect 再新建／选档不会丢掉菜单期间收到的奖励，前一档的回执也不会阻止新档领取。断线清空连接缓存；已消费矿石不返还。原版本地拾取经历和世界资源仍取来源并集，规则未改。复现与检测边界见[菜单选档资源修复](RESOURCE-MENU-JOIN-2026-10-20.md)。

随后按用户要求，将入房核对及远端补发的面具/容器碎片改为直接结算：每 4 个面具碎片增加一格生命上限，每 3 个容器碎片增加 33 额外灵魂容量，余数保留；收到的当前批次在同一轮安全更新内结算，不创建原版碎片收集或集齐动画、不接管控制。世界资源仍按固定来源取并集，同一来源只发一次；本地实际拾取继续原版动画，并拦截已共享来源的重复数值。当前原生交互未结束时仍延后远端结算，避免其缓存覆盖新数值。详见[直接结算记录](DIRECT-RESOURCE-SETTLEMENT-2026-10-20.md)。

远端实际补发及旧档补差成功后，护符槽、面具碎片、灵魂容器碎片、苍白矿石均使用原版左下角物品提示框、资源图标和当前游戏语言的名称。同批同类合并数量，逐类显示；数值结算不等待提示播放。原版提示框只有淡入淡出，不启动碎片收集/集齐动画；重复来源和消费后重连不重复弹出。断线或切换存档会清理本次提示。替换此前人物旁漂浮文字及矿石自绘文字，不再另发顶部资源文字。详见[资源提示记录](RESOURCE-NOTIFICATIONS-2026-10-20.md)。

共享拥有状态与本地拾取经历分别保存。远端获得的能力、护符和资源不会提前清除尚未在本地拾取的对象；本地碰到后仍执行原版动作、过场和恢复控制，只拦截重复数值奖励及重复碎片合成。0.1.28 对已共享碎片的本地重复领取，在原版 UI 初始化后直接进入原版收尾，跳过依赖碎片余数的计数展示，避免余数为零卡住；本地首次领取保持完整原版流程。原版已经在本地领取并隐藏的物品保持原版状态。入队核对与远端奖励等待当前原版交互结束，不强制打断其他原生过场。能力/护符经历在首次迁移旧档时以已有原版数据初始化；旧版曾远端共享但未本地拾取的经历无法追溯区分。

本轮验证范围、原生过场检查和受测二进制见[入队进度核对记录](PROGRESS-JOIN-AUDIT-2026-10-19.md)。这不是整个原版存档的复制；地图探索、吉欧、精华、钥匙余额及个人开锁状态、装备和未列出的任务状态不进入并集。0.1.37 的简单钥匙仅共享固定来源，详见本页开头规则。

2026-10-22 补上地图购买共享：`hasMap`、`hasQuill` 和 13 个区域地图购买标记遵从房主的 `ShareProgress`，不受护符共享开关影响。入房时合并已有地图，联机时同步新购买地图；探索房间和地图绘制记录仍各自独立。原版会隐藏已拥有的商店地图；共享消息到达前已打开的柯尼法／伊塞尔达确认框，再确认时也不会重复扣费。来源消息到达前双方同时购买，仍可能各自支付首次价格。地图购买不使用能力的本地拾取回执，避免把共享地图再次显示为未购买。

0.1.29 收窄本地拾取经历的读取范围：普通能力前置条件必须使用共享后的实际能力，例如无眼出场需要的提灯；不能因为控制器有 `playerData bool` 变量就把它当成拾取前后布景。只有存在实际能力发放动作的拾取对象及已核对的 `Abyss_21` 二段跳前／后布景使用本地经历。梦战士仍由本场原生死亡同步战胜状态，双方各自通过原版对话领取一次精华；不会复制队友精华余额。见[无眼问题审查与复现](NO-EYES-NET-AUDIT-2026-10-20.md)。

0.1.20 将骨钉等级、伤害和升级消费改为个人独立。护符槽、面具碎片、灵魂容器碎片和苍白矿石按固定来源共享去重，消耗后不会因为重连重新发放；不按资源余额取最大值。来源与验证边界见[本轮修复记录](USER-FLOW-REPAIR-2026-10-17.md)。

房主的 `ShareCharms` 开关默认 `true`，开房时固定规则。总开关 `ShareProgress` 开启后，可将 `ShareCharms` 设为 `false`，仅关闭下列 `gotCharm_1` 至 `gotCharm_40` 的共享；其他永久能力及护符槽来源共享不受影响。关闭不撤销已获得护符，装备始终各自独立；迟加入也遵从本房间规则。

护符共享开启时也补齐原生 `hasCharm` 护符页解锁标记，本地首次拾取教程使用独立领取经历。国王之魂两种半片按组合合并，`1 + 2` 得到完整状态 `3`，虚空之心状态 `4` 不回退；同时共享两种半片的领取标记。护符总数遵从原版规则，不把未拼齐的半片当作一个完整护符。关闭 `ShareCharms` 同时关闭这些护符标记与状态的共享。

房主存档的 `DeathStatistics` 保存共享进度值和资源来源回执。服务器启动从该存档装载，收到新来源或能力时先更新房主当前槽位的模组数据再广播，随游戏正常保存写入磁盘；在正常保存后重启服务器或迟加入仍可读取来源记录。远端来源不会让本地场景物品消失，玩家仍可走原生拾取流程，但正向资源写入只成功一次。

Dung Defender 的 `defeatedDungDefender` 与 `dungDefenderEncounterReady` 和 `killedDungDefender` 一起共享。前两项是原生 `Waterways_05` 场景选择活 Boss、已击败场景以及 White Defender 梦境再次进入状态所用的剧情标志；只同步击杀记录会让迟加入或重新进房的一端重新加载活 Boss。

0.1.7 另将 `watcherChandelier`（守望者吊灯永久事件）纳入共享，只有房主开启共享进度时合并。

- `collectorDefeated`
- `defeatedDungDefender`
- `defeatedMantisLords`
- `dreamNailUpgraded`
- `dungDefenderEncounterReady`
- `falseKnightDefeated`
- `falseKnightDreamDefeated`
- `fireballLevel`
- `flukeMotherDefeated`
- `giantBuzzerDefeated`
- `giantFlyDefeated`
- `gotCharm_1`
- `gotCharm_10`
- `gotCharm_11`
- `gotCharm_12`
- `gotCharm_13`
- `gotCharm_14`
- `gotCharm_15`
- `gotCharm_16`
- `gotCharm_17`
- `gotCharm_18`
- `gotCharm_19`
- `gotCharm_2`
- `gotCharm_20`
- `gotCharm_21`
- `gotCharm_22`
- `gotCharm_23`
- `gotCharm_24`
- `gotCharm_25`
- `gotCharm_26`
- `gotCharm_27`
- `gotCharm_28`
- `gotCharm_29`
- `gotCharm_3`
- `gotCharm_30`
- `gotCharm_31`
- `gotCharm_32`
- `gotCharm_33`
- `gotCharm_34`
- `gotCharm_35`
- `gotCharm_36`
- `gotCharm_37`
- `gotCharm_38`
- `gotCharm_39`
- `gotCharm_4`
- `gotCharm_40`
- `gotCharm_5`
- `gotCharm_6`
- `gotCharm_7`
- `gotCharm_8`
- `gotCharm_9`
- `gotKingFragment`
- `gotQueenFragment`
- `hasAcidArmour`
- `hasAllNailArts`
- `hasCharm`
- `hasCyclone`
- `hasDash`
- `hasDashSlash`
- `hasDoubleJump`
- `hasDreamGate`
- `hasDreamNail`
- `hasKingsBrand`
- `hasLantern`
- `hasNailArt`
- `hasShadowDash`
- `hasSpell`
- `hasSuperDash`
- `hasTramPass`
- `hasUpwardSlash`
- `hasWalljump`
- `hegemolDefeated`
- `infectedKnightDreamDefeated`
- `hornet1Defeated`
- `hornetOutskirtsDefeated`
- `killedBigFly`
- `killedBlackKnight`
- `killedDungDefender`
- `killedFalseKnight`
- `killedFlukeMother`
- `killedHiveKnight`
- `killedHornet`
- `killedInfectedKnight`
- `killedMageLord`
- `killedMantisLord`
- `killedMegaMossCharger`
- `killedTraitorLord`
- `lurienDefeated`
- `mageLordDefeated`
- `mageLordDreamDefeated`
- `maskBrokenHegemol`
- `maskBrokenLurien`
- `maskBrokenMonomon`
- `mawlekDefeated`
- `megaMossChargerDefeated`
- `monomonDefeated`
- `openedBlackEggDoor`
- `openedCrossroads`
- `openedDeepnest`
- `openedFungalWastes`
- `openedGreenpath`
- `openedPalaceGrounds`
- `openedRestingGrounds`
- `openedRoyalGardens`
- `openedRuins1`
- `openedRuins2`
- `openedTownBuilding`
- `openedTramLower`
- `openedTramRestingGrounds`
- `royalCharmState`
- `quakeLevel`
- `screamLevel`
