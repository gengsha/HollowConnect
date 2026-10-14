# 2026-10-14 今日筛查记录

本次筛查使用本地重新构建的 0.1.13 测试 DLL。由于文档提交后重新构建会改变 ProductVersion 和 SHA-256，本次结果不能直接替代昨天 `6ddf5b3` 构建的 12 组发布证据；试玩包也没有重打。今天构建的三个 DLL 哈希为：

- `HKMP.dll`: `B94D1E1D354556F6A0EB2602104C5CE85994F38086AC32D28344458F9D905F5F`
- `HollowConnect.dll`: `8EBA1D0FD8ABC893CE47D181CFAE757DBBF291A44291DD9449DAF92DF47044E6`
- `HollowConnect.Core.dll`: `6C29BB9B5F28698A54F8C8C477EB953777A30E552134E318F05EF6264698C2C0`

## 通过项目

- 109 项核心回归测试。
- 14 项严格日志门槛测试。
- 13 项便携安装器测试。
- 房主先入、客机先入的两轮原生一门入口和连战。
- 假骑士迟入场、阶段移交、下一阶段 AI、血量和迟返客户端。
- 普通格林房主先入、客机先入，火球和火蝠移动副本。
- 救援、普通死亡、完整协作战斗、传送和菜单重开房。
- 镜头对照：非危险冻结被拒绝，持续危险冻结约 15 秒后被拒绝，真实尖刺危险恢复在双方通过。

对应运行目录位于 `.test-results/today-*20261014`；镜头汇总为 `.test-results/today-camera-gate-20261014.json`。

## 复现的失败

生命周期压力组在 `GG_Hornet_1` 第 3 次投针断线重连时失败，房主和客机分别记录了：

```text
Timeout: native Throw Recover after reconnect 2 state=Thrown
Timeout: authority throw and recovery after reconnect 2
```

失败前，Boss 的 `Control` FSM 仍为 `Thrown`，Needle 的 `Control` FSM 为 `Notify`，Needle 继续向场外移动（日志中位置从约 `226` 增至 `950`），没有自然进入 `Throw Recover`。`Needle` 与 `Needle Tink` 引用都能重新绑定，静态实体 ID 也已收敛，因此当前证据只说明“重连后的原生投针恢复链仍可能断掉”，还不能把原因归结为单纯的空引用或 ID 分叉。

失败日志：

- `.test-results/today-lifecycle-host-20261014/host.log`
- `.test-results/today-lifecycle-guest-20261014/guest.log`

这项失败暂时阻止正式发布，也不能用其它通过的 Boss 或重跑结果覆盖。下一步应沿着第三轮重连时 `Hornet Control=Thrown`、`Needle Control=Notify` 的原生 FSM/回收回调继续定位。

## 边界

本次仍是同机隔离双客户端和本机 UDP。真实两台电脑樱花 FRP、完整四门所有 Boss 招式、全部梦境变体、普通流程关键奖励和飞行中弹幕接管仍未完成验收。
