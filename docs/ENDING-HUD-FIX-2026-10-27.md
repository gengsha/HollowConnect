# 剧情辐光结局回椅 HUD 修复（2026-10-27）

用户反馈：版本 0.2.1 中，双方击败剧情辐光后完成结局并回到长椅，血量和灵魂 HUD 消失；再次死亡也不能恢复，重启游戏才恢复。用户确认是剧情辐光，不是无上辐光，且问题发生在通关回椅后。

## 根因

精确游戏程序集审计和双端预检显示，`Cinematic_Ending_C` 及其后续 `End_Credits` / `End_Game_Completion` 场景属于非游戏场景。原生 `GameCameras.StartScene` 会关闭持久 HUD 相机；HUD Canvas 的 `activeSelf` 仍为 `true`，但 `activeInHierarchy` 为 `false`，其 `Globalise` 和 `Slide Out` FSM 也因此无法显示。旧的结局退出恢复代码只激活 Canvas 并发送 `IN`，没有重新激活 HUD 相机、启用 Camera，也没有确保 Canvas FSM 已启用。

## 修复

`src/HKMP/Ui/UiManager.cs` 的结局回程协程现在在原生长椅场景准备完成后：

- 激活 HUD Camera GameObject，并显式启用 Camera；
- 激活 HUD Canvas；
- 启用 Canvas 上的 HUD FSM，再发送原生 `IN` 事件。

没有修改辐光 FSM、Boss 状态或网络协议顺序。

## 验证

- `Cinematic_Ending_C` schema 双端预检通过；确认结局实际使用 `CinematicPlayer`，不是 `CutsceneHelper`。
- 真实剧情辐光路径：原生挑战入口、F8 加入、15 个升天平台两端一致、真实上劈进入 `Knight Pull`，随后切入 `Cinematic_Ending_C`。该路径记录在 `.test-results/radiance-ending-hud-20261026-native-ending-native-c/`；其中旧夹具第一次失败是将剧情场景误按 Godhome 的 `Final Idle` 阶段处理，第二次失败是 `Dream_Final_Boss`/`Dream_Final` 场景别名误判，均为测试夹具问题，已保留。
- 最小真实结局往返双端严格通过：`.test-results/radiance-ending-hud-20261026-native-c-return-native-f/`。两端均执行：`Cinematic_Ending_C` → 原生视频跳过 → `End_Credits` → `End_Game_Completion` → `PermaDeathUnlock` → Town。两端均记录：HUD Camera `True/True`、Canvas `True/True`、Canvas FSM 启用且回到 `Init/Idle`，连接和个人 profile 未改变。
- 既有合成退出边界双端通过：`.test-results/radiance-ending-hud-20261026-boundary-fix/`。
- Smoke 增量构建通过：`.analysis/radiance-ending-hud-20261026/build-smoke-final.log`；Core 增量构建通过：`.analysis/radiance-ending-hud-20261026/build-core-fix.log`。Core 仅保留既有 XML 参数注释警告，0 错误。
- Core 全量回归为 293 通过、1 失败；唯一失败是既有进度测试缺少 `grimmFlames2`（`.analysis/radiance-ending-hud-20261026/core-tests-final.log`），与本次 HUD 改动无关。

本次直接结局用例验证了回椅后的原生 HUD 相机、Canvas 与 FSM；没有在同一长用例中再额外执行一次完整死亡流程，避免把已有进度夹具失败混入结局证据。

最终 Smoke 生产依赖哈希：

- HKMP.dll `9DB32A94BF88A9E31666E708AAA074EE4C62276BCAE29539B0C9381A6CBCE1DA`
- HollowConnect.dll `AA58ACAE98FA02C735B1CD9892CF2700B0FA4E18A997194E3461D9DA2BD12523`
- HollowConnect.Core.dll `746C12EF77F5097A57A7416CCA5BE728BE33F3163FEAF527A2804AA6852426F6`
- BouncyCastle.Cryptography.dll `3D7CED058D7264036323F8CFD87D3203761B5FE45DFDDF1EBCE069C1E36B81B8`

未打包、未安装、未改版本号或个人存档。
