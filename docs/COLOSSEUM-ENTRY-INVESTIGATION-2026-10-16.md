# 竞技场正常报名与个人复活点调查

后续已实施竞技场个人复活点修正，见[修正与验收](COLOSSEUM-CHECKPOINT-FSM-BOUNDS-2026-10-16.md)。下文保留修正前的调查与失败证据，不能当作修正后的验收。

原始调查：2026-10-16；当时仅测试，生产代码与 DLL 未变，未升版、打包或安装。

## 已确认

铜级试炼通过报名牌确认、原生右侧入口进入，也会出现复活点差异。双方从不同个人长椅开始，经过休息区但没有坐该区长椅。角色在报名牌附近与门前由探针定位；向上交互经输入监听器处理，确认使用原生 UI 选择事件，走门时注入横向移动意图，碰撞、TransitionPoint 与试炼场景加载均由原版执行。没有直接跳进试炼、改写入场后的复活点或执行修复补丁。

完整运行 `colosseum-normal-bronze-host-final-20261016`：房主先入，实际取得场景权威，入场后记录 `Room_Colosseum_02/RestBench`；后入副机仍为 `Crossroads_47/RestBench`。双方各自正式死亡并放弃救援，实际分别回竞技场休息区与十字路旧长椅。两端原生死亡处罚各一次，个人暗影位置均为原版竞技场休息区，长椅起身后可操作。双方正常断网、服务器停机，停止隔离进程后严格日志与生产 DLL 哈希核对通过。这里的完成标记表示成功完成调查并复现问题，不表示 bug 已修复。

交换先入者的 `colosseum-normal-bronze-guest-final-20261016` 已观察到相反方向：先入副机取得场景权威，复活点改为竞技场休息区；后入房主仍保留 Town。该组中断，未完成死亡返回及正常网络退出，不计完整通过。原始观察支持问题跟随场景权威，而非固定跟随房主/副机；反向完整验收仍需补做。

## 原生设置来源

运行时状态追踪和动作表确认：写入来自 `Colosseum Manager` 上的 **`Manager / Set Respawn`**，不在 `Battle Control` 的入场动作内。该状态设置 `respawnMarkerName=RestBench`、`respawnScene=Room_Colosseum_02`、`respawnType=1`、`respawnFacingRight=false`，随后还有原生 SendMessage 动作。

联网实体登记管理整个对象；非权威使用副本，其未登记为同步 FSM 的 Manager 被停用，原生对象也未激活。因此本地入场设置不会自然执行。修复候选应限定为每位入场者自己的原生 checkpoint 初始化；不能直接启用整套 Manager，它还承担竞技场进程、标题与奖励事件。正式死亡正确读取已有本机记录，缺失发生在入场阶段。

## 运行记录与边界

- `colosseum-normal-inspect-20261016`：双端休息区与报名厅动作表读取完成、正常退出，严格日志通过；没有进入试炼。
- `colosseum-normal-bronze-host-20261016`：报名正确，横向输入 getter 模拟未推动角色，走门超时；探针刺激无效，保留失败。
- `colosseum-normal-bronze-host-r2-20261016`：测试反射遗漏公开字段标志，输入注入报错；保留失败，非生产故障。
- `colosseum-normal-bronze-host-r3-20261016`：已通过原生门进入，动作表诊断访问已销毁的 Unity 引用失败；保留失败，非生产故障。
- `colosseum-normal-bronze-host-final-20261016`：完整复现与双端实际个人死亡返回，日志/哈希通过。
- `colosseum-normal-bronze-guest-final-20261016`：有反向复活点观察，整组中断，不能计完整通过。

银级、金级、迟入、关闭共享进度、断开联机的原生对照、休息区实际坐椅对照均未完成。测试使用已解锁试炼的内存角色，不包含首次付费解锁、逐波通关、胜利奖励或真实双机 FRP。没有修改或读取个人存档；三个 SaveGame 重载均拦截，隔离进程静音，测试插件已停用。

后续入口：`tests/Smoke/ColosseumProbe.cs`、`Run-Colosseum.ps1`、`Complete-Colosseum.ps1`。仅构建测试插件时使用 `-p:BuildProjectReferences=false`；每次选择新运行名，保留失败目录。尚未执行生产修正，不能沿用现有复现记录宣称修复通过。
