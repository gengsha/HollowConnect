# 特殊死亡与救援落位检查

2026-10-16。用户要求严查白宫、苦痛之路、竞技场、最终 Boss、移动平台和无安全落点。本轮只操作隔离客户端，拦截全部存档保存重载，不升版、打包或安装。

## 已确认的问题和修正

旧生产构建 `5bc3349` 的 `rescue-edge-faults-baseline-20261016` 在真实双客户端与 UDP 下，对房主/副机分别注入两种支撑故障：倒地后移走支撑、死亡时根本没有支撑。四种情况均可实际挥刀救起，受救者随后处于空中下坠；日志记录向下速度约 `20.9`。这是故障注入证明的公共漏洞，不声称在某个原生平台已经复现同一故障。

生产提交 `687582a` 做两项修正：接受救援确认时重新检查完整身体的支撑、净空和危险区；死亡时或救起时确实没有任何合法落位，则复用现有个人返回消息和原生死亡流程。救起时服务器已接受旧死亡序号，因此先登记新的倒地序号，再请求个人返回，保留最初 `PersonalDeath` 上下文。重复旧确认不能作用于新序号。全队倒下仍由原来的全队失败流程处理。不增加网络消息类型，不维护逐场景坐标表，不复制原版死亡奖励或惩罚逻辑。

## 原生规则

只读输入为 `E:/SteamLibrary/steamapps/common/Hollow Knight`。本轮重新运行 `hollow-knight-boss-net-audit` 技能的静态工具，扫描 3578 个类型和 1854 个数据文件；产物在 `.test-results/rescue-edge-audit-20261016/native-audit`。`Assembly-CSharp.dll` 的 SHA-256 为 `5944411BD93830369390A4B51766EE68C4AB26195B299E25A07B5E7D0E00086D`，与前轮一致。原生死亡 FSM 导出保留在 `.test-results/inspect-death-011/host.inspect.txt`。

| 范围 | 原版机制与要核对的行为 |
| --- | --- |
| 白宫 | `WHITE_PALACE` 使用梦境死亡动画，正式返回仍按个人长椅，不应掉吉欧或新增个人暗影 |
| 苦痛之路 | `White_Palace_18/19/20/17` 均实际读取为 `WHITE_PALACE`；危险触发器可在角色移动时更新本机复位点，不能硬认入场时坐标 |
| 竞技场三试炼 | `Room_Colosseum_Bronze/Silver/Gold`；正式死亡的 `Colosseum Shade` 将个人暗影放在 `Room_Colosseum_02` 的 `(27.2,45.6)`，临时救援应留在当前房间。本房也有实际 `Shade Marker`，不可把正式跨房暗影位置与临时救援锚点混为一谈 |
| 空洞骑士与辐光 | `Room_Final_Boss_Core`、`Dream_Final_Boss`；原生 `Final Boss Shade` 的跨房暗影位于 `Room_Final_Boss_Atrium` 的 `(219,10)`，不能只因场景名有 Dream 就认定无死亡惩罚 |
| 神居与万神殿 | 使用实际挑战返回门和 BossSequence 上下文，核对全队失败后无新增个人暗影、吉欧保留和输入恢复 |
| 移动/消失平台 | 死亡时的合法支撑不代表收到救援确认时仍合法；应重新查询现有支撑或原生回退点 |
| 无任何安全落点 | 原生点与地面检查全部失败时，走原版个人死亡返回，不让一血骑士在无支撑处继续游玩 |

## 运行记录

所有正式通过必须双方 `done`、双方正常断开、服务器停止、无 `failed`，且隔离进程停止后通过严格日志扫描。DLL 以各组 `binaries.json` 和最终机器证据为准；旧 DLL 的通过不能移记为新构建的通过。

已通过的新生产构建专项包括：白宫与苦痛四房全队死亡、三竞技场原版正式死亡、空洞骑士正式死亡、神居与第三万神殿上下文返回；白宫真实升降台实际运动后的双角色死亡与挥刀救援；两角色支撑移走/无支撑的个人原版返回；两角色原落点失效后的新原生复位点救援。普通死亡/F6/全队失败、普通/神居/梦境六组合的原生救援保护与双方闪烁、真实酸水/尖刺碰撞，以及四轮救援奖励与旧消息拒绝也通过。最终场景/日志清单在机器证据中逐组记录。

选择回归为沿用最近 100 项和原有 21 项相关测试，加 1 项新死亡序号测试，共 **122 通过、0 失败**；严格日志门槛 **14 项通过**。本轮没有把全部历史测试重复一遍。

| 专项 | 已实际验证的结果 | 最终运行目录后缀均为 `20261016` |
| --- | --- | --- |
| 白宫与苦痛之路 | 5 房 × 双角色全队失败：个人长椅、无新增暗影/无吉欧惩罚、返回后物品栏和重复跳跃；5 房 × 双角色普通致命攻击与实际骨钉救援：原生标记附近有支撑、救起一血和无敌、不再死亡、不离房 | `rescue-edge-palace/pop18/pop19/pop20/pop17-verified-*`；`rescue-edge-palace-rescue-1/2/3/4/5-isolated-*` |
| 竞技场 | 三试炼正式死亡的个人惩罚和跨房暗影；三试炼 × 双角色临时救援留在战场、实际落地和控制 | `rescue-edge-bronze/silver/gold-current-checkpoint-*`；`rescue-edge-colosseum-rescue-1/2/3-final-*` |
| 空洞骑士 | 正式死亡的原生跨房暗影、个人返回与输入；双角色房内实际救援 | `rescue-edge-hollow-knight-verified-*`；`rescue-edge-exception-rescue-4-*` |
| 神居与万神殿 | 实际返回门、原生第三万神殿序列上下文、无个人暗影和吉欧惩罚、返回后实际输入 | `rescue-edge-godhome/pantheon-verified-*` |
| 白宫原生移动平台 | 双角色实际落在升降台，原生平台移动超过 0.3，再致死、挥刀救援并落地 | `rescue-edge-lift-verified-*` |
| 移走支撑且无回退点 | 双角色救援确认后使用新死亡序号，走原生个人返回；存活者留房且不推进全队轮次 | `rescue-edge-moved-guest-native-*`；`rescue-edge-moved-host-verified-*` |
| 从死亡起没有支撑 | 双角色不等待无效空中救援，原生个人返回、临时暗影清除、个人既有暗影/吉欧保留 | `rescue-edge-missing-guest/host-verified-*` |
| 移走支撑但有新原生点 | 双角色重新选实际有支撑的原生危险复位点，房内一血救援，无正式死亡 | `rescue-edge-fallback-guest/host-verified-*` |
| 辐光 | 双角色房内实际救援；正式死亡的吉欧/暗影与个人长椅返回；先起身，再连续两次跳跃落地、攻击、打开/关闭物品栏和输入恢复均通过。刚回长椅立即开物品栏仍单列待查 | `rescue-edge-radiance-active-scene-final-*`；`rescue-edge-radiance-stand-first-*` |

最终 **33 组**同生产哈希双客户端专项通过。逐组终态、生产 DLL 和日志 SHA-256 见 [机器证据](evidence/rescue-edge-audit-2026-10-16.json)。所有通过组在停止隔离进程后重新扫描日志，另列 32 组历史/对照/失败或未完成运行，不以它们补足验收。

保留的前置与失败运行：

- `rescue-edge-godhome-baseline-20261016`、`rescue-edge-palace-wipe-20261016`、`rescue-edge-pop18-wipe-20261016`、`rescue-edge-pop19-wipe-r2-20261016`：旧生产构建的专项结果，终态与日志各自留存。
- `rescue-edge-inspect-20261016`：初版使用不存在的苦痛之路入口 `left1` 导致入场超时。已从原生构建场景表和关卡资产核对入口，不算生产死亡故障。
- `rescue-edge-inspect-gates-20261016`：白宫及苦痛四房成功读取；在连续切到竞技场下一试炼时没有终态，不计通过。
- `rescue-edge-palace-baseline-20261016`、`rescue-edge-pop19-wipe-20261016`：启动器在前一隔离进程完全退出前复制 DLL，文件仍映射而拒绝；无完整测试，不计通过。启动器补等待进程退出。
- `rescue-edge-palace-placement-20261016`：苦痛 `White_Palace_20` 的原生危险触发器更新了复位点，初版断言错误要求初始入场坐标；保留并改为核对实际当前原生点和支撑。
- `rescue-edge-palace-placement-r2-20261016`：同一房主第二轮没有产生预期致命伤，保留为未完成，不推定根因；后续检查原生受伤/复位前置状态。
- `rescue-edge-pop20-wipe-20261016`：夹具在底部入口的原生上升流程完成前冻结重力，导致保护窗口无法就绪；补等原生入口完成，保留失败。
- `rescue-edge-moved-guest-final-20261016`、`rescue-edge-moved-guest-trace-20261016`：前两个新构建故障修正测试错误要求长椅返回后立即 `acceptingInput=true`，超时并保留。后续 `rescue-edge-moved-guest-native-20261016` 的八秒状态追踪确认已经原生返回 Town、`Returning=false`、`atBench=true`；夹具先等待原生长椅 Resting，再发起起身并断言操作恢复，整组通过。没有因此调整生产控制权或缩短死亡动画。
- `rescue-edge-bronze/silver/gold-verified-20261016`：测试错误硬认最初设置的 Town。Gold 字符串追踪确认房主在正式死亡前已由原生入场改为 `Room_Colosseum_02/RestBench`；副机仍保留个人 Crossroads_47。后续三组 `*-current-checkpoint-*` 以各自死前实际原生记录为准，惩罚、跨房暗影和返回后实际输入检查通过。这验证返回逻辑保留本机记录，不等于验证了竞技场入场对双方复活点的改写完全一致。
- `rescue-edge-placement-verified-20261016`：追踪确认在苦痛 `White_Palace_20` 交换救援角色时，靠近底部入口的测试站位触发了离房；强加的受伤刺激发生于 `WAITING_TO_ENTER_LEVEL`，未生效，后续另一房间地形导致倒地。此组不算当前房间普通致命攻击或救援通过。改用实际房内原生标记作为测试站位，增加入场完成、当前房间及直接攻击即时倒地断言，不改生产死亡逻辑。
- `rescue-edge-placement-interior-20261016`：前四房包括 `White_Palace_20` 已完成直接受伤与双方实际救援，但副机进入第五房时触发了原生离房，整组无成功终态，不能合并算通过。后续五个 `palace-rescue-*-isolated-*` 用独立进程分别验证，每组双方正常退出并通过日志门槛。测试在原生入口完成后固定角色，随后等待网络快照，避免无输入等待网络期间又走进出口。
- `rescue-edge-exception-rescue-1/2/3-20261016`：最初临时救援测试错误硬认危险复位点，竞技场本房实际有原生 `Shade Marker`。实际锚点在标记的支撑处，后续按原生房间资产核对，失败保留。
- `rescue-edge-radiance-verified-20261016`、`rescue-edge-radiance-current-checkpoint-20261016`、`rescue-edge-radiance-diagnostic-20261016`：原版死亡惩罚、跨房暗影及个人长椅返回均完成，但物品栏输入未通过。`rescue-edge-radiance-native-control-20261016` 明确绕过协作倒地/个人返回/全队释放逻辑，仍同样失败。继续追踪原生输入与测试进入方式，不能记为辐光输入已验收或断言是本次生产修改造成。
- `rescue-edge-exception-rescue-5-20261016`、`rescue-edge-radiance-rescue-final-20261016`、`rescue-edge-radiance-rescue-visualization-20261016`：测试曾把 `GameManager.sceneName=Dream_Final` 误判为离房。原生 `GameManager.UpdateSceneName/GetBaseSceneName` 会去掉 `_Boss` 后缀，实际 Unity 场景仍为 `Dream_Final_Boss`。`rescue-edge-radiance-prelude-trace-20261016` 在同一时刻读取到该实际场景的 Boss Control，证实没有据此离房。已改为实际 Unity 场景名核对；后续双方救援完整通过。原失败保留，前期“自动离房”的解释撤回。10 秒原生观察组随后因夹具仍硬认初始入场保护而失败，仅作为观察证据，不当作正式死亡验收。
- `rescue-edge-radiance-input-trace-20261016`、`rescue-edge-radiance-native-input-detail-20261016`：原生物品栏监听收到按键、暂停关闭、时间比例正常，但刚回长椅时没有打开物品栏，仍待归因。绕过协作死亡的对照也失败，不能据此修改协作控制权。另设 `HOLLOWCONNECT_DEATH_STAND_FIRST=1` 只跳过“坐着立即开包”这个刺激，保留全部起身、重力、两次实际跳跃/落地、攻击和站立后物品栏开关断言；正式协作死亡的双方均通过。该通过仅说明起身后控制恢复，不关闭刚回长椅立即开包的时机现象。

## 保留的待查与未覆盖

- 辐光返回后刚坐上长椅立即按物品栏的响应时机，尚未归因；先起身后的完整输入已通过。没有用强制解锁或改写原版 FSM 掩盖此现象。
- 直接进入三竞技场试炼时观察到入场改写复活点存在房主/副机差异。正式死亡正确保留各自死前记录，但实际从休息区报名、首次入场与迟入时是否应都改写为竞技场长椅，尚需正常入口专项；不能把当前个人返回通过说成竞技场整个流程已完全一致。
- 本轮白宫、苦痛五房、三试炼、空洞骑士/辐光房内救援都验证了死亡分支；没有逐段走完苦痛、逐波打完三试炼或打完最终 Boss 全流程。没有逐个平台测试每个运动、消失或机关动画。
- 钢魂、救援关闭的原版直通仅静态核对，未在本轮新做实机流程。此前六项历史未解继续保留。

## 覆盖边界

本轮验证死亡与救援分支，不等于逐波通关所有竞技场、完整走完苦痛之路或通关全部万神殿。故障注入覆盖平台支撑失效的处理，不能代替所有原生平台的运动与消失动画。所有网络运行均为同机双客户端真实 UDP，不能称为真实双机 FRP 验收。此前六项未解问题不因本轮检查而关闭。
