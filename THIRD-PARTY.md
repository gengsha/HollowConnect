# 第三方来源与许可

- **HKMP**：https://github.com/Extremelyd1/HKMP ，2026-10-11 获取的 `master` 源码快照（上游标记 3.0.0 开发版本）。修改副本完整置于 `src/HKMP`；LGPL-2.1-or-later，原许可见 `HKMP-LICENSE`。上游 README 声明："This library is free software; you can redistribute it and/or modify it under the terms of the GNU Lesser General Public License as published by the Free Software Foundation; either version 2.1 of the License, or (at your option) any later version." 本项目将其视为可选择 LGPL-3.0，以便与自有代码的 GPL-3.0-only 许可组合。此项目与官方发行的 HKMP 2.4.3 协议不同。
- **Hollow Knight Modding API v77**：https://github.com/hk-modding/api/releases/tag/1.5.78.11833-77 。Windows 官方依赖包未经修改，SHA256 `bc9f0db3d0916b05cd5a2420bb602fb1b239ce3ff6c289fd84bffb682fb8f1d6`。参考官方仓库许可及发行说明；它包含对原游戏程序集的补丁产物，原游戏权利属于 Team Cherry。
- **BouncyCastle.Cryptography 2.5.1**：https://www.nuget.org/packages/BouncyCastle.Cryptography/2.5.1 ，https://www.bouncycastle.org/licence.html ，Bouncy Castle License；由 HKMP 原项目依赖提供。许可正文附在依赖 NuGet 包中，发行包另含其许可副本。
- **HkmpPouch**：https://github.com/PrashantMohta/HkmpPouch ，仅检查接口与用途，没有将其作为本版运行依赖或复制其实现。
- 用户提供的丝之歌联机包仅用于确认模组结构与用户描述的需求，未将其游戏程序集或实现复制到 HollowConnect。

HollowConnect 的自有代码（包括 `src/HollowConnect`、`src/HollowConnect.Core`、`src/HollowConnect.Installer`、`tests` 和 `scripts`）自本次开源起采用 **GNU General Public License v3.0（GPL-3.0-only）**，完整条款见根目录 [LICENSE](LICENSE)。Copyright (c) 2026 HollowConnect contributors。

上游 HKMP 及其修改继续受 LGPL-2.1-or-later 许可约束，原文见 [HKMP-LICENSE](HKMP-LICENSE)。本项目的 GPL-3.0 许可声明不替代第三方代码、依赖或游戏资源的原有许可。
