# HollowConnect 0.2.9

2026-11-13。Windows 提供 Legacy 与 Unity6 两套完整安装包，均包含匹配的 HKMP、Steam 好友联机预览模块和 Modding API。模组版本统一为 0.2.9，网络标识为 0.2.9-l15，HKMP 引擎仍为 3.0.0-hc13。

## 安装与版本选择

| 游戏 | 完整安装包 |
|---|---|
| 1.5.78.11833 / Unity 2020.2.2f1 | [HollowConnect-0.2.9-windows.zip](https://github.com/gengsha/HollowConnect/releases/download/v0.2.9/HollowConnect-0.2.9-windows.zip) |
| 1.5.12620 / Unity 6000.0.61f1（Steam public-beta） | [HollowConnect-0.2.9-unity6-windows.zip](https://github.com/gengsha/HollowConnect/releases/download/v0.2.9/HollowConnect-0.2.9-unity6-windows.zip) |

关闭游戏，完整解压匹配版本的 ZIP，运行 **一键安装.exe**，选择游戏目录后点击 **安装 / 更新**。源码 ZIP 用于开发。不要跨版本覆盖 DLL；Unity6 包包含固定社区 API 移植所需的全部依赖，安装器会检查游戏版本。所有队友更新同一配置的完整包后重新开房。新旧游戏混合房间尚未验证。

## 修复

- 共享进度下，水晶山峰七类敌人的原生距离、朝向及玩家感知可读取同场景的有效队友，不再只响应场景权威端的本机骑士。观战、倒地、离场或已失效的玩家位置不作为目标。
- 为远端玩家补齐受上述实体限制的原生 FSM 激活区域，按游戏实际区域大小启动休眠敌人。修复先入图玩家远离时，后入图玩家靠近飞虫仍无反应、激光爬虫不进入发射循环的问题。场景权威、同步格式及原生 AI 流程保持既有实现。
- 包含按攻击玩家独立计算的敌人原生受击冷却修复，同一玩家的重复碰撞仍受原生节奏约束；敌人阶段无敌、方向防御和法术原生多段规则保留。
- 两版共用玩法源码，游戏 API 差异使用条件编译。今后打包及核验默认同时生成 Legacy／Unity6，用户明确指定单版时才限于该版。

本次没有继续修改灵魂大师。“任意小怪完全打不死”的全部条件尚未复现；已确认并修复的是远端感知和休眠激活问题，不能据此声称所有类似报告都已解决。

## 验证与保留范围

本版执行 17 项相关核心检查，以及两版本各三组同机双端专项：房主先入图、客机先入图的水晶山峰远端唤醒／激光／真实骨钉击杀，以及独立受击无敌帧。实际结果与最终 DLL 哈希分别记录在 [Legacy 验证](evidence/development-validation-0.2.9.json) 和 [Unity6 验证](evidence/development-validation-0.2.9-unity6.json)。

本版按用户要求打包并发布至 [GitHub Release](https://github.com/gengsha/HollowConnect/releases/tag/v0.2.9)，完整 12 项房间矩阵未重新执行。真实双账号 Steam 跨机器、P2P／中继和新旧游戏混合联机不在本轮验收范围。继承问题继续保留在 [问题账本](evidence/runtime-blockers-0.2.9.json)，旧版 Mines FSM 字符串布局问题不因本次感知修复自动关闭。

Unity6 继续使用 [Modding API PR #164](https://github.com/hk-modding/api/pull/164) 的固定提交 `468f579f25e63da2188f39fe5ece15c25d2854d7`。该社区移植尚未合并，启动预加载的原生 SetupHeroRefs 空引用在仅加载同一 API 的历史对照中也出现；本次没有修改或隐藏该底座问题。许可见 [Unity6 API 许可](UNITY6-API-LICENSE.txt)。
