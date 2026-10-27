# 五门失败冠军开场镜头（2026-10-23）

隔离双端已复现失败冠军开场后，一端镜头正常，另一端仍停在开场位置。原生五门场景为 `GG_Failed_Champion`，序列索引 47；该索引包含中场房间，不是战斗场次数。

## 原因与修复

| 项目 | 证据与结论 |
| --- | --- |
| 已确认运行时问题 | 基线 `champion-camera-20261023-baseline`：权威端的 `CamLock Intro/Deactivate` 收到 `INTRO OFF` 并自行停用；另一端持续停在 `State 1`，镜头固定在约 `(60.2, 32.8)`。 |
| 原生发送路径 | `False Knight Dream/FalseyControl` 的 `Music` 状态执行 `SendEventToRegister(INTRO OFF)`；接收者的 `State 1 → State 2` 执行原生 `ActivateGameObject(false)`。 |
| 同步缺口 | `SendEventToRegister` 不在同步动作表中，副本不执行这项动作；后来入场时也不能只凭已经过去的 `Music` 状态补发。 |
| 与叛徒领主的关系 | 都是开场镜头未释放，但叛徒领主使用受隔离过滤的 `BATTLE STARTED`。本次需要补充原生发送路径的完成状态。 |

新增 `BossIntroCamera`，仅为该场景的失败冠军本体记录原生开场解除阶段，并通过现有、按字段版本过滤的可靠 FSM 布尔快照保留“开场已结束”。副本收到后，只向同场景、存在 `INTRO OFF` 到自身停用动作的原生 `CameraLockArea` 发送事件，由原生动作释放镜头。尚未初始化的本地接收者会等待就绪，成功后停止检查。

不执行整个 `EventRegister` 广播，也不直接改写相机坐标、模式或锁区激活状态。正常战斗锁区、入口门以及其它原生事件接收者继续使用各自流程。没有新增网络消息或改变数据布局；只给这对场景 FSM 追加一个同步布尔字段。原有战吼同步使用的布尔发布函数改为通用名称，行为保持一致。

## 验证

用例选择、逐组状态、哈希与严格日志记录保存在 `.analysis/failed-champion-camera-20261023/`。验收结果以该目录 `final-evidence.json` 为准。

增量构建通过，10 项世界隔离及乱序稀疏 FSM 快照核心检查通过，7 组双端检查均完成且通过严格日志门槛。全部使用相同的最终生产 DLL 与 Smoke 哈希。

| 双端用例 | 覆盖 | 结果 |
| --- | --- | --- |
| `champion-camera-20261023-host-a` | 失败冠军，主机权威 | 通过 |
| `champion-camera-20261023-guest` | 失败冠军，客机权威 | 通过 |
| `champion-camera-20261023-late-host` | 主机开场结束后，客机入场 | 通过 |
| `champion-camera-20261023-late-guest` | 客机开场结束后，主机入场 | 通过 |
| `champion-camera-20261023-handoff` | 客机权威原生离场，主机接管 | 通过 |
| `champion-camera-20261023-traitor` | 叛徒领主原生开场镜头回归 | 通过 |
| `champion-camera-20261023-uumuu` | 乌姆原生开场镜头回归，反向权威 | 通过 |

实际双端检查开场锁自行停用、正常战斗锁区保留、角色操作恢复；失败冠军还移动角色，要求原生镜头跟随且角色保持可见。各组复用本地屏障动作边界检查，包括即时、延迟及定向事件和外部变量／激活／位置／缩放隔离。运行结束后两份隔离游戏进程均为 0。

仅使用既有两份隔离游戏及禁止存盘的内存角色，通过本机真实 UDP 运行原生五门序列上下文。不是完整五门通关、完整人工战斗或跨机器验证；未修改个人存档，未打包、安装或升版。

游戏依赖与现成原生审计一致，两份隔离副本均为：

- `Assembly-CSharp.dll`：`5944411BD93830369390A4B51766EE68C4AB26195B299E25A07B5E7D0E00086D`
- `PlayMaker.dll`：`0EF0E7829D125E1F632C8A189260EC6C6882630BE6932C8C7AE032EFBC53469A`

构建目录：`src/HollowConnect/bin/Release/net472/`。最终 DLL 哈希由 `final-binaries.json` 及 `final-evidence.json` 共同记录：

| 文件 | SHA256 |
| --- | --- |
| HKMP.dll | `6CD287E9D699BFAD14B945959B01AF279D6C8CBF30E54A5FC6CFB0F42A1E16D6` |
| HollowConnect.dll | `78AB23C465C76B126D7B68FEF9E663D3A7581CEB528BC746B112031A12C3CF56` |
| HollowConnect.Core.dll | `20D8D11C22ADE2F4661A1FCE915543F77905ABE4CD0EC678E0496D1D0627CD69` |
