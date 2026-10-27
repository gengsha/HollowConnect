# 全量双端检测记录（2026-10-17）

> 本文保留首轮全量检测的历史失败。后续修复、复测与性能数据见 [双端卡死、共享奖励与流畅性修复记录](STABILITY-FIX-VERIFICATION-2026-10-17.md)。

最新最终构建的 18 组双端记录、200 项核心回归和性能窗口已由 [稳定性汇总](../.test-results/stability-final-template-20261017-h-summary.json) 重扫；历史失败和未确认阻塞仍保留在本文及运行台账中。

本轮使用两个隔离测试副本、独立 `Config.ini`、唯一运行目录和 `HOLLOWCONNECT_SMOKE_ROOT`。没有读取或修改用户真实存档。游戏进程已在收尾时停止。

## 安装与静态审计

- 游戏目录：`E:\SteamLibrary\steamapps\common\Hollow Knight`
- `Assembly-CSharp.dll`：4,064,768 bytes，SHA-256 `5944411BD93830369390A4B51766EE68C4AB26195B299E25A07B5E7D0E00086D`
- `PlayMaker.dll`：212,480 bytes，SHA-256 `0EF0E7829D125E1F632C8A189260EC6C6882630BE6932C8C7AE032EFBC53469A`
- `hollow_knight.exe`：653,824 bytes，SHA-256 `4D3D0F55AFBB249170241D0DC6FAF9A5DCF3E1ED427A282AE990BB6E4E3A6913`
- 只读 Boss 审计：[`audit-report.md`](../.test-results/full-dual-20261017-boss-audit/audit-report.md)，机器结果：[`audit.json`](../.test-results/full-dual-20261017-boss-audit/audit.json)。扫描 3,578 个类型、1,855 个数据文件、2,553 个方法和 4,235 个调用关系。

静态扫描确认 Boss 血量/死亡、PlayMaker FSM、动态实例/对象池、Boss 场景转场和梦境入口都存在高风险原生路径；静态名称本身不等于运行时缺陷，以下实机结果优先。

## 全量房间套件

| Case | 结果 | 证据目录 |
|---|---|---|
| `native-host-013` | **失败**：Guest 的 `ReliableEntityUpdate` 触发 `CombatStateComponent.Parts.Refresh` 空对象 `NullReferenceException` | `.test-results/full-dual-20261017-final-native-host-013` |
| `native-guest-013` | **失败**：同一 NRE，反向权威时 Host 重复触发 | `.test-results/full-dual-20261017-final-native-guest-013` |
| `lifecycle-host-013` | 通过，FSM 续接、断线重连、6 轮权威/飞针回收 | `.test-results/full-dual-20261017-final-lifecycle-host-013` |
| `lifecycle-guest-013` | 通过，反向权威、FSM 续接、6 轮权威/飞针回收 | `.test-results/full-dual-20261017-final-lifecycle-guest-013` |
| `boss-013` | 通过 | `.test-results/full-dual-20261017-final-boss-013` |
| `boss-late-013` | 通过，晚加入/延迟权威 | `.test-results/full-dual-20261017-final-boss-late-013` |
| `grimm-host-013` | 通过，Grimm 动态弹幕/火蝠，Host 权威 | `.test-results/full-dual-20261017-final-grimm-host-013` |
| `grimm-guest-013` | 通过，Guest 权威 | `.test-results/full-dual-20261017-final-grimm-guest-013` |
| `rescue-013` | 通过，原生暗影救援 | `.test-results/full-dual-20261017-final-rescue-013` |
| `death-story-013` | 通过，死亡/暗影/返回流程 | `.test-results/full-dual-20261017-final-death-story-013` |
| `full-013` | **失败**：Guest 等待房主实时地图坐标超时；Host 等待 Guest 自愿回个人长椅超时 | `.test-results/full-dual-20261017-final-full-013` |
| `join-013` | 通过，加入、断开、退菜单、重新加入 | `.test-results/full-dual-20261017-final-join-013` |

### 关键失败 1：CombatState 空 Unity 对象

两种权威顺序都出现：

```text
ENGINE ERROR ... ReliableEntityUpdate
System.NullReferenceException
  at UnityEngine.Component.GetComponents[T](List[T] results)
  at Hkmp.Game.Client.Entity.Component.CombatStateComponent+Parts.Refresh()
  at ...CombatStateComponent.Build()
  at ...CombatStateComponent.Update()
```

当前实现的 [`CombatStateComponent.cs`](../src/HKMP/Game/Client/Entity/Component/CombatStateComponent.cs) 在 `Parts.Refresh()` 直接对 `Transform` 调用 `GetComponents`。原生对象销毁/脱离层级后仍可进入构建列表，Unity 假对象引用会在这里抛异常。该异常发生在可靠实体包处理线程，后续同步更新可能被跳过，属于 **P0 死房/状态冻结风险**，必须修复后重新跑两种 native 权威。

### 关键失败 2：完整流程地图/个人长椅

Host 已通过地图图标和投影断言；Guest 没有收到可用的房主实时地图坐标。随后 Guest 没有完成 F6 个人长椅返回，Host 在 45 秒后超时。该项属于 **P1 双端流程阻断**，需要检查地图坐标发布的接收方向及个人返回请求/确认的目标玩家状态。

## 资源与进度专项

### `resources`

目录：`.test-results/full-dual-20261017-resources`

- 通过：完整 SaveData 重放不会注入未收到的资源；世界苍白石真实碰撞领取；同一 source ID 同时领取和重复 offer 不重复；本地原生商店确认与房主扣款一次。
- **失败**：房主在 Salubra 原生确认界面领取护符槽时，Guest 收到共享 offer 后停在 `UI List/Confirm Control=Listen`，`input=False`、`controlReqlinquished=True`、`pause=True`，45 秒超时。Host 完成原生购买；Guest 没有恢复流程。

### `resources-after-shop`

目录：`.test-results/full-dual-20261017-resources-after-shop`

- 通过：原生碎片连续领取、3→4 面具转换在房主侧、UI 控制恢复。
- **失败**：Host 的转换结果为 `heartPieces=0,maxHealthBase=6,claimed=True`；Guest 仍为 `heartPieces=3,maxHealthBase=5,claimed=False`，10 秒 source/counter 等待超时。说明“房主存档进度同步到后来者”在转换型资源上仍未闭合。

## 竞技场专项

- `colosseum-lifecycle`：通过。
- `colosseum-wipe`：通过。
- `colosseum-late`：通过。
- `colosseum-reward`：**失败**。目录：`.test-results/full-dual-20261017-colosseum-reward`。Guest 的一次原生护符拾取和一次 slot ledger 记录通过，但 Host 等待“迟入者收到首次完成的唯一护符槽”超时。属于 **P1 奖励共享/迟加入边界问题**。

## 最终 Boss 结算

- `final-ending-battle`：通过，独立尸体、原生音乐、自我刺击和 Focus ending A/B。
- `final-ending-continuation`：通过，显式尸体 continuation 与 Focus ending A/B。

## 工程回归

- `dotnet build tests/HollowConnect.Tests.csproj --no-restore`：通过。
- `dotnet run --project tests/HollowConnect.Tests.csproj --no-build`：**196 passed, 0 failed**。
- `dotnet build tests/Smoke/HollowConnect.Smoke.csproj --no-restore`：通过。
- `git diff --check`：通过。

## 本轮未直接覆盖

以下项目没有把“未运行”当作通过：

- 用户截图所述的“粪便 Boss 下砸进入隐藏梦境房间后卡死/有声音但不能传送”的精确原生房间和触发顺序；本轮只覆盖了 Pantheon/Boss/梦境相关通用路径。
- 同一精确房间内“房主从椅子下来后传送队友，房主永久卡死，重进/单人档仍卡”的旧存档复现；该状态需要用户存档或可复现步骤才能做破坏性前置验证，本轮没有读取真实存档。
- Colosseum 的 `entry`、`return`、`native`、`shareoff`、`bounds`、Silver/Gold 全档位；本轮已覆盖生命周期、wipe、late 和 reward continuation。
- 真实两台物理机器/FRP 链路；本轮是两个隔离客户端在同机 UDP 29650，不能替代跨机网络验收。

本轮结论：核心纯逻辑回归全绿，但当前发行构建不能判定为双端安全。至少需要修复 CombatState 空对象 NRE、完整流程地图/个人返回、资源确认界面并发、资源转换同步和竞技场迟加入奖励后，再重新跑失败项及用户所述隐藏梦境房间的专门探针。
