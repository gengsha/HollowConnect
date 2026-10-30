# 共享永久进度

房主开房前决定是否共享，默认开启。共享会写入所选存档；关闭后已经获得的进度保留。护符拥有状态可由房主单独关闭共享，装备始终各自决定。

| 项目 | 规则 |
|---|---|
| 移动能力、法术、骨钉技、梦之钉等能力 | 合并已获得状态；法术等级按有效上限取较高值 |
| 护符、国王之魂半片、虚空之心 | 遵从护符共享开关；半片可拼合，完整状态不回退 |
| 地图购买、羽毛笔 | 共享已购买状态；地图探索、房间绘制和个人位置不共享为永久进度 |
| 护符槽、面具／容器碎片、苍白矿石 | 按固定来源去重，每个存档每个来源结算一次；消费后重连不返还 |
| 幼虫 | 按 46 个独立来源共享，收藏家三只分别记录 |
| 简单钥匙、城市纹章、典雅／爱之／店主钥匙 | 共享获得来源，各自消费；门锁和交付状态各自处理，旧已开门保留 |
| 梦战士与梦境 Boss 首次精华 | 按 12 个来源共享首次奖励，不复制精华余额；梦之树及零散精华各自获得 |
| 指定 Boss 击败、区域通路与世界标记 | 只合并明确列出的状态，不复制整个存档 |
| 吉欧、骨钉升级与强化扣费、装备、个人复活点 | 各自独立；队友升级不会代替自己升级或扣费 |

加入或重连时核对各自进度和领取来源，合并后给缺少者补发。面具每 4 个碎片增加一格上限，容器每 3 个增加 33 备用灵魂；远端补发直接结算并使用左下角原生提示，不启动集齐动画或抢占控制。

共享拥有状态与本地实际拾取经历分开。本地仍可触发原版拾取和剧情，但已共享的同来源不会重复给数值奖励；重复碎片领取会正常收尾。本地已有交互时等待它结束后再处理远端补发。共享消息到达前双方同时购买，仍可能各自支付首次价格。

旧档优先用确定的原生标记和已保存回执恢复来源；只有完整上限或精确总量能证明全部来源时才补齐，不根据剩余库存猜测。苍白矿石消费、钥匙消费不会因重连退回。梦境 Boss 的一次性击败与个人挑战次数、实际领奖分别记录，不用队友已领奖状态跳过自己的原生收尾。

关闭「共享永久进度」后，普通怪、机关、奖励、剧情与存档各自推进，只保留匹配的同房 Boss 合作和队友位置。传送、救援仍由各自开关控制；不同剧情布局不强行套用队友场景。修改规则后重新开房生效。

## 开发参考

永久布尔值按 OR、有效法术等级按最大值合并；资源使用独立来源回执，不按余额取最大值。地图购买、鹿角虫、钥匙、竞技场与其它专用来源规则另由对应服务处理。下列基础字段清单保留供核对，当前实现以 `src/HollowConnect.Core/ProgressLedger.cs` 和各来源服务为准。

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
