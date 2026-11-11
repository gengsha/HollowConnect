# HollowConnect Frame Trace

独立诊断插件，用于调查 0.2.7 游玩时的偶发长帧。它不修复卡顿。与 Smoke 分开构建，无生产项目引用，不包含输入模拟、存档拦截或联机规则修改。

## 交给测试者

1. 退出游戏，在游戏的 `hollow_knight_Data/Managed/Mods/` 下新建 `HollowConnect.FrameTrace` 文件夹，将 `HollowConnect.FrameTrace.dll` 放进去。保留原来的 HollowConnect 文件。
2. 按原来的方式联机，开启原来使用的共享进度。先由房主录制；客机也可以独立安装并录制。
3. 进入能正常操作的场景，按 **F2**。等待屏幕底部显示 `Recording`（约两秒），照常走路、跳跃、冲刺。感到卡顿后按 **F3** 标记；可标记多次。
4. 按 **F2** 停止并保存，或等待自动结束（最长 120 秒，极高帧率时可能提前到达记录上限）。等到显示 `Saved` 后再退出。
5. 在 Windows 资源管理器地址栏输入 `%USERPROFILE%\AppData\LocalLow\Team Cherry\Hollow Knight\HollowConnect-FrameTrace`，找到最新的 `capture-...` 文件夹。把整个文件夹交回，里面应有 `complete.txt`、`manifest.json`、`summary.json`、`frames.csv.gz`。不必解压或打开帧数据。
6. 能做对照时，在 **0.2.6** 使用同一电脑、同一画面设置、同一地图、同样的共享进度与联机方式再录一次。不同版本玩家需要分别开对应版本的房间。记录每个文件夹对应的版本、房主/客机、联机方式和体感。

两次录制之间可以继续按 F2 开始新录制。准备阶段再次按 F2 会取消，本次不生成日志。若显示 `failed`，重启游戏后再试，并保留 ModLog 中的 Frame trace 错误。

卸载：退出游戏后，移除 `Mods/HollowConnect.FrameTrace` 文件夹。已导出的诊断记录保留在上述输出目录。

插件不主动改写存档、共享规则、帧率限制或垂直同步；游戏本身的正常存档行为照常运行。它会增加计时、固定内存缓冲和状态显示的开销，因此两版本对照都应安装同一个诊断 DLL。记录的是帧时间和部分同步调用的耗时，不能单靠一次记录认定根因。

## 开发与解读

构建：`scripts/Build.ps1 -Target FrameTrace`。输出在 `tests/FrameTrace/bin/Release/net472/HollowConnect.FrameTrace.dll`。仅交付这一份 DLL，依赖由已安装的 Modding API 提供。

记录在内存中进行，停止后后台写文件和计算 DLL SHA256。清单包含 CPU/GPU/系统、画面设置、开始录制时的共享进度/房主状态、可用计时点、场景名和实际加载 DLL 的哈希；不采集用户名、IP 或存档内容。网络/实体数量及连接标志每秒更新一次；队列长度为近似快照。

`unity_previous_ms` 是 Unity 的上一帧时长，`late_interval_ms` 是连续两次 LateUpdate 之间的墙钟时间。当前采样到的方法耗时和 Unity delta 的边界不同，需要结合相邻帧分析。标记 flags：1=空中，2=冲刺，4=手动标记，8=切场，16=最近一次采样已联机。

方法耗时含嵌套调用，不能相加当成总 CPU 时间。后台发送耗时单独标记，不属于主线程 CPU。`lock-wait:` 包含 Monitor.Enter 和计时开销，可帮助区分等待与调用内的其它工作，但不能直接当作无误差的纯锁等待。没有可用的逐线程分配字节计数，不把缺失计数解释为零分配。

`previous_observer_ms` / `observerMeanMs` / `observerMaxMs` 仅计量采样器自身 LateUpdate 的工作，**不包含所有 Hook 和 OnGUI 开销**；它们不能证明插件整体开销低或为零。采样启动/停止附近的数组分配、后台导出及测试夹具也会影响现场，应避开这些边界进行性能对照。

隔离验证：`Run-Two.ps1 -FrameTracePath <完整DLL路径>` 配合 `HOLLOWCONNECT_FRAME_PROFILE=1`、`HOLLOWCONNECT_FRAME_TRACE_CHECK=1`、`HOLLOWCONNECT_FRAME_SHARED=1`。Smoke 在测试私有锁上注入 60ms 竞争，并核对异常、finally、原函数调用次数、真实移动和导出数据。此记录 `fixture=true`，不能作为实际游玩性能测量。普通 Run-Two 未指定 DLL 时会把残留诊断 DLL 移到本轮证据目录，避免污染其它测试。
