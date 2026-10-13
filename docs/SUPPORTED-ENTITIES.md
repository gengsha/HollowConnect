# 实体支持注册表

在上游 HKMP 注册表基础上增加神居格林的 Firebat Pillar，以及 0.1.8 的 Needle 与 Jellyfish Spawner；258 条目、239 种类型，包括敌人、Boss、弹幕、门与机关。这是实现注册清单，不是实战逐项通过清单。

0.1.7 另提供实际动态生成物描述器、独立子体生成匹配和 `DynamicHazard` 通用危险物类型，后者不计入下面的具名 JSON 表。它覆盖已注册来源实际生成的带 DamageHero 物件；纯视觉特效不因此成为同步战斗实体。逐 Boss 实战范围见 VERIFICATION.md。

| 类型 | 游戏对象基础名称 | FSM |
|---|---|---|
| DynamicHazard | Needle |  |
| CombatSpawner | Jellyfish Spawner | Spawn |
| BattleGate | Battle Gate | BG Control |
| BattleGate | Dream Gate | Control |
| CameraLockArea | CameraLockArea |  |
| CityElevator | Ruins Lift | Lift Control |
| CrystalPeakPlatform | Mines Platform |  |
| DreamPlatform | F Plat |  |
| DreamPlatform | dream_plat_ |  |
| Crawlid | Crawler | Crawler |
| Tiktik | Climber |  |
| Vengefly | Buzzer | chaser |
| WanderingHusk | Zombie Runner | Zombie Swipe |
| HuskBully | Zombie Barger | Zombie Swipe |
| HuskHornhead | Zombie Hornhead | Zombie Swipe |
| Gruzzer | Fly | Bouncer Control |
| AspidHunter | Spitter | spitter |
| HuskGuard | Zombie Guard | Zombie Guard |
| LeapingHusk | Zombie Leaper | Zombie Leap |
| AspidMother | Hatcher | Hatcher |
| AspidHatchling | Hatcher Baby Spawner | Control |
| HuskWarrior | Zombie Shield | ZombieShieldControl |
| Goam | Worm | Worm Control |
| Baldur | Roller | Roller |
| ElderBaldur | Blocker | Blocker Control |
| FalseKnight | False Knight New | FalseyControl |
| FalseKnightHead | Head | Health Check |
| FalseKnightBarrels | FK Barrel Summon | summon |
| FalseKnightFloor | Normal 1 |  |
| FalseKnightFloor | Normal 2 |  |
| FalseKnightFloor | Cracked 1 |  |
| FalseKnightFloor | Cracked 2 |  |
| FalseKnightFloor | Broken |  |
| FalseKnightFloor | Break Floor |  |
| GruzMother | Giant Fly | Big Fly Control |
| BroodingMawlek | Mawlek Body | Mawlek Control |
| BroodingMawlekArm | Mawlek Arm L | Mawlek Arm Control |
| BroodingMawlekArm | Mawlek Arm R | Mawlek Arm Control |
| BroodingMawlekHead | Mawlek Head | Mawlek Head |
| BroodingMawlekDummy | Dummy |  |
| Mosscreep | Moss Walker | Moss Walker |
| Mossfly | Moss Flyer | Moss Flyer |
| Mosskin | Mossman_Runner | Zombie Swipe |
| VolatileMosskin | Mossman_Shaker | Fungus Zombie Attack |
| FoolEater | Plant Trap | Plant Trap Control |
| Squit | Mosquito | Mozzie |
| Obble | Fat Fly | Fatty Fly Attack |
| Gulka | Plant Turret | Plant Turret |
| Durandoo | Acid Walker | Acid Walker |
| Duranda | Acid Flyer | Acid Flyer |
| MassiveMossCharger | Mega Moss Charger | Mossy Control |
| MossCharger | Moss Charger | Mossy Control |
| MossKnight | Moss Knight | Moss Knight Control |
| Aluba | Lazy Flyer | Lazy Flyer |
| VengeflyKing | Giant Buzzer | Big Buzzer |
| VengeflySummon | Buzzer Summon |  |
| ChargedLumafly | Zap Cloud | zap control |
| Hornet | Hornet Boss | Control |
| Uoma | Jellyfish Baby | Jellyfish Baby |
| Ooma | Jellyfish | Jellyfish |
| OomaCorpse | Corpse Jellyfish | corpse |
| OomaCore | Lil Jellyfish | Lil Jelly |
| Uumuu | Mega Jellyfish | Mega Jellyfish |
| UumuuQuirrel | Quirrel Land | Watch |
| Ambloom | Fung Crawler |  |
| Fungling | Fungoon Baby | Fungoon baby |
| Fungoon | Fungus Flyer | Fungus Flyer |
| Sporg | Mushroom Turret | Shroom Turret |
| SporgSpore | Spore Bomb | Spore Bomb |
| FungifiedHusk | Zombie Fungus | Fungus Zombie Attack |
| Shrumeling | Mushroom Baby | Mushroom Mini |
| ShrumalWarrior | Mushroom Roller | Mush Roller |
| ShrumalOgre | Mushroom Brawler | Shroom Brawler |
| MantisYouth | Mantis Flyer Child | Mantis Flyer |
| MantisWarrior | Mantis | Mantis |
| MantisThrone | Mantis Lord Throne | Mantis Throne Sub |
| MantisThrone | Mantis Lord Throne | Mantis Throne Main |
| MantisLordBattle | Battle Main | Start |
| MantisLord | Mantis Lord | Mantis Lord |
| MantisLordBattle | Battle Sub | Start |
| MantisLord | Mantis Lord S | Mantis Lord |
| MantisLordCage | mantis_cage_down | Cage Control |
| MantisLordFloor | mantis_lord_opening_floors | Floor Control |
| MantisLordFloor | Floor |  |
| HuskSentry | Ruins Sentry | Ruins Sentry |
| HeavySentry | Ruins Sentry Fat | Ruins Sentry Fat |
| WingedSentry | Ruins Flying Sentry | Flying Sentry Nail |
| LanceSentry | Ruins Flying Sentry Javelin | Flying Sentry Javelin |
| Mistake | Mage Blob | Blob |
| Folly | Mage Balloon | Control |
| Folly | Mage Balloon Spawner | Control |
| SoulTwister | Mage | Mage |
| SoulOrb | Mage Orb | Orb Control |
| SoulWarrior | Mage Knight | Mage Knight |
| SoulMaster | Mage Lord | Mage Lord |
| SoulMasterOrbSpinner | Orb Spinner | Summon Orbs |
| SoulMasterPhase2 | Mage Lord Phase2 | Mage Lord 2 |
| SoulMasterFakeQuake | Knight Get Fake Quake | Get Quake |
| SoulMasterWindow | mage_window | Break |
| SoulMasterWindow | break_ground |  |
| HuskDandy | Royal Zombie | Attack |
| CowardlyHusk | Royal Zombie Coward | Coward Swipe |
| GluttonousHusk | Royal Zombie Fat | Attack |
| GorgeousHusk | Gorgeous Husk | Attack |
| GreatHuskSentry | Great Shield Zombie | ZombieShieldControl |
| WatcherKnight | Black Knight | Black Knight |
| TheCollector | Jar Collector | Control |
| CollectorJar | Spawn Jar |  |
| Belfly | Ceiling Dropper | Ceiling Dropper |
| Pilflip | Flip Hopper | Attack |
| Hwurmp | Inflater | Inflater |
| HwurmpChild | Enemy Inflater | Send Inflate Event |
| HwurmpChild | Bouncer |  |
| Bluggsac | Egg Sac |  |
| Flukefey | Fluke Fly | Fluke Fly |
| Flukemon | Flukeman | Flukeman |
| FlukemonBot | Flukeman Bot | Flukeman Bot |
| FlukemonTop | Flukeman Top | Flukeman Top |
| DungDefender | Dung Defender | Dung Defender |
| LargeDungBall | Dung Ball Large | Ball Control |
| SmallDungBall | Dung Ball Small | Ball Control |
| DungDefenderBurrow | Burrow Effect | Burrow Effect |
| DungDefenderCorpse | Corpse Dung Defender | Corpse |
| Flukemarm | Fluke Mother | Fluke Mother |
| HuskMiner | Zombie Miner | Zombie Miner |
| EnragedGuardian | Zombie Beam Miner Rematch | Beam Miner |
| CrystallisedHusk | Zombie Beam Miner | Beam Miner |
| Shardmite | Mines Crawler | Mines Crawler |
| Glimback | Crystal Crawler | Crawler |
| CrystalHunter | Crystal Flyer | Crystal Flyer |
| CrystalCrawler | Crystallised Lazer Bug | Laser Bug |
| CrystalTurret | Laser Turret | Laser Bug |
| CrystalGuardian | Mega Zombie Beam Miner | Beam Miner |
| CrystalGuardianLaser | Laser Turret Mega | Laser Bug Mega |
| FuriousVengefly | Angry Buzzer | Control |
| VolatileGruzzer | Bursting Bouncer | Bouncer Control |
| ViolentHusk | Bursting Zombie | Attack |
| SlobberingHusk | Spitting Zombie | Spit |
| Dirtcarver | Baby Centipede | Centipede |
| CarverHatcher | Centipede Hatcher | Centipede Hatcher |
| Garpede | Big Centipede |  |
| CorpseCreeper | Zombie Spider | Chase |
| Deepling | Tiny Spider | Spawn |
| Deephunter | Spider Mini | Spider |
| LittleWeaver | Spider Flyer | Control |
| StalkingDevout | Slash Spider | Slash Spider |
| Nosk | Mimic Spider | Mimic Spider |
| NoskBlob | Vomit Glob Nosk | Vomit Glob |
| ShadowCreeper | Abyss Crawler |  |
| LesserMawlek | Lesser Mawlek | Lesser Mawlek |
| Mawlurk | Mawlek Turret | Mawlek Turret |
| InfectedBalloon | Parasite Balloon | Control |
| LostKin | Lost Kin | IK Control |
| LostKinCorpse | Corpse Infected Knight Dream | corpse |
| BrokenVessel | Infected Knight | IK Control |
| BrokenVesselCorpse | Corpse Infected Knight | corpse |
| Boofly | Blow Fly | Blow Fly |
| PrimalAspid | Super Spitter | spitter |
| GreatHopper | Giant Hopper | Hopper |
| Hopper | Hopper | Hopper |
| GrubMimic | Grub Mimic | Grub Mimic |
| Hiveling | Bee Hatchling | Bee |
| Hiveling | Hiveling Spawner | Control |
| HiveSoldier | Bee Stinger | Bee Stinger |
| HiveGuardian | Big Bee | Big Bee |
| HuskHive | Zombie Hive | Hive Zombie |
| SpinyHusk | Garden Zombie | Attack |
| Loodle | Grass Hopper | Crazy Hopper |
| Loodle | Sprite | Damage Flash |
| MantisPetra | Mantis Heavy Flyer | Heavy Flyer |
| MantisPetraScythe | Shot Mantis | Shot Mantis |
| MantisPetraSummon | Dragonfly Summon | summon |
| MantisTraitor | Mantis Heavy | Mantis |
| TraitorLord | Mantis Traitor Lord | Mantis |
| ColosseumManager | Colosseum Manager | Battle Control |
| ColosseumCageSmall | Colosseum Cage Small | Spawn |
| ColosseumCageLarge | Colosseum Cage Large | Spawn |
| ColosseumPlatform | Colosseum Platform | Control |
| ColosseumPlatform | Platform |  |
| ColosseumSpike | Colosseum Spike | Control |
| ColosseumWall | Colosseum Wall | Control |
| ColosseumWall | Wall |  |
| SharpBaldur | Colosseum_Armoured_Roller | Roller |
| ArmouredSquit | Colosseum_Armoured_Mosquito | Mozzie2 |
| ShieldedFool | Colosseum_Shield_Zombie | ZombieShieldControl |
| SturdyFool | Colosseum_Miner | Zombie Miner |
| HeavyFool | Colosseum_Worm | Ruins Sentry Fat |
| WingedFool | Colosseum_Flying_Sentry | Flying Sentry Nail |
| BattleObble | Blobble | Fatty Fly Attack |
| Oblobble | Mega Fat Bee | Fatty Fly Attack |
| VoltTwister | Electric Mage | Electric Mage |
| ColosseumCageZote | Colosseum Cage Zote | Control |
| Zote | Zote Boss | Control |
| Tamer | Lancer | Control |
| Beast | Lobster | Control |
| Xero | Ghost Warrior Xero | Attacking |
| XeroNail | Sword | xero_nail |
| Gorb | Ghost Warrior Slug | Attacking |
| ElderHu | Ghost Warrior Hu | Attacking |
| Marmu | Ghost Warrior Marmu | Control |
| NoEyes | Ghost Warrior No Eyes | Attacking |
| Galien | Ghost Warrior Galien | Movement |
| GalienScythe | Galien Hammer | Control |
| GalienMiniScythe | Galien Mini Hammer | FSM |
| Markoth | Ghost Warrior Markoth | Attacking |
| MarkothShield | Markoth Shield | Control |
| Wingmould | White Palace Fly | Control |
| Kingsmould | Royal Gaurd | Guard |
| KingsmouldBlade | Shot Kings Guard | Spin |
| Sibling | Shade Sibling | Control |
| HornetSentinelSpikes | Barb Region | Spawn Barbs |
| HornetSentinelSpike | Hornet Barb | Control |
| HollowKnight | Hollow Knight Boss | Control |
| Radiance | Radiance | Control |
| RadianceOrb | Radiant Orb | Orb Control |
| RadianceNailComb | Radiant Nail Comb | Control |
| RadianceNail | Radiant Nail | Control |
| GreyPrinceZote | Grey Prince | Control |
| Zoteling | Zoteling | Control |
| VolatileZoteling | Zote Balloon | Control |
| WhiteDefender | White Defender | Dung Defender |
| GrimmkinSpawner | Flamebearer Spawn | Spawn Control |
| GrimmkinNovice | Flamebearer Small | Control |
| GrimmkinMaster | Flamebearer Med | Control |
| GrimmkinNightmare | Flamebearer Large | Control |
| Grimm | Grimm Boss | Control |
| GrimmBat | Grimm Firebat | Control |
| GrimmFirebatPillar | Firebat Pillar | Control |
| NightmareKingGrimm | Nightmare Grimm Boss | Control |
| NightmareKingGrimmBat | Nightmare Firebat | Control |
| GrimmSpikes | Grimm Spike Holder | Spike Control |
| GrimmFireball | Flameball Grimmballoon |  |
| HiveKnight | Hive Knight | Control |
| HiveKnightSpike | Hive Knight Glob | Control |
| HiveKnightBee | Bee Dropper | Control |
| Flukemunga | Fat Fluke | Control |
| PaleLurker | Pale Lurker | Lurker Control |
| Revek | Ghost Battle Revek | Control |
| SoulTyrant | Dream Mage Lord | Mage Lord |
| SoulTyrantPhase2 | Dream Mage Lord Phase2 | Mage Lord 2 |
| OroMato | Brothers | Combo Control |
| Oro | Oro | nailmaster |
| Mato | Mato | nailmaster |
| Sheo | Sheo Boss | nailmaster_sheo |
| Sly | Sly Boss | Control |
| PureVessel | HK Prime | Control |
| PureVesselBlast | HK Prime Blast | Control |
| WingedNosk | Hornet Nosk | Hornet Nosk |
| WingedNoskGlobDropper | Glob Dropper | Dropper |
| TurretZoteling | Zote Turret | Control |
| LankyZoteling | Zote Crew Tall | Control |
| HeadOfZote | Zote Thwomp | Control |
| FlukeZoteling | Zote Fluke | Control |
| ZoteCurse | Zote Salubra | Control |
| HeavyZoteling | Zote Crew Fat | Control |
| AbsoluteRadiance | Absolute Radiance | Control |
| RadiancePlatform | Radiant Plat | radiant_plat |
| RadianceAbyss | Abyss Pit | Ascend |
