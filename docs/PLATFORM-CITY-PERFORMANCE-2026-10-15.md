# 水晶翻转平台本地运行与城内冗余同步

2026-10-15。用户反馈水晶山峰翻转平台卡顿，明确要求这种平台各自本地运行；另反馈泪水之城多处卡顿，截图网络延迟为 3318 ms。截图能证明当时显示高延迟，不能单凭数字确定拥堵位置或全部掉帧原因。

生产修改分两次本地提交：`cfce235` 移除水晶翻转平台登记，`65faba1972eaf25edf569a811772bc05fe755e72` 消除未使用根动画帧引起的可靠战斗快照。只修改源码并在隔离副本验证，未发版、打包或安装日常游戏。

## 已确认问题及最小修改

| 问题 | 证据与影响 | 修改 |
|---|---|---|
| 水晶翻转平台被登记为联网实体 | `Mines Platform` 注册项、全场 FlipPlatform 扫描及专用 FlipPlatformComponent；原生 Flip/Idle 协程与权威动画/CombatState 同时参与表现 | 删除注册项和专用扫描，恢复各自原生运行，不创建平台副本或同步实体 |
| 原生根动画帧反复触发整实体快照 | CombatState 每 0.05 秒写根 spriteId，再比较整包；接收端只对非空子路径应用帧 ID，根动画已由 EntityUpdate 回放，根帧字节没有被使用 | 根帧写既有合法哨兵 -1，停止无意义的变化；子体帧、颜色、碰撞及阶段字段继续按现有格式发送 |
| 城内真实双机全部卡顿 | 用户截图和体验已确认；同机批处理没有重现真实 FRP 持续掉帧 | 本轮只关闭已量化的冗余，不能认定所有卡顿已解决 |

没有新增协议、全场轮询、定时重试或对象池。原采样频率及首份/接管完整快照机制不变；修改让字节比较能过滤纯根动画变化。CityElevator 和其他交互没有从注册表删除。CrystalPeakPlatform / FlipPlatform 的枚举编号保留，避免改动其他类型的线上编号。

只读原生方法检查确认：`FlipPlatform.OnCollisionEnter2D` 在 Player 实际碰撞后停止 Idle、启动 Flip；Flip 自行切换上下尖刺并自然恢复 Idle。资产中 `Mines_35` 对应 level274，含 21 个平台，入口 left1；`Ruins2_04` 对应 level116，left1 有效。用户私密日志保留了这两个房间，旧版本分别登记 33 / 22 个实体。用户截图的精确房间没有额外询问确认，这两个房间用作有记录且包含目标类型的运行样本。

## 实际对照

使用同机隔离双客户端，进入真实场景、原生动画运行，稳定观察窗口均为 20 秒。下表是权威端 AddEntityData 生成的 **CombatState 载荷**，不是抓包总流量，不含其他消息、DTLS、重传和 FRP。

| 场景 | 基线 | 最终房主先入 | 最终副机先入 |
|---|---:|---:|---:|
| Mines_35 | 1,970,029 字节 / 1,646 份 | 17,732 字节 / 11 份 | 17,732 字节 / 11 份 |
| Ruins2_04 | 3,693,120 字节 / 2,400 份 | 0 字节 / 0 份 | 0 字节 / 0 份 |

基线平台本身的 CombatState 为 365,389 字节 / 446 份。移除同步后房内联网实体从 33 降为 12，21 个平台仍在原场景实际工作。Mines_35 整房该类载荷减少约 99.1%；本地平台独立翻转，以及其他敌人根动画的冗余削减共同贡献这个差值，不能将全部降幅只归于平台。

Ruins2_04 的 22 个联网实体仍存在。稳定待机窗口内不再生成多余 CombatState；首次快照、其他消息、根动画仍正常运行，不能把零样本误读为这个房间完全不需要联网。两端大盾守卫原生动画均实际出现至少三个不同精灵帧。根动画优化前后，权威端 CombatState 采样累计耗时约 334 / 337 ms，没有测出该端采样 CPU 明显下降；主要已证实收益是重复可靠数据和接收应用减少。

## 验证记录

| `.test-results` 运行目录 | 结果 |
|---|---|
| `platform-city-baseline-20261015-a` | 修改前构建，三房观察，双方 done / FINAL PASS、停机后严格日志通过。仅前两房用于上表；第三房 Ruins1_30 含原生 Boss / 动态生成，没有作为单一法师攻击的优化对照 |
| `platform-city-candidate-20261015-a` | **拒绝**：新增探针使用私有字段帮助器读取公开 topSpikes / bottomSpikes，双方 NullReferenceException，无 done；失败保留 |
| `platform-city-candidate-20261015-b` | 修正探针字段访问后双方 done，21 个平台实际落地翻转/尖刺切换/恢复与互不影响通过；两端根动画继续，流量降低；旧候选哈希不替代最终构建 |
| `platform-city-final-host-20261015` | 最终提交构建，两房、平台独立运行、根动画和性能观察双方 done / FINAL PASS |
| `platform-city-final-guest-20261015` | **拒绝**：客机实际落地触发检查超时，另一端屏障超时；没有捕获原生碰撞回调，不能唯一归因，无 done |
| `platform-city-final-guest-retry-20261015` | 保持相同生产 DLL，探针改用原生碰撞回调确认实际接触、原生 Flip 确认翻转，并用原生入口作为等待位置；双方 done / FINAL PASS |
| `platform-city-final-dream-combat-20261015` | 相同三个生产 DLL；双方 done / FINAL PASS，梦之钉平台的真实靠近/站立/重入/接管/再次站立通过，既有嵌套命中盒几何、旋转、透明度、材质块、不叠加副本后坐力及双方实际 HeroBox 受伤通过 |

143 项回归和 14 项严格日志检查通过，分别保留 `.test-results/platform-city-final-regression-20261015.log`、`platform-city-final-log-gate-20261015.log`。初次构建只有既有 ClientUpdateManager isActive XML 注释警告。运行终态附近的 ConnectionReset WARN 按实际保留，不称为无警告运行。

反向首轮保留为失败。原平台探针将角色放在平台正上方 25 单位等待，并要求原生 Flip 与角色 FSM 的 onGround 同时可见；该位置没有验证为无地形/尖刺。重跑等待位置改为原生入口，交互断言使用真实 Player 碰撞回调与原生 Flip，不直接调用翻转、不改变平台状态、不延长超时。后续通过不能反推旧超时的唯一原因。

探针修正另存为 `10e665f`；所有最终运行的三个生产 DLL 仍由 `65faba1` 构建。最终三组均确认双方 done、无 failed、双方 shutdown-offline 与 shutdown-server-stopped；仅停止路径核实的隔离进程后，严格日志再次通过，未发现 HKMP ERROR、模组异常、Spawn expired 或原生崩溃。两隔离副本 Smoke DLL 均重命名为 `.disabled-platform-city-20261015-final`；本轮未停止正常或未知路径的游戏进程。停机后对照三组 binaries.json 与当前测试输出 DLL，三个哈希全部一致；文档/探针提交后没有重建生产 DLL。

最终构建三个 DLL 的 SHA-256，各运行 `binaries.json` 保留原值：

| 文件 | SHA-256 |
|---|---|
| HKMP.dll | `66304660701731E7F62919998F998F7A3802C777E5872824BBE1576C34726A76` |
| HollowConnect.dll | `EB80DE030A95A7B7E9EDFE171D5F660BDED84E402055CC2049B56B565C1DC384` |
| HollowConnect.Core.dll | `D2390FD9A0AEB4CFBFD73D9D85233538049732EDF01DB5E5FB5C090BDF72F720` |

只读游戏来源为 `E:\SteamLibrary\steamapps\common\Hollow Knight`。Assembly-CSharp.dll SHA-256 `5944411BD93830369390A4B51766EE68C4AB26195B299E25A07B5E7D0E00086D`，4,064,768 字节；PlayMaker.dll SHA-256 `0EF0E7829D125E1F632C8A189260EC6C6882630BE6932C8C7AE032EFBC53469A`，212,480 字节。复用既有只读审计 `.test-results/crystal-soul-audit-20261015`，另保存本轮 FlipPlatform 原生方法 IL。用户日志在测试前备份于私密忽略目录 `.test-results/platform-city-userlogs-20261015-a`，不打包或输出姓名/地址。

## 范围限制

测试只使用 `.test-results/game-host` / `game-guest`，静音、内存角色、拦截全部三个 SaveGame 重载。本轮不关闭此前激光/法师实际攻击下的子节点快照开销、位置插值缺失、Mines_11 String 快照越界、地图持续不可见或历史接管阻塞。下一步真实双机复测需使用包含这些提交的新小版本，观察平台独立表现及同一路线的延迟/掉帧；本轮没有测得实际 FRP 链路降幅，完整发布仍未通过。

| 范围 | 本轮证据 | 尚未验证 |
|---|---|---|
| 水晶翻转平台 | 21 个均不登记/不复制；代表平台双方轮流真实碰撞、独立翻转和自然恢复，两种先入顺序通过 | 每个房间/每个平台逐个踩踏、真实双机延迟体验 |
| 泪水之城 | Ruins2_04 待机流量及双端原生根动画、22 个实体保留 | 全部城内房间、持续战斗、实际 FRP/渲染帧率改善 |
| 公共状态安全性 | 嵌套激活/几何/颜色/伤害、双方实际受伤及梦境平台离房接管通过 | 所有 Boss/弹幕、任意飞行中接管和完整发布矩阵 |
