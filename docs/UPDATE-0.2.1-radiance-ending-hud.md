# HollowConnect 0.2.1：剧情辐光结局 HUD 修复

2026-10-27。测试构建标识为 `0.2.1-playtest-radiance-ending-hud`，基于 0.2.1 的当前生产代码生成。房主和所有队友应使用同一完整包。

## 本次更新

- 修复剧情辐光通关后经过片尾并回到长椅时，血量和灵魂 HUD 消失的问题。
- 修复结局场景关闭原生 HUD Camera 后，回到游戏场景无法恢复显示的问题。

结局场景 `Cinematic_Ending_C`、`End_Credits` 和 `End_Game_Completion` 会关闭持久 HUD Camera。旧的回程逻辑只重新激活 HUD Canvas 并发送 `IN` 事件，没有恢复 Camera 或确保 HUD FSM 已启用。本构建在原生长椅场景准备完成后，同时恢复 HUD Camera、Camera 组件、HUD Canvas 及其 FSM，再发送原生 `IN` 事件。

## 验证范围

- 双端真实剧情辐光结局往返：`Cinematic_Ending_C` → `End_Credits` → `End_Game_Completion` → `PermaDeathUnlock` → Town。
- 房主和客机回程后均确认 HUD Camera、HUD Canvas 和 HUD FSM 已恢复并可见。
- 连接状态和个人 profile 保持正常。
- 既有合成结局退出边界双端检查通过。
- Smoke 增量构建、Core 增量构建和 Installer 构建通过。
- Windows 包和源码包完整性校验通过；包内不包含测试插件或个人存档。

## 构建信息

- Windows 包：`HollowConnect-0.2.1-playtest-radiance-ending-hud-windows.zip`
- 源码包：`HollowConnect-0.2.1-playtest-radiance-ending-hud-source.zip`
- HKMP.dll：`9DB32A94BF88A9E31666E708AAA074EE4C62276BCAE29539B0C9381A6CBCE1DA`
- HollowConnect.dll：`AA58ACAE98FA02C735B1CD9892CF2700B0FA4E18A997194E3461D9DA2BD12523`
- HollowConnect.Core.dll：`746C12EF77F5097A57A7416CCA5BE728BE33F3163FEAF527A2804AA6852426F6`
- BouncyCastle.Cryptography.dll：`3D7CED058D7264036323F8CFD87D3203761B5FE45DFDDF1EBCE069C1E36B81B8`

## 已知限制

- 这是测试渠道构建，`releaseReady=false`，不代表正式发行验收完成。
- 0.2.1 问题账本仍有 22 项未解决运行时记录。
- Core 全量测试结果为 293 通过、1 失败；唯一失败是既有进度夹具缺少 `grimmFlames2`，与本次 HUD 修复无关。
- 直接结局往返用例确认了回椅后的 HUD 状态，但没有在同一用例中追加完整死亡流程。

## 参考记录

- [剧情辐光结局回椅 HUD 修复](ENDING-HUD-FIX-2026-10-27.md)
- [结局 HUD 验证证据](evidence/ending-hud-fix-2026-10-27.json)
- [0.2.1 问题账本](evidence/runtime-blockers-0.2.1.json)
- [0.2.1 原始更新日志](UPDATE-0.2.1.md)
