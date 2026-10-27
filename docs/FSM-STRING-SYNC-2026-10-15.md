# FSM 字符串遗漏修复与 Mines_11 越界调查

2026-10-15。修复提交 `1544a05dcd4e3f6c14b6afd60c4e00acd967a308`，未升版本、打包或安装。生产修改只有字符串快照初始化、采样两处 null 规范化。

## 已修问题

`Entity.ProcessHostFsm` 原先将原生 `FsmString.Value` 的 null 直接存入快照。`OnUpdate.CondAddData` 将 null 当作“没有上次值”并永久跳过，因此之后赋上有效值也不会更新快照或发包。若只去掉该判断，后续清回 null 还会在比较或 `Packet.Write(string)` 中失败。

现在初始化快照和读取待发送字符串时均使用 `Value ?? string.Empty`，使初值和后续值采用同一可序列化表示。原生变量表、动作和网络格式不变。清回 null 在网络上表示清为空字符串；协议仍不区分这两种空值。未增加重试、catch、数组扩容或状态重置。

使用真实 PlayMaker 对象与生产 `Entity` 的隔离夹具验证：初始 null 后改为 `armed`、改为 `phase-two`、清回 null，均经生产采样和实际编解码到达接收实体的原生/副本；重复 null 不重发，合并缓存中的清空值可供迟加入和接管恢复。发送器没有连接网络，避免将夹具实体发布进真实房间。这是确定性同步路径验证，不是实际玩家触发复现，也不算 FSM 字符串的双机 UDP/FRP 验收。

## Mines_11 越界仍未解决

用户 0.1.15 的私密日志中，同一次 Mines_11 访问出现四次“未知 String 下标 0”紧接 `snapshot.Strings[index]` 越界。两份此前收集的日志含相同记录，不能算两次独立复现。原异常没有实体 ID 或 FSM 名，不能直接归因于邻近的 HuskMiner/Shardmite 警告。

接收代码按每个原生/副本 FSM 的变量表检查下标，却写入同一原生快照；不同长度可形成该异常，属于有具体代码路径的风险。导致长度不同的真实触发链仍缺失，不能把上述 null 修复当作数组布局修复。

本轮在原生 Mines_11、right1 入场，真实 UDP 同机双端均登记 11 个实体。测试插件记录登记前后、原生 Awake 前后和首次字符串接收时的布局。两次修复前观察中，所有已登记实体根 FSM 的字符串表均为空，没有收到根字符串更新，也未复现越界。场景另有带字符串的子 FSM，但不能据此认定其数据被误用。暂未更改下标检查、丢弃字段或重建变量表；仍需实际失败时的实体/FSM/变量名和初始化时序。

## 运行记录

全部目录位于忽略的 `.test-results`，保留失败，不打包私人日志。隔离游戏静音、内存角色、拦截三种 SaveGame 重载，不修改日常游戏或存档。

| 目录 | 用途 | 结果与边界 |
|---|---|---|
| `fsm-layout-mines11-baseline-20261015` | 修复前原生房间布局，15 秒 | 双端终态及严格日志通过；未复现用户越界 |
| `fsm-layout-mines11-awake-20261015` | 补充 Awake 布局，5 秒 | 双端终态及严格日志通过；未复现用户越界 |
| `fsm-null-string-baseline-20261015` | null 初值夹具失败基线 | 双端均在第一次赋值后明确失败：`String transition did not publish a reliable update`；保留 `.failed`，不计验收 |
| `fsm-null-string-candidate-20261015` | 未提交候选 | 两端各四项字符串检查及正常退出通过；不替代最终哈希 |
| `fsm-null-string-final-20261015` | 最终提交构建，Mines_11 5 秒与夹具 | 双方各四项字符串检查、done、offline、server-stopped、严格日志通过；停机后复扫通过 |

最终构建的 143 项回归、14 项日志门槛检查通过；记录为最终目录内的 `regression.txt`、`log-gate.txt`、`binaries.json` 和 `final-verification.json`。构建有既有 CS1573 注释警告，无错误。两个隔离进程已停止，Smoke 改名为 `.disabled-fsm-string-20261015-final`。最终隔离载荷、Smoke 构建输出和普通测试构建输出哈希一致，文档收尾不再重建 DLL。

| 最终生产 DLL | SHA-256 |
|---|---|
| HKMP.dll | `DCBF2E1C02D42C106604082AD5EE5927BA2F3B6D647A2D0F3DFB6BE39410AF9D` |
| HollowConnect.dll | `750219AEE6B739203A5EB510116090BE74A298A4671D0D6D67C060D15F5B8A0C` |
| HollowConnect.Core.dll | `71DC2E8A22B822493EBCC95BCBE1CED80A8AE9C6CEE695240C80782DE1C0B0CA` |

HKMP ProductVersion 为 `1.0.0+1544a05dcd4e3f6c14b6afd60c4e00acd967a308`。本轮只读核对的游戏程序集来自 `E:\SteamLibrary\steamapps\common\Hollow Knight\hollow_knight_Data\Managed`：Assembly-CSharp.dll 4,064,768 字节 / `5944411BD93830369390A4B51766EE68C4AB26195B299E25A07B5E7D0E00086D`；PlayMaker.dll 212,480 字节 / `0EF0E7829D125E1F632C8A189260EC6C6882630BE6932C8C7AE032EFBC53469A`。

本轮没有重新验收平台/城内流量降幅或完整战斗矩阵；旧构建的相应结果见 [平台与城内专项](PLATFORM-CITY-PERFORMANCE-2026-10-15.md)。地图购买后持续不可见、实际攻击剩余同步开销、历史接管问题及完整发布矩阵的边界见 [0.1.15 问题账本](evidence/runtime-blockers-0.1.15.json)，`releaseReady=false`。当前已交付 0.1.15 包不含本轮和此前未打包修复。
