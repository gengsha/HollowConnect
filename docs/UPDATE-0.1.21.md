# HollowConnect 0.1.21

本版增加两项联机功能：

- 配置文件与模组菜单增加 `ShareCharms`，默认开启。房主开房前设为 `false` 可单独关闭护符获得状态共享，护符槽继续按来源共享；装备各自独立，已获得的护符不会撤销。护符共享仍以总开关 `ShareProgress` 开启为前提。
- 梦之钉实际命中同房间的存活队友，施放者交替获得 16、17 点灵魂，两次共 33 点，正好够普通聚焦回一格血。一次挥击最多结算一次，挥空不加魂，梦之支配者不翻倍；队友不受伤，满魂沿用原版上限。

所有人关闭游戏，完整解压 `HollowConnect-0.1.21-windows.zip`，运行 **一键安装.exe → 安装 / 更新**，然后重新开房。菜单应显示 **HollowConnect 0.1.21 / HKMP 3.0.0-hc11**。房主和队友必须使用同一完整包，FRP 配置沿用。

配置文件位于 `%USERPROFILE%\AppData\LocalLow\Team Cherry\Hollow Knight\CoopMod.GlobalSettings.json`，关闭游戏后编辑合法 JSON。旧配置缺少 `ShareCharms` 时按默认开启处理。

新增功能已通过 19 项相关回归和护符开/关两组同机真实 UDP 双端专项，包含双方重连、实际梦钉挥击、满魂及原生聚焦回血；见[实现与验证](CHARMS-TEAMMATE-DREAM-2026-10-17.md)。本次升版只编译和核对包内容，不重跑游戏或历史测试矩阵；原始验收保留各自实际 DLL 哈希，不将新版包记为重新实测。见[打包记录](evidence/runtime-room-0.1.21.json)。

历史未解问题继续保留，见[问题账本](evidence/runtime-blockers-0.1.21.json)。本次只打包，不自动安装或修改个人存档。
