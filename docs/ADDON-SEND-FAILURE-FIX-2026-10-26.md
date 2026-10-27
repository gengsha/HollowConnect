# Addon 发送错误分类与收件人隔离修复

2026-10-26。关闭 A1-01：非断线 `InvalidOperationException` 不再静默转成 false；一个收件人发送失败不再跳过其他结算或伤害榜收件人。

## 问题与处理

原发送器用 `InvalidOperationException` 同时表示服务器停止、收件人离线、无效 channel、addon 未取得 ID、集合类型冲突。上层全部捕获为断线，无法知道在线收件人的真实失败。只改为重新抛出也不完整：待结算条目已经移除，会跳过剩余收件人。

- 在发送器中，用内部 `AddonConnectionUnavailableException` 明确标识服务器停止或收件人退役；它仍继承 `InvalidOperationException`，保留现有调用者的异常兼容性。未修改消息格式、网络协议或方法签名。
- `CoopServerAddon.Send` 只把明确的连接不可用视为正常跳过。其他异常保持可见，包括“配置错误发生时连接恰好也退役”的情况。
- 结算和伤害榜经 `Deliver` 逐收件人处理，结果分为成功入队、连接退役和失败。失败记录消息类型、收件人、serial、aux、场景和原异常，其他收件人继续。
- 当前入队 API 的非连接错误属于无效参数／配置／集合用法，按当前消息的明确失败终态处理，不自动循环重试。结算条目已经移除，失败日志保留其身份。
- 伤害榜另记失败 revision，不把它写成成功发送，也不每 150ms 重试同一失败版本。下一 revision 可以重新发送；服务器重置和历史裁剪会清理失败记录。正常断线窗口原有的 Leave 后重试保持有效。

主要代码：`src/HKMP/Api/Server/Networking/AddonConnectionUnavailableException.cs`、`ServerAddonNetworkSender.cs`、`src/HollowConnect/CoopServerAddon.cs`、`DamageRankingServer.cs`。

## 验证

**31 项相关核心检查通过，0 失败。** 包含此前失败的非断线错误可见性检查，以及：

- 真实发送器区分离线、服务器停止、无效 channel、缺失 addon ID 和集合类型冲突；
- 非断线错误与连接退役同时发生时仍然可见；
- 一个收件人失败后，后面的收件人继续入队；失败日志保留结算身份，条目不持续重试；
- 单个结算异常不阻断 roster、wipe、release；
- 伤害榜失败版本不会被记为成功或反复重试，配置修正后的新版本能够恢复，重置清除失败记录；
- 原结算资格、幂等、伤害榜和可靠发送相关检查。

在冻结生产 DLL 上，复跑了与前次验证相同的原生 Gruz Mother 发送窗口探针：

| 用例 | 共享进度 | 结果 |
|---|---|---|
| `send-failure-fix-20261026-settlement-two-a` | 关闭 | 双端完成，严格日志通过，约 39 秒 |
| `send-failure-fix-20261026-settlement-three-a` | 开启 | 三端完成，严格日志通过，约 46 秒 |
| `send-failure-fix-20261026-damage-three-a` | 开启 | 三端完成，严格日志通过，约 46 秒 |

实机在 Connected 检查后、真实发送器再次查找收件人之前，通过真实服务端断开流程退役 guest，确认 transport 已消失但 addon Leave 仍未处理。存活端收到实际结算消息，继续 roster、救援请求和状态流、原生全灭返回及 Release；伤害榜最终 revision 收敛。三端救援验证的是实际请求／状态流，物理骨钉碰撞仍属于已有独立专项。

生产 SHA256：

| 文件 | SHA256 |
|---|---|
| HKMP.dll | `CB1BB446EE3A2013DC97536EC8AD3DCFB2019391A6A8327A04355F61234DE21D` |
| HollowConnect.dll | `7E946F793D4623CE58A48F2874A24D2DFC03B1CE8DA431E7BF6789D66AF074DB` |
| HollowConnect.Core.dll | `746C12EF77F5097A57B7416CCA5BE728BE33F3163FEAF527A2804AA6852426F6` |

证据：`.analysis/send-failure-fix-20261026/evidence.json`、`core.log`、各组状态及严格日志；实机原始记录在对应 `.test-results/send-failure-fix-*`。此前的失败记录完整保留于 `.analysis/a1-validation-20261026/negative-final.log`，本次同名核心用例已通过。

本次是针对回归，未运行完整发行矩阵，未打包或更改个人安装。隔离进程已结束，Smoke 插件已从隔离副本启用路径移除。本地构建位于 `src/HollowConnect/bin/Release/net472/`。
