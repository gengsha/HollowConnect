# 结局回椅后的 HUD 恢复

联机时，`UiManager` 将结局退出到主菜单的路径改为 `GameManager.ContinueGame()`，让本机回到自己的检查点并继续留在房间。结局保留了持久 HUD 的隐藏状态，这条返回路径此前没有发送恢复事件。

本次只在这个返回分支启动有限等待：同一存档角色回到新的、已完成入场的游戏场景后，激活原生 HUD Canvas 并发送 `IN`，恢复血量、灵魂等界面。断线、更换存档或等待超时就结束。保留原有回椅和房间连接行为；不改结局选择、存档进度或其它玩家的界面。

验证按用户的时间要求限制为增量构建和一组隔离双端检查，未跑完整结局或通用矩阵。

- 构建通过，只有既有 XML 注释警告。
- `ending-hud-20261024-a1` 两端完成且严格日志通过，约 33 秒。
- 使用原生 `OUT` 确认 HUD 实际移出显示位置；主机经过 `MainMenu`、客机经过 `MainMenuNoSave` 的既有 CutsceneHelper hook，执行真实 ContinueGame/回椅，要求 HUD 回到原生显示位置且 HUD 相机启用。
- 返回者保持连接与个人检查点；未参与返回的队友留在原房间、HUD 保持正常。
- 这是结局退出接口的定向夹具，未重放完整 Boss 战斗、片尾和全部结局。

状态、严格日志与实际 DLL 哈希见 `.analysis/ending-hud-20261023/status.json` 和 `.test-results/ending-hud-20261024-a1/`。构建目录为 `src/HollowConnect/bin/Release/net472/`。未打包、安装或修改个人存档。
