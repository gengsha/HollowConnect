# HollowConnect 0.2.5

2026-10-30。Windows 完整安装包与源码包，GitHub 正式 Release。网络标识为 `0.2.5-l12`，引擎为 `3.0.0-hc13`。所有玩家须使用同一完整包并重新开房。

## 联网流程修复

- 统一 Boss 战结束、门禁、场景重入和延迟交接的收尾逻辑，避免部分 Boss 死亡后战场仍锁住或玩家无法离场。
- 修复竞技场上、下、左、右边界的碰撞方向，边界会把空中的玩家送回设计的战斗场地，不再形成无法向下通过的平台。
- 保留并覆盖原有 Boss 死亡存档、重入复活、Boss 索敌和共享血量生命周期修复。

## 主动观战（Beta，本机）

- 菜单开关默认关闭。开启后，落地时按 F4 观看同房存活队友，F7 切换目标。
- 观战本体隐藏、关闭碰撞和物理模拟，不受伤、不攻击、不参与 Boss 索敌和战斗存活判定。
- 再按 F4、目标离开、没有战斗玩家、断线或全队失败时，返回德特茅斯 `Town/RestBench`，保持联机。
- 暂停只打开原生菜单；观战退出不算死亡、不扣吉欧、不增加死亡统计。
- 观战者之外的战斗玩家全部死亡时，按原有团灭屏障处理；观战者不会拖住团灭，会回到德特茅斯长椅。

## 共享血量（Beta，房主）

共享血量继续默认关闭。观战者单独回椅不会补满团队血条，最后一个战斗玩家死亡时也不会被观战者阻止团灭。其他共享血量、休息和救援规则见[玩法说明](LINKED-HEALTH.md)。

## 下载与安装

- [HollowConnect-0.2.5-windows.zip](https://github.com/gengsha/HollowConnect/releases/download/v0.2.5/HollowConnect-0.2.5-windows.zip)：游玩用完整包，含一键安装器及依赖。
- [HollowConnect-0.2.5-source.zip](https://github.com/gengsha/HollowConnect/releases/download/v0.2.5/HollowConnect-0.2.5-source.zip)：对应源码。
- [SHA256SUMS-0.2.5.txt](https://github.com/gengsha/HollowConnect/releases/download/v0.2.5/SHA256SUMS-0.2.5.txt)：压缩包校验值。

关闭游戏，完整解压 Windows 包，运行 **一键安装.exe** 并选择安装／更新。安装后所有玩家使用同一版本并重新开房。

## 验证范围与限制

升版前的当前工作构建通过 **71 项相关核心检查、26 组真实 UDP 双端实机检查**，范围包括 9 组主动观战、2 组原生大黄蜂权威与结算、12 项房间矩阵和 3 项既有专项。版本号升到 0.2.5 后没有把旧二进制哈希追认为新版本实机验证；本次 Release 按用户明确请求沿用这些开发验证记录完成构建和包完整性校验。

完整证据见 [主动观战说明](VOLUNTARY-OBSERVATION-2026-10-30.md)、[开发验证记录](evidence/development-validation-0.2.5.json)、[版本交付记录](evidence/runtime-room-0.2.5.json) 和 [问题账本](evidence/runtime-blockers-0.2.5.json)。真实跨机器网络、三人目标轮换、所有 Boss 全招式和完整剧情长流程仍不在本轮覆盖内。
