# Steam 好友联机（预览模块）

Steam 模式已加入源码，使用游戏自带 Steam 接口和独立的 `HollowConnect.Steam.dll`。
Steam 消息通过本机 UDP 桥接进入现有局域网／IP 联机流程，继续使用同一套 HKMP
数据包、伤害、Boss、救援、存档和房间逻辑。这些共用逻辑修复一次，两种方式同时生效；
好友邀请、房间发现和 Steam 通道的建立／清理由新增接入模块负责。
`0.2.7` Windows 完整包已包含本模块和匹配的 HKMP，可从 [GitHub Release](https://github.com/gengsha/HollowConnect/releases/tag/v0.2.7) 下载。
构建与本机自动检查不代表真实双账号邀请、中继或跨机器连接已经通过。

当前模块面向 Windows 64 位 Steam 版空洞骑士。两端需要在线的 Steam 客户端、相同版本的 HollowConnect 和本模块。
请保留游戏原装的 Steam DLL。Steam 模式无须填写公网 IP 或映射 UDP 端口。

## 创建、邀请和加入

主菜单的「局域网联机」与「Steam 联机」及两页控件均使用原生菜单组件，支持鼠标、键盘和手柄导航。

1. 返回游戏主菜单，选择「Steam 联机」，或按 F10 打开原生 Steam 菜单。
2. 输入玩家名，点击“创建好友房间”，选择主持游戏使用的存档；取消选档会返回 Steam 页面。
3. 房间创建后，按 F10，点击“邀请 Steam 好友”；也可以从 Steam 好友列表邀请好友加入游戏。
4. 好友接受邀请后会进入本房间。也可以右击房主的 Steam 好友条目选择“加入游戏”，或在 Steam 页面查看好友主持的游戏，房间较多时按“下一页”切换。
5. 客机继续使用原有的存档选择和确认流程。存档同步规则仍由房主的房间设置决定。

接受邀请时若游戏尚未启动，模块读取 Steam 的 `+connect_lobby` 启动参数，等主菜单就绪再加入。
若已经在游戏中或连接着另一个房间，邀请会暂存；请自行结束当前游戏并返回主菜单，再点击“加入邀请的房间”。
房主退出会结束房间，不会把原游戏服务器自动迁移给另一位大厅成员。旧房间邀请不能用于下一轮游戏。

原来的 IP／域名／端口入口改名为「局域网联机」，仍支持直连及内网穿透。使用 Steam 房间时，请先离开房间再切换连接方式。游戏内从 Steam 页面返回会进入原生暂停菜单，可继续游戏。

## 构建与开发安装

```powershell
.\scripts\Build.ps1
.\scripts\Build.ps1 -Target Steam
```

保留正常安装的 `HollowConnect.dll`、`HollowConnect.Core.dll`、`BouncyCastle.Cryptography.dll`，
使用同一次源码构建的 `src/HollowConnect/bin/Release/net472/HKMP.dll`，
再把 `src/HollowConnect.Steam/bin/Release/net472/HollowConnect.Steam.dll` 放入同一模组目录。
不要把 `HollowConnect.SteamProbe.dll` 或 `HollowConnect.Smoke.dll` 放入玩家安装。
移除 Steam 模块后，原有 IP 方式仍可使用。

桥接来源、固定提交及 LGPL 许可见 [第三方说明](STEAM-THIRD-PARTY.md)。对应源码随同版本 source ZIP 提供。

## 两个账号和跨机器验收

需要两台机器、两个拥有游戏的 Steam 账号和相同构建。以下项目使用真实 Steam 后端，不能用同机内存传输测试代替。

| 项目 | 操作与通过条件 |
|---|---|
| 在线邀请 | A 创建房间并邀请 B；B 接受后两端进入同一场景，互相可见、移动、受伤和救援正常。 |
| 好友列表加入 | B 从 Steam 好友列表右击 A 选择加入；不填写 IP 即完成相同的存档确认和游戏连接。 |
| 游戏关闭时邀请 | 关闭 B 的游戏；接受 A 的邀请启动游戏，进入主菜单后继续加入正确房间。 |
| 已有连接时邀请 | B 在另一局或正在加载时收到邀请；当前局不被强制断开，返回主菜单后可选择加入。 |
| 不同网络 | A、B 使用不同家庭网络，或一端使用手机热点；保持没有手工端口映射，重复邀请、加入和双向游戏操作。 |
| 断线与重建 | 客机离开后可再次加入；房主退出后客机结束旧连接；新建房间后旧邀请不接入另一轮游戏。 |
| Steam 离线 | Steam 离线／连接失败后，房间资源退出；返回主菜单后 IP 直连仍能正常使用。 |

记录两端 `ModLog.txt`、相同构建的 DLL SHA-256、Steam 账号是否在线、两个网络的类型和每项结果。
不要公开私人存档、Steam 好友列表或完整身份信息。
连接建立时，日志中的 `Steam connection established` 记录 `pingMs`、`relayPOP` 和 `remotePOP`。
若需要证明使用了 Steam 中继，应记录非零的 `relayPOP`；“不同网络连接成功”本身不能证明实际经过中继。
