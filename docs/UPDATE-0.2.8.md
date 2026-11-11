# HollowConnect 0.2.8

2026-11-11。Windows 完整安装包包含匹配的 HKMP、Steam 好友联机预览模块和依赖。所有玩家更新同一完整包后重新开房；显示版本为 0.2.8，网络标识为 0.2.8-l15，HKMP 引擎仍为 3.0.0-hc13。

## 自 0.2.7 以来的修复

- 联机受击时改为本地化停顿，保留原生受伤无敌和恢复节奏，避免房主或队友受击时冻结整个联机世界、Boss 投射物和另一名玩家的输入。
- 修复 Grimm 咆哮的结束回执、延迟生成的波纹发射器和独立弓模型清理。早到的结束事件会留在有序状态里，晚生成的客机对象也能收尾；正常咆哮的动作和控制仍由原生 FSM 驱动。
- 修复竞技场 Bronze/Silver 领奖对象在客机层级被统一暂停、领奖后权威交接丢失一次性激活动作的问题。奖励子树保留本地原生 FSM 与刚体，接管后只续接已授权的领奖状态，仍由原生拾取和完成标记决定奖励与开门。
- 补齐共享血量面具碎片、四片转化、重排回执和旧存档迁移的专项检查，避免把个人拾取历史、共享资源回执和团队血量容量混为一项。

## 安装

下载并完整解压 [HollowConnect-0.2.8-windows.zip](https://github.com/gengsha/HollowConnect/releases/download/v0.2.8/HollowConnect-0.2.8-windows.zip)，关闭游戏后运行 一键安装.exe。HollowConnect-0.2.8-source.zip 为对应源码，不能直接作为安装包。Steam 使用方法见 [联机说明](STEAM-MULTIPLAYER.md)。

## 验证与保留问题

本轮核心回归通过 53 项；Grimm 咆哮/弓清理与在线受击停顿专项通过 7 组同机双端流程；共享血量面具碎片和 Mawlek 领奖流程通过 linked-f、resources-h，Silver 普通房主与普通客机流程通过 silver-m、silver-o。记录见 [开发验证摘要](evidence/development-validation-0.2.8.json)。

Silver 权威交接在领奖交互范围检查处仍失败，不能声称断线中领奖已完整修复；该失败保留在 [问题账本](evidence/runtime-blockers-0.2.8.json)。用户反馈的“两人都未获得面具碎片”在本轮共享血量/四片转化流程中未复现，原因仍未确认。正式 Release 按用户请求打包，未重新执行完整 12 项房间矩阵；当前生产 DLL 哈希、旧验证来源和未解决项见 [发布记录](evidence/runtime-room-0.2.8.json)。

真实双账号 Steam 邀请、冷启动邀请、跨机器 P2P/中继仍需玩家验证；历史大黄蜂 Thrown 权威交接问题和其它未覆盖项继续保留在 [已知问题](KNOWN-ISSUES.md)。竞技场专项使用原生流程和受控加速波次，不能替代完整武器通关、全部 Boss/Pantheon 或完整三人认证。