# 日常开发入口

2026-10-18 检查时，现有生产项目的无修改增量编译约 1.2 秒。主要重复工作来自旧构建脚本每次复制游戏引用、解压 API、运行核心测试并继续打包，以及双端套件逐组重复构建。历史 `.test-results` 约 19 GB / 4.9 万文件，不能在每一步都递归扫描。容量为逻辑大小，隔离游戏可能含硬链接，不代表实际占用相同。

默认构建现在只编译模组。测试、双端套件和打包分别执行；首次缺少引用或明确刷新时，才定位本机游戏和准备 Modding API。

```powershell
# 日常编译；已有 vendor/references 时不扫描游戏、不解压 API。
.\scripts\Build.ps1

# 只构建核心测试程序，或 Smoke 插件；不会执行测试/启动游戏。
.\scripts\Build.ps1 -Target Core
.\scripts\Build.ps1 -Target Smoke

# 构建可选 Steam 联机模块；仍需同一次源码构建的 HKMP。
.\scripts\Build.ps1 -Target Steam

# 只构建独立帧诊断插件，不更换生产 DLL、不启动游戏。
.\scripts\Build.ps1 -Target FrameTrace

# 依赖已还原时可省去 restore 检查；依赖变化后不使用此选项。
.\scripts\Build.ps1 -NoRestore

# 首次准备 / 游戏或 API 版本变更时显式刷新引用。
.\scripts\Build.ps1 -RefreshReferences -GamePath '自己的游戏目录'

# 运行已明确选择的检查项（每行一个运行器完整名称）。
.\scripts\Build.ps1 -TestSelection '.test-results/本次选择.txt'

# 需要完整核心回归时显式使用；不会启动游戏或打包。
.\scripts\Build.ps1 -Target Core -RunTests
```

`-Target` 支持 `Mod`、`Core`、`Smoke`、`Installer`、`Steam`、`FrameTrace`。`-RunTests` / `-TestSelection` 会先构建对应测试程序。没有指定名单的 `-RunTests` 执行全部核心回归；日常小改动按既定最近/相关项范围选择。暂未增加自动按 Git 判断最近 100 项的选择器。

默认 `Legacy` 引用和现有安装包对应 Windows 1.5.78.11833 / Unity 2020.2.2 / API v77。刷新引用先检查游戏程序集、再校验 API 压缩包；不匹配时保留已有引用并退出。`tests/Build.References.Tests.ps1` 在隔离目录检查拒绝路径。

新版使用独立的 `Unity6` 构建配置，精确对应本机 Steam `public-beta` 的 **1.5.12620 / Unity 6000.0.61f1**：

```powershell
.\scripts\Build.ps1 -GameVersion Unity6 -GamePath 'E:\SteamLibrary\steamapps\common\Hollow Knight'
.\scripts\Build.ps1 -GameVersion Unity6 -Target Smoke
# 仅在需要重新核对游戏输入并准备引用时显式刷新。
.\scripts\Build.ps1 -GameVersion Unity6 -RefreshReferences -GamePath '新版游戏目录'
```

采用 [API PR #164](https://github.com/hk-modding/api/pull/164)，固定到 `SFGrenade/api` 提交 `468f579f25e63da2188f39fe5ece15c25d2854d7`；这是尚未合并的社区移植。脚本校验固定源码 ZIP 和原版游戏程序集，再从源码生成 API；不会写入所指定的游戏目录。`vendor/unity6-api/<提交>/build.json` 记录游戏输入和全部 API 产物哈希，`payload/` 保存配套 API。不能混用 v77、[PR #174](https://github.com/hk-modding/api/pull/174) 或其它提交的钩子 DLL。Steam 更新可能残留旧 MonoMod/Cecil/MMHOOK；准备引用时会排除这些残留。

新版引用在 `vendor/references-unity6/`，生产 DLL 在 `src/HollowConnect/bin/Unity6/Release/net472/`，可选 Steam DLL 在对应项目的同名输出目录。保留 net472 模组契约，按 #164 使用游戏的核心库和拆分后的 TeamCherry 程序集。新版与旧版的 `bin/obj` 相互独立；普通增量构建仍不重新准备引用。核心检查继续使用默认 `Legacy` 执行，新版游戏检查使用隔离 Mono 客户端。

适配范围是实际变动的类型/字体、场景协程定位、过场、震动和受击/扣血钩子，以及新增原生参数和 Steam 接口。联机协议不变。0.2.9 起打包和核验默认覆盖 Legacy／Unity6 两版；普通构建仍默认 Legacy。新版使用独立 Unity6 包及配套 #164 API，安装与卸载均校验游戏／API 和备份链，不能仅替换模组或放开哈希检查。普通构建不会自动打包或安装。

### 双版本同步开发

两版使用同一工作区和同一套联机／玩法源码，不维护两份分叉。仅游戏 API 差异使用 `HOLLOW_UNITY6` 条件编译；新增共享功能时同时构建两版，避免改一版遗漏另一版：

```powershell
.\scripts\Build.ps1 -GameVersion Legacy
.\scripts\Build.ps1 -GameVersion Unity6
```

`Legacy` 仍是默认配置，输出在原有 `bin/Release/net472/`；`Unity6` 输出在 `bin/Unity6/Release/net472/`。可选 Steam 模块也要分别用 `-Target Steam` 构建。每版引用、编译中间文件和游戏验证证据独立，不能相互替换 DLL 或把某一版的实机结果当作另一版通过。共享协议保持一致不等于已经验证新旧游戏混合联机；混合版本房间不在本次兼容承诺内。

新版实机入口同样指定配置：`tests/Smoke/Run-Two.ps1 -GameVersion Unity6` 或 `Run-Room-Suite.ps1 -GameVersion Unity6`。脚本要求现有 `game-host`／`game-guest` 的引擎与配置一致，只核验、不会自动替换游戏引擎。切回旧版实机验证前，应恢复保留的旧版隔离环境；不能仅切换编译参数就给另一版本的游戏覆盖 DLL。

2026-11-11 的适配验证：两版生产构建及旧版 Smoke／Steam 构建通过，70 项相关核心检查通过；新版最终生产哈希上的 8 组直接专项和完整 12 项房间矩阵均双端通过严格日志检查。包含原生加密存档隔离往返、中文通知渲染、原生 Steam 回环收发及延迟接口。详见 [新版验证记录](evidence/development-validation-unity6-20261111.json)。本轮没有安装、打包或发布；既有 0.2.8 问题继续保留。

底座仍有一项已知限制：启动预加载时的原生 `GameManager.SetupHeroRefs` 空引用，在仅加载同一 API、Mods 为空的隔离标题运行中也可复现。上述流程继续运行并完成，但不能宣称所有原生异常已修复。本次保留固定社区 API，不为消除该日志另行修改游戏／API；原始日志与空 Mods 对照保存在验证记录所指的分析目录。

## 双端专项

```powershell
# 独立运行一次，默认先构建 Smoke。
.\tests\Smoke\Run-Two.ps1 -RunName '唯一名称' -Mode progress

# 已手动准备 Smoke 的连续运行，可显式复用；改源码后应先重建。
.\tests\Smoke\Run-Two.ps1 -RunName '另一个唯一名称' -Mode progress -NoBuild

# 套件自动在开头构建一次，各组复用，继续检查实际 DLL 哈希和严格日志。
.\tests\Smoke\Run-Stability-Suite.ps1 -RunPrefix '唯一前缀' -Only resources,defender
.\tests\Smoke\Run-Room-Suite.ps1 -RunPrefix '另一个前缀' -Only final-rescue-013

# 已准备 Smoke 后补跑房间矩阵，可复用产物；Run-Two 仍核对依赖哈希。
.\tests\Smoke\Run-Room-Suite.ps1 -RunPrefix '补跑新前缀' -Only final-rescue-013 -NoBuild
```

测试模式、开关和对应范围见 [测试完整目录](TEST-INVENTORY-2026-10-18.md)。只改脚本、文档或版本号时不自动开启大型双端矩阵。一次针对性实机验证通过后，只有新变更、失败或未解疑点才需要扩大或重复测试。

## 迭代与长回归规则（2026-10-22）

日常顺序固定为：增量构建及相关核心检查 → 最小直接复现 → 稳定生产 DLL 上的必要回归。前一阶段失败，先修正再进入下一阶段。只改规则或文档时检查文本与差异即可，不构建、不启动游戏。

预计超过 10 分钟或超过 8 组双端用例时，在本任务的 `.analysis/<任务目录>/` 保存明确用例清单、每组必要性、已有可复用结果和预计耗时；耗时优先参考相同用例的近期记录，没有记录就说明未知。开始前简要告知范围，已有授权内直接执行，无须再让用户批准常规检查。重大协议／生命周期改动的完整范围为 12 项房间矩阵加直接受影响专项，不自动扩大为所有 Boss／Unity 套件。扩展范围需记录具体依赖、失败、疑点或用户要求。

构建与直接检查通过后，及时给出本地构建路径、已经验证的范围和仍在等待的检查，并继续完成必要工作。阶段进展不等于最终验收，也不自动触发打包、安装或发布。

长回归由脚本管理启动、有限等待、日志、超时和限定隔离路径的进程清理，优先使用已有的可靠后台执行能力。没有这种能力时拆成可恢复的小批次；不要依赖一个跨越整轮回归的交互会话。用例清单及状态持续落盘，建议每项包含：

| 字段 | 用途 |
|---|---|
| 用例、参数及相关测试逻辑版本 | 确认实际执行范围；测试版本应能识别受影响用例及依赖，不能只靠显示版本。 |
| 生产 DLL、游戏依赖和隔离环境标识 | 判断结果是否适用于当前待验收构建。 |
| 结果目录、运行器／游戏 PID、开始与结束时间 | 中断后先确认是否仍在运行，避免启动另一组杀掉未结束的测试。 |
| 状态及失败摘要 | 区分待运行、运行中、通过、失败、中断、失效；不把中断算通过。 |
| 双端完成及严格日志检查结果 | 只有两端完成、日志通过且文件哈希匹配的结果可复用。 |

工具会话断开后先读清单、当前用例状态和 PID。仍在运行的先接续观察；已完成且全部条件匹配的直接复用；仅补跑缺失、中断、失败或已失效项。每次补跑使用新目录，旧证据保留。当前脚本已有 `-Only`、`-StartAt` 或 `-NoBuild` 时优先使用；不支持自动恢复时按清单手动选择缺项，不能声称已经实现自动缓存／续跑。

最终回归期间冻结生产 DLL；Smoke 的输入未变就复用已准备产物，跨套件继续用 `-NoBuild`。修改测试夹具后重建 Smoke，只重跑受该修改影响的用例及其依赖；记录为何其它用例的测试逻辑仍适用。生产 DLL 哈希改变则原记录只能作为开发证据，要求范围的最终验收必须与新哈希一致。

新改的慢夹具先在最小适用用例中核对原生主／附加场景、对象／FSM、权威及入场条件，给出缺失对象名称和实际状态后立即失败。修夹具不得用强制跳场或直接置状态代替待验证的原生流程。遇到失败先判断产品、夹具或环境原因；只有证据指向瞬时故障时允许一次不改代码的重试，原失败与重试均保留。不要盲目整套重跑。

共享 `game-host`／`game-guest` 的套件继续串行；现有启动器会替换文件并停止这两份副本的进程，不能为了提速直接并发它们。独立读取与构建准备可以并行，不额外创建游戏副本或代理来追求并发。脚本内部监控进度，外部只取简短状态和新失败；故障或停滞时再读取相关详细日志。更新说明通过／待跑／失败数量和新发现，避免反复读取整份日志或重复播报等待。

## 打包与文件

用户要求打包时默认两版同时交付，同一版本号分别保留游戏依赖和验收哈希；只有用户明确限于单版才指定 `-GameVersion Legacy` 或 `Unity6`。0.2.9 的本地交付命令为：

```powershell
.\scripts\Package.ps1 -SteamModule -UserRequestedRelease
.\scripts\Verify-Release.ps1 -SteamModule -UserRequestedRelease
```

两条命令均默认 `-GameVersion Both`，串行处理 Legacy／Unity6。该参数是现有本地打包记录入口，不会自动创建 GitHub Release。0.2.9 使用 17 项核心及每版三组最终哈希专项，完整 12 项房间矩阵未重跑；Legacy 交付记录中的 `retestOmitted` 指完整矩阵省略，专项实测见单独验证摘要。两版保持相同源码和网络标识，不据此承诺新旧游戏混合联机。

公开文档入口是 [docs/README.md](README.md)。文档清单集中在 `scripts/Documentation.psd1`：源码包保留公开开发指南，Windows 包只带玩家文档、许可和必要发布记录；指向未附带开发文档的链接自动转到 GitHub。重复打包前会把旧的解压目录文档另行保留，避免旧报告残留到新包。

逐轮日志、失败尝试与临时分析放在 `.analysis/<任务>/` 或 `.test-results/<运行>/`，不继续往 `docs/` 追加过程记录。公开变更合并进对应指南和本版更新；发布摘要记录版本、哈希、覆盖范围和限制。历史文档与日志保留在 Git 固定提交和本地归档，参见 [历史版本](CHANGELOG.md)。不要改写既有证据或删除失败来精简目录。

正式发布渠道和实机验收范围分别记录。用户明确要求正式 Release、此次仅升版打包时，可用 `scripts/Package.ps1 -UserRequestedRelease`，并以同版本的 `docs/evidence/release-authorization-<版本>.json` 绑定原始请求、当前三份生产 DLL 哈希及保留问题账本的哈希；使用 `scripts/Verify-Release.ps1 -UserRequestedRelease` 校验相同记录和包内文件。此路径保留 `retestOmitted` 和原始验证证据，不将旧实机结果追认为新哈希的完整验收。未使用该参数时沿用既有运行记录门槛。

`Package.ps1` 继续独立执行并保留已有证据门槛。压缩改为直接写 ZIP，源码枚举从入口跳过 `bin/obj` 和草稿；新包写完后才替换目标 ZIP。`SHA256SUMS.txt` 保留历史项，只计算本次新包及缺失记录的哈希。需要核验历史 ZIP 是否被外部更改时显式用 `-RebuildChecksums`；单包 `Verify-Release.ps1` 仍检查当前包的内容和校验和。

本地候选版使用 `scripts/Package.ps1 -ReleaseCandidate rc2 -SteamModule`，核验使用 `scripts/Verify-Release.ps1 -ReleaseCandidate rc2 -SteamModule`。候选版沿用有明确请求与哈希记录的 playtest 通道，产物名为 `HollowConnect-0.2.6-rc2-*`，不会发布 GitHub。`-SteamModule` 加入匹配构建的 Steam DLL/PDB 和清单；安装器按清单复制。记录中的附加文档只能位于 `docs/`，用于携带候选版说明和原始哈希对应的验证摘要。升版后未重测的 DLL 不会追认为先前实机通过。

当前 Unity6 包名为 `HollowConnect-0.2.9-unity6-*`，入口校验本版三组最终哈希专项、用户本地打包请求和固定 API 缓存，包含配套 API，不读取旧版 v77 产物。旧版 ZIP 保留；仅打包安装器，不重建已验收的游戏 DLL、不重新运行游戏矩阵。安装器支持清单指定的 API 压缩包，拒绝错误游戏哈希和跨游戏版本的卸载备份链。玩家说明见 [0.2.9](UPDATE-0.2.9.md)。此前 0.2.8 的 20 组移植验证及交付见 [Unity6 0.2.8](UNITY6-0.2.8.md)，仍保留原哈希。

历史 0.2.8 受击无敌帧修订使用 `-GameVersion Unity6 -Unity6Revision hit-evasion`，产物另加 `hit-evasion` 后缀。它校验当时四组专项与原请求，不能用新版本 DLL 改写旧交付；本功能已纳入 0.2.9 双版本。实现和边界见 [受击无敌帧说明](HIT-EVASION-0.2.8.md)。游戏内针对性入口为 `Run-Two.ps1 -GameVersion Legacy|Unity6 -Mode pantheon`（任选一个配置），设置 `HOLLOWCONNECT_HIT_EVASION_CHECK=1`；分别用 `HOLLOWCONNECT_PANTHEON_AUTHORITY=host` / `guest` 检查两种场景权威。水晶山峰专项为 `-Mode coverage`，设置 `HOLLOWCONNECT_CRYSTAL_RANGE=crystal`、`HOLLOWCONNECT_CRYSTAL_SCENE=Mines_20` 和 `HOLLOWCONNECT_COVERAGE_AUTHORITY=host|guest`。

生产代码、模组行为和已交付的 0.1.22 ZIP 未因本次流程调整而改变。没有自动移动/删除历史日志或旧安装包，以免破坏证据路径。目录搜索优先 `rg src scripts tests`；调查某次失败时再指定 `.test-results/具体运行名`，不递归整个历史目录。

只验证本次脚本文件操作可运行 `tests/Workflow.Tests.ps1`，使用隔离文件夹，不启动游戏。它检查 ZIP 路径和内容、源码排除、失败时保留旧包、增量校验和及显式全量重算。

安装脚本与游戏运行检测分别使用 `tests/Installer.Tests.ps1` 和 `tests/GamePath.Process.Tests.ps1`。后者检查零线程残留、活跃目标／其它目录、路径无权限及 CIM 不可用，并用同名休眠辅助程序验证实际 Windows PowerShell 的拦截；不会启动游戏。CIM 确认线程数为 0 才忽略残留记录，未知状态继续保守阻止并给出 PID。

本次 9 项文件流程检查通过。用既有 0.1.22 安装目录作同机压缩对比：旧 `Compress-Archive` 约 1.66 秒，新直接写 ZIP 约 0.67 秒；492 个文件逐项与原内容哈希一致，压缩包大小相同。这是单次本机压缩阶段样本，不是完整打包或所有机器的耗时保证；默认无修改构建另测约 1.17 秒。未运行游戏测试、未重新发布 0.1.22 安装包。
