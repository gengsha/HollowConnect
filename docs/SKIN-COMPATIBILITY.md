# 皮肤兼容

HollowConnect 不附带皮肤或 Custom Knight。皮肤与模组应分别从各自作者提供的渠道安装；联机双方仍须使用同一版本的 HollowConnect 完整包。

当前已修复联机动画观察中的循环帧越界和动画事件切换后的旧范围访问。检测到 Custom Knight 时，也会协调钟道兽视频的准备时序，避免原生与皮肤替换重复准备同一播放器。

含自定义视频的皮肤与普通逐帧皮肤存在不同路径。用户报告的单端进程闪退未在本地复现，以上修复不能视为已经确认解决该闪退。遇到问题请附皮肤名、Custom Knight 版本、双方诊断和崩溃日志，并说明是否替换了 `Cinematics/StagTunnelRun.webm`。

既有隔离检查使用 Custom Knight 3.5.0、Satchel 0.9.4，覆盖单方／双方使用视频皮肤、移除插件对照、原生钟道兽播放／跳过与到站。范围和原失败见 [原始兼容报告](https://github.com/gengsha/HollowConnect/blob/c1a6d16fb5cf14b1f8470bf8c8b18b8f74349a2b/docs/SKIN-COMPATIBILITY.md)，当前未解项见 [已知问题](KNOWN-ISSUES.md)。
