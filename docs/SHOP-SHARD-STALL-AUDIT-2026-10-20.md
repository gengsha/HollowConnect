# 斯莱商店重复容器碎片卡住：版本对照（2026-10-20）

结论：已交付的 0.1.27 与当前工作区修复版均可复现，最近的远端碎片直接结算没有覆盖这个本地重复领取分支。本次仅调查和增加专项，生产源码及生产 DLL 哈希保持检查前状态，没有升版、打包或安装。

后续用户已要求修复，处理与新哈希验证见[碎片界面收尾修复](SHOP-SHARD-STALL-FIX-2026-10-20.md)。本报告保留调查当时的产物和失败结论，不用于描述修复后工作区。

## 已确认的触发与原因

1. 客机已有 2 个容器碎片，房主先在斯莱商店购买 `vessel:slyVesselFrag1`。
2. 客机同步得到该来源，形成 33 额外灵魂容量，零散容器碎片归零。
3. 客机在同一场景打开商店，购买本地仍可见的同一来源。
4. 来源去重正确跳过重复扣费、碎片增加和合成；本地原版购买仍创建 `Vessel Fragment Instant` 和 `Vessel Fragment UI`。
5. UI 的 `Fleur` 只通过 `IntSwitch [1,2,3]` 前往 `Get One/Two/Three`，实际值为 0，没有匹配转移，无法到达释放控制的 `Destroy Self`。

当前版失败时实测 `acceptingInput=false`、`controlReqlinquished=true`、`disablePause=true`、`inFlight=1`。商店列表已经关闭，但碎片界面停在 `Fleur`，与反馈中的购物装饰界面和无法移动相符。原版与当前版的 FSM 跟踪均记录到计数 0；专项另导出原生状态和动作，确认没有零计数出口。

最近的直接结算删除的是**远端** `StartNativeRemote`/碎片收集 UI，避免远端奖励播放动画；本次失败的第二个 UI 来自**本地商店重复购买**，仍经过 `CreateReward`、`BeginNativeFlow` 和 `SetInt` 的原有路径。左下角资源通知与五门修复没有改变这个分支。

## 对照范围

使用现有隔离游戏副本、禁止保存的内存角色和 localhost UDP。双方通过真实斯莱库存打开确认框，使用原版确认、支付、发奖、动画和退出流程；测试只设置初始余额/碎片并选择物品。初始碎片来自夹具，不声称实际游玩了这些前置来源。

| 产物与运行前缀 | 场景 | 结果 |
|---|---|---|
| 当前版 `shop-shards-current-20261020-b` | `queued-mask` / `queued-vessel`：双方先开确认框，客机收到待结算来源再确认 | 双端通过，严格日志通过 |
| 同上 | `settled-mask`：客机共享合成后再购买同来源面具碎片 | 双端通过，严格日志通过 |
| 同上 | `settled-vessel`：客机共享合成后再购买同来源容器碎片 | 客机控制无法恢复，复现失败保留 |
| 同上 | `simultaneous-mask` / `simultaneous-vessel`：双方同时确认 | 双端通过，严格日志通过 |
| 原版 `shop-shards-release-027-20261020-c` | `settled-vessel` | 同一原版零计数 UI 分支，复现失败保留 |
| 同上 | `queued-vessel` | 双端通过，严格日志通过 |
| 当前版 `shop-shards-current-20261020-c` | `settled-vessel` 追加原生 UI 动作导出 | 再次复现，确认 `Fleur` 的零计数没有转移 |

共 9 组双端：6 组通过、3 组确认卡住（原版 1 组、当前版 2 组）。卡住运行保留失败标记和严格日志拒绝结果，没有计入通过。全部运行结束后隔离游戏已停止，双方 Smoke 插件已改名停用。

`shop-shards-current-20261020-a-queued-vessel` 在首次商店介绍对话处准备失败：夹具缺少 `metSlyShop` 标记，还未执行购买。补齐夹具后新开 `-b`，保留原失败，不作为产品问题或通过结果。之后仅为记录 UI 的实际 `IntSwitch` 增加测试导出，重新构建 Smoke；生产 DLL 未变。每轮探针哈希独立记录，不能用新探针哈希追认旧运行。

没有运行全部 Boss/Unity 矩阵或双机 FRP，没有验证所有其他碎片数量。面具专项通过不证明容器分支安全；同源并发通过也不能替代已结算后本地重复购买的检查。本次没有验证重新进房恢复，截图中的该现象仍属于用户反馈。

原版 ZIP：`dist/HollowConnect-0.1.27-windows.zip`，SHA-256 `05F97A2D7762C5276E87E508D18E6A509A291AB8FB329A9B2EA518A18EFE89FC`。精确生产哈希、各运行路径、探针哈希和失败状态见[机器可读证据](evidence/shop-shard-stall-audit-2026-10-20.json)。本次源码对照基线保存在 `.analysis/shop-shards-audit-20261020`，不覆盖之前的五门修复。
