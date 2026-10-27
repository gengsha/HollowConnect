# 五门通关后加入者卡住：只读排查

2026-10-21。用户报告使用 0.1.30，加入者存活通关五门后到达截图位置，无法移动或交互；后来已退出或脱困，未取得卡住瞬间的 F9 诊断。本轮没有改动生产源码、已安装模组、存档或交付包，没有启动游戏测试。

## 当前结论

优先调查最终结算后的入场及控制恢复。当前运行对应的原生日志出现最后一次 `Boss scene ended`、`Pushing achievement ENDINGD`，随后出现 `No entry gate has been defined in the Game Manager, unable to move hero into position.`。这说明已有结局阶段的线索，但不足以把该错误直接认定为卡住根因：日志没有输出这次错误的场景名、入口参数、角色状态或控制锁所有者，过场也可能刻意不使用普通入口。

截图不能代替运行时场景名。尚未确认截图对应 `GG_Waterways`、其他神居场景还是尚未退出的过场，也未确认截图是否与本机日志末段完全一一对应。用户确认存活通关，不以倒地或救援残留作为既定原因。

## 二进制与证据

游戏路径由当前进程的可执行文件路径定位，没有扫描其他盘或选用隔离副本替代现场版本：`E:\SteamLibrary\steamapps\common\Hollow Knight`。以下是本机安装文件；朋友电脑的文件和日志未取得。

| 文件 | 字节数 | SHA-256 |
| --- | ---: | --- |
| `hollow_knight_Data/Managed/Assembly-CSharp.dll` | 4064768 | `5944411BD93830369390A4B51766EE68C4AB26195B299E25A07B5E7D0E00086D` |
| `hollow_knight_Data/Managed/PlayMaker.dll` | 212480 | `0EF0E7829D125E1F632C8A189260EC6C6882630BE6932C8C7AE032EFBC53469A` |
| `Managed/Mods/HKMP/HKMP.dll` | — | `3F7495F42420DEEB3A88CFE227BFD68A5A54A7361631968CA7A6E0440FF1F69E` |
| `Managed/Mods/HKMP/HollowConnect.dll` | — | `CD5630FFFBDFDBE08E8362093B0951331BBDA7A950100DC7CDB3F6187AA45CBF` |
| `Managed/Mods/HKMP/HollowConnect.Core.dll` | — | `23F3A6E3DCC200D528AD0BB7C65E2853445476C201DC4B3059AEAFEE0816CF8E` |

三份已安装生产 DLL 均与 `docs/evidence/playtest-authorization-0.1.30.json` 一致。版本字符串没有替代哈希核验。

执行了 `hollow-knight-boss-net-audit/scripts/audit_bosses.ps1`，扫描原生 3578 个类型和 1855 个数据目录文件；原始静态报告、机器可读结果和当前安装程序集的相关反编译保存在 `.analysis/p5-completion-lock-20261021/`。这些是静态检查产物，不是双端游戏验收。生产源码检查对象的哈希保存于同目录 `source-hashes.json`。

只提取原生日志里的通用事件，不复制私人日志、玩家名、网络地址或存档。读取时的相关事件顺序为：

1. 最后一次 `Boss scene ended`。
2. `Pushing achievement ENDINGD`。
3. `No entry gate has been defined in the Game Manager, unable to move hero into position.`。
4. 后续出现找不到 Game Manager、Hero、GameCameras 的消息。没有场景名，不能判断这些是故障中的丢失对象，还是退出后的正常销毁被持续访问。

## 风险与最小验证

| 编号 / 优先级 | 触发与可能后果 | 证据 | 置信度 | 最小验证 |
| --- | --- | --- | --- | --- |
| P5-END-01 / P0 | 最终结算返回时入口为空，GM 已结束入场但角色未完成入场；可留下不可操作状态 | 当前日志；原生 `GameManager.EnterHero` 的空入口分支只调用 GM 的 `FinishedEnteringScene`，未调用角色入场；角色的 `FinishedEnteringScene` 才处理转换、重力和输入 | **strong candidate**。分支本身已确认；是否发生在截图场景并导致本次卡住未确认 | 最后一场死亡前开始记录两端场景、每次载入目标和入口、`isHeroInPosition`、`acceptingInput`、`controlReqlinquished`、`transitionState`、GM 入场状态，继续经过原生结局与返回 |
| P5-END-02 / P0 | 最终梦境返回或过场退出缺少本地结束回调，角色入场已完成却仍保持无输入 | 原生梦境入场与控制释放分属角色方法和本地 FSM；`CoopRuntime.CompleteWarpEntry` 的额外恢复仅针对本模组创建的传送，白色防御者恢复仅针对其场景；现有日志缺少最终 FSM 状态 | **requires runtime verification**。没有证据证明本次发生了 F8 或回调被同步代码破坏 | 记录最终 Dream Return、过场 FSM 状态及本地 `RegainControl`/动画恢复调用；若 GM 和角色均完成入场但无输入，再确定实际持锁者 |
| P5-END-03 / P2 | 结局或退出销毁原生对象后，模组仍访问查找式单例，反复输出缺失对象消息，掩盖关键异常 | `CoopRuntime.Update` 在联机与可玩判断前调用 `SampleDebugState`；`BuildDebugSnapshot` 读取 GM、Hero、GameCameras 的 `instance`；日志后段反复出现缺失 Hero | **confirmed static risk**，仅确认存在无对象访问路径，未证明它造成卡死 | 正常走完结局和退出，区分无对象消息从哪个场景开始；检查后续重进时单例和运行时是否恢复 |

原生 `BossSequenceController.FinishLastBossScene` 会设置完成记录、计算本地 `ggEndScene`，再通过 `DoDreamReturn` 发送 FSM 事件。扫描看到这些调用，不等于确认序列最终回调、对应 FSM 及入口在两端都实际完成。单机原生过场或空入口分支本身不作为模组缺陷直接修改。

旧的守望者中途换房修复仍在当前源码中，不能仅凭“都发生在五门”认定是同一条回退链。也不建议对所有无输入状态定时执行 `RegainControl`：最终过场有合法的控制占用，应先识别实际场景和未完成边界。

## 覆盖矩阵

| 范围 | 本轮完成 | 尚缺 |
| --- | --- | --- |
| 安装版本 / 原生代码 | 现场安装文件哈希、相关原生反编译、源码静态比对 | 朋友电脑的文件核验 |
| 通关及返回 | 用户现场报告；本机日志有结局 D 与空入口事件 | 卡住瞬间 F9 状态；同一访问的双端日志关联 |
| 行程推进 / 控制 | 检查现有守望者专项的终点 | 最终辐光到结局、返回后真实移动与交互；旧专项只到 `GG_Uumuu_V`、索引 32 且明确要求尚未完成行程 |
| Boss 血量 / 阶段 / 动态对象 | 本轮未运行 | 不以本次静态审计认证其正确性；只在最终结算探针有需要时检查 |
| 权威 / 生命周期 | 静态检查房间清理、原生结算和可玩门槛 | 交换双方权威；结算期间迟入、断连、接管 |
| 原生返回差异 | 读取原生末场完成方法 | 首次 / 重复完成、双方既有通关记录不同时的原生目标 |
| 实际联网范围 | 未新增运行 | 隔离副本的双端 UDP 定向验证；真实双机现场复核。没有完整连续五门或三人验收 |

后续若复现，脱困前优先按 F9 导出诊断。若需要立即恢复，0.1.30 的 `Ctrl+Alt+F12` 会断开联机并走紧急回德特茅斯长椅流程；这不等于修复五门最终返回。用户已退出或脱困，本轮没有执行该操作。

## 后续实测：2026-10-22

后续实现与隔离副本实测记录于 [结算复活整改](SETTLEMENT-RESCUE-2026-10-21.md)。双方配置相同五门末场上下文后，实际伤害推进无上辐光、完成最后一击及原生过场，复现房主进入 `Cinematic_Ending_D`、加入者误停在空入口 `GG_End_Sequence` 的分歧。该路径遗漏了实际启用的 `SetStaticVariable(ggCinematicEnding=true)`；旧直接换场动作被原生资产禁用，不能作为执行路径证据。限定同步这项末场标记后，开发验证已让双端正确进入原生结局 D；最终构建回归结果以结算报告中的哈希记录为准。

最终构建的三组末场用例已通过：加入者处于暗影、观战或全程存活时，两端都完成原生最后一击／过场并进入 `Cinematic_Ending_D`。生产哈希与逐组记录见 [最终结算验证证据](evidence/settlement-rescue-2026-10-21.json)。

这个复现提供了具体的末场同步缺陷及修复，不等于重新取得了原截图瞬间的状态。结局加载与完整连续五门通关、片尾后重新载入、截图场景中再次移动／交互是不同验收边界，未运行的边界仍不作已修复保证。
