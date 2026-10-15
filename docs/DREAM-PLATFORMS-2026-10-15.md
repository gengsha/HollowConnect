# 梦之钉剧情梦境：靠近平台不浮现

2026-10-15。生产修复提交 `a2b82429f742e819b4ce43866c3a33b55607815b`。用户截图和原始日志指向 `Dream_Nailcollection`，当时本机为非场景权威。此轮只修改源码并使用隔离副本验证，未升版本、打包或安装日常游戏。

## 根因与修复

原生 `DreamPlatform.Start` 为内圈进入绑定 Show、外圈离开绑定 Hide；`visible` 防止重复播放原生显隐动画。旧 `DreamPlatformComponent` 抑制原生绑定，由各端自行加减人数并发送可靠实体消息；服务器 `OnReliableEntityUpdate` 又拒绝所有非场景权威的消息。结果是副机本地确实触发，却不能改变权威平台，后续权威的 CombatState 继续覆盖副本表现。

旧版失败基线 `.test-results/dream-platform-baseline-20261015-a` 保留：副机 `triggered=1, visible=True`，房主原生平台仍 `visible=False`，双方无法完成“双方显示”屏障。这是实际双端状态分歧，不是仅由静态代码推断。

修复位于三个生产文件：

- `DreamPlatformComponent`：只接受当前显示对象上由本地角色触发的内圈进入/外圈离开，报告自身是否在范围内；从服务器的绝对人数调用原生 Show/Hide。首次状态/入场快照补交已发生的本地进入，接管时将已有状态应用到原生本体。
- `ServerEntityData`：权威注册平台后保留玩家 ID 集合，重复进入/离开幂等，不信任客户端提供的总人数。
- `ServerManager`：在已有场景、visit、epoch 校验内，仅允许副机向已注册平台提交范围请求；仍不接受其注册或发布状态，也不放开 Active、FSM、CombatState 等权威数据。向全场景（包括发送者）发布绝对人数，并缓存为入场/接管快照。离房/断线时清理该玩家，再构造接管快照。

沿用原生内外圈的迟滞行为，不增加全场轮询、定时重试或剧情 FSM 重置。`showOnEnable` 平台保留原生流程。一般实体消息沿用原列表，只有包含平台请求时才创建过滤列表，纯平台请求不再通用转发。

## 验证与失败记录

复用现有 `coverage` 双端探针，配置见源码包中的 `tests/Smoke/README.md`。测试目标为真实场景的 `dream_plat_small`，38 个实体，入口 `door_dreamReturn`。靠近由真实 Unity 触发器驱动，没有调用平台 Show/Hide 或设置平台 FSM；实际站立要求角色自然下落、onGround 为真且脚底接近平台碰撞顶面。

| 运行目录（均在 `.test-results`） | 结果与范围 |
|---|---|
| `dream-platform-baseline-20261015-a` | **失败基线**：旧实现副机触发后权威平台保持隐藏；双方 failed 保留 |
| `dream-platform-candidate-20261015-a` | 第一候选双方 done，靠近/站立/一人离开/最后离开通过；不是最终源码哈希 |
| `dream-platform-candidate-20261015-b` | **拒绝**：扩展换房/交接期间角色意外离开梦境，错误诊断又访问了已销毁对象；无 done |
| `dream-platform-candidate-20261015-c` | **拒绝**：补充追踪确认原生入场还在移动角色并恢复重力，探针提前传送/冻结被覆盖，随后角色坠落触发原生梦境重新加载；无 done |
| `dream-platform-candidate-20261015-d` | 等待原生输入、transition 和房间快照就绪后，扩展换房/接管专项双方 done；不是最终提交构建 |
| `dream-platform-final-host-20261015` | 最终提交构建，房主先入；双方 done / FINAL PASS，初次副机与接管后的原生平台均实际可站立 |
| `dream-platform-final-guest-20261015` | 相同最终三个 DLL，副机先入；双方 done / FINAL PASS，同样完成初次副本及接管本体的实际站立 |

候选 b/c 不计通过，未放宽断言或超时。入口就绪条件复用既有购买/救援探针使用的原生状态；近距离靶子位于触发圈内、实体平台上方，避免把角色直接放进固体碰撞体。

最终专项覆盖：两端人数 0/1/2 与显隐一致、非权威/权威分别先靠近、重复进入/离开、伪造总人数、非权威 Register/State 拒绝、一人离开仍显示、最后离开隐藏、有人在圈内时另一人换房重入、权威离房后接管及旧权威重入、接管后实际站立和再次隐藏。离房阶段在测试插件中抑制目标平台 exit 回调，验证服务器自己清理成员。

最终构建的 143 项回归及 14 项严格日志检查通过，日志分别为 `.test-results/dream-platform-final-regression-20261015.log` 和 `dream-platform-final-log-gate-20261015.log`。初次干净构建只有既有 `ClientUpdateManager.AddEntityHostFsmData` 的 isActive XML 注释警告，没有新增编译警告。两组终态附近有 ConnectionReset WARN；没有将其称为零警告运行。

两组最终运行都已确认双方 done、无 failed、双方 shutdown-offline 和 shutdown-server-stopped；仅停止路径核实的隔离进程后再次通过 `Assert-ProbeLogs.ps1 -RequireRuntimeFiles`，没有 HKMP ERROR、模组异常、Spawn expired 或原生崩溃。两隔离目录的 Smoke DLL 均改名为 `.disabled-dream-platform-20261015-final`。日常游戏 PID 20476 保持运行。停机后对照两组 binaries.json 与当前测试输出的三个 DLL，哈希全部一致；文档收尾后不再构建。

最终三个生产 DLL 的 SHA-256（各运行目录 `binaries.json`）：

| 文件 | SHA-256 |
|---|---|
| HKMP.dll | `D240741A2335F9473A67A31743DD7BFC75E9809A6C4FED7DDB174013892CEBA2` |
| HollowConnect.dll | `EBBE7B562053B854B01D25C21C979C6BF8FE67E7668372FC1F0720F5CDCCD85E` |
| HollowConnect.Core.dll | `6571D27B68629D0FCA7000E198A80890E9A64E3A8B37C288CE14319DE19FAC71` |

游戏只读来源为 `E:\SteamLibrary\steamapps\common\Hollow Knight`。`Assembly-CSharp.dll` 4,064,768 字节，SHA-256 `5944411BD93830369390A4B51766EE68C4AB26195B299E25A07B5E7D0E00086D`；`PlayMaker.dll` 212,480 字节，SHA-256 `0EF0E7829D125E1F632C8A189260EC6C6882630BE6932C8C7AE032EFBC53469A`。既有技能只读审计产物位于 `.test-results/crystal-soul-audit-20261015`；本轮另核对原生 DreamPlatform/TriggerEnterEvent 方法及 BuildSettings 场景条目。原始用户日志预先保存在私密忽略目录 `.test-results/dream-platform-userlogs-20261015-a`，不纳入交付。

## 覆盖边界

| 场景/对象 | 靠近/离开 | 碰撞站立 | 重入 | 权威交接 | 完整剧情奖励/真实双机 FRP |
|---|---|---|---|---|---|
| Dream_Nailcollection / dream_plat_small | 最终两种先入顺序通过 | 两组初次副本、接管本体均通过 | 两种顺序通过 | 两种顺序离房接管通过 | 未验收 |
| 同场其他梦境平台 / 其他梦境 | 共用同一组件；未逐个运行断言 | 未逐个验证 | 未逐个验证 | 未逐个验证 | 未验收 |

这是同机 UDP、批处理运行；所有测试仅在 `.test-results/game-host` / `game-guest`，静音、内存角色并拦截全部三个 SaveGame 重载。断网/进程崩溃与高延迟下完整剧情未专门测试；本轮不能关闭地图互相不可见、激光/弹幕卡顿或历史接管阻塞，也不授予完整发布通过。
