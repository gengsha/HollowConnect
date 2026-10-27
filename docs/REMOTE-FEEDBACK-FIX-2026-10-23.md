# 远端动作的手柄振动与挥刀后坐力隔离

本次只处理两个反馈：队友动作触发本机手柄振动；队友挥刀碰撞导致本机出现后坐力。

- 新增 `RemotePlayerFeedback`，在远端特效的原生振动组件和 PlayMaker 振动动作入口拦截反馈，同时防止远端 StopVibration 停掉本机振动。
- 远端预制体实例化及网络动画播放使用可嵌套、异常后自动退出的创建作用域，覆盖 `Instantiate` / 对象池激活时、远端标记尚未挂载的回调。
- 拦截远端 `NailSlash.OnTriggerEnter2D`。`SlashBase` 的原生组件采用延迟销毁，在销毁完成前也不能调用本机 HeroController 的后坐力；独立的 `damages_enemy` FSM 继续负责远端伤害。
- 所有钩子沿现有连接／断开生命周期注册和注销，本机原生动作继续执行。

## 有限验证

遵照用户时间限制，不运行房间或 Boss 矩阵，不打包、不安装。

增量生产构建通过。随后使用现有 `game-host` / `game-guest` 隔离副本完成一组定向双端回调检查：

1. 原生本机振动组件仍调用振动入口。
2. 活跃预制体克隆、远端重新启用及显式播放均不调用本机振动入口；随后本机振动正常。
3. 同帧远端原生挥刀碰撞不调用本机 Recoil，本机原生挥刀仍调用 Recoil。
4. 远端墙面 Tink 回调不调用本机 Recoil，本机对应回调保持原生后坐力。
5. 远端普通挥刀的原生 NailSlash 组件正常销毁。

两端完成标记、严格日志及生产 DLL 哈希核对通过。振动检查拦截实际原生振动入口计数，未连接实体手柄测量马达。普通敌人／Boss 伤害和 PlayMaker 循环振动未增加专项实机覆盖。

最终结果目录：`.test-results/remote-feedback-20261023-b/`。先前只检查振动组件及预制体结构的开发证据保存在 `remote-feedback-20261023-a/`，未作为完整后坐力检查。

本次哈希与范围：`.analysis/remote-feedback-20261023/evidence.json`；有限等待、日志、限定路径进程清理脚本：`.analysis/remote-feedback-20261023/Run-Feedback.ps1`。

本地构建：`src/HollowConnect/bin/Release/net472/HKMP.dll`。
