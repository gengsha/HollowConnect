# 苍绿之径藤蔓石块本地化（2026-10-19）

用户要求可用骨钉砍落的藤蔓石块只响应本机，不因队友的同步刀光掉落。本轮已修改并通过针对性双端检查，未升版、未打包。

## 原因和修改

真实场景 `Fungus1_06/left1` 有 7 个原生 `VinePlatformCut`。石块本身不在同步实体列表中，但 HKMP 远端刀光使用原生刀光 prefab，也带 `Nail Attack` 标签；原生藤蔓接收器因此会接受队友刀光。此外，保存映射中另有 19 项 `Vine Platform` 持久化记录原本允许服务器共享。

- `GamePatcher` 对原生藤蔓攻击入口增加连接期间的本机 Hero 层级检查，只允许本机骨钉触发。注册、卸载钩子成对处理，不改全局刀光碰撞。
- `SaveDataMapping` 将这 19 项记录的 `Sync` 关闭，保留原有网络索引和顺序。
- `SaveManager` 拒绝接收这些石块的历史全存档或迟到更新，保留本机自己的 SceneData。
- `ServerManager` 拒绝接收这些石块的上传更新，避免旧客户端继续写入及广播。

19 项记录分布：`Fungus1_06` 7 项、`Fungus1_07` 4 项、`Fungus1_09` 1 项、`Fungus1_13` 3 项、`Fungus1_14` 1 项、`Fungus1_21` 1 项、`Fungus1_22` 2 项。

## 验证

仅使用现有隔离游戏副本 `.test-results/game-host`、`game-guest`，全新内存角色 99，拦截全部存档写入。两端通过真实 localhost UDP 连接，永久进度共享开启。

| 运行 | 结果 |
|---|---|
| `local-vine-baseline-20261019-c` | 修复前复现失败：远端刀光进入原生藤蔓接收器后，另一端的本地石块也掉落。保留失败证据。 |
| `local-vine-fixed-20261019-a` | 房主、副机轮流用本机原生攻击砍落石块，各自观察到实体下落，另一端的对应石块保持悬挂。两端完成标记及退出后严格日志检查通过。 |

两端另验证全部 19 项映射均禁止向外共享且原索引仍可查；向真实客户端保存接收入口注入旧的掉落记录，未改变未砍石块或本地 SceneData。服务器上传拦截完成静态检查，本轮没有模拟旧客户端向服务器实际发送该包。

远端攻击检查使用真实 HKMP 远端刀光 prefab，并在接收端显式调用原生碰撞入口，消除网络动画时序对命中测试的影响；同时记录到真实 Unity 刀光接触。本项属于原生接收入口夹具，不作为完整网络攻击动画链路或异机 FRP 验收。

其余六个房间只检查保存映射，未逐房实测；未重跑无关 Boss 专项。具体 DLL、探针及日志哈希见 [证据 JSON](evidence/local-vine-platforms-2026-10-19.json)。前期选错房间、入口及固定测试角色不充分的诊断运行保留，不计为通过。

## 复跑

```powershell
./scripts/Build.ps1 -Target Smoke -NoRestore
$env:HOLLOWCONNECT_LOCAL_VINE_CHECK='1'
./tests/Smoke/Run-Two.ps1 -RunName <新证据目录名> -Mode rescue -NoBuild
```

等待双方 `.done` 后，仅关闭路径核对无误的隔离游戏进程，再执行 `Assert-ProbeLogs.ps1 -RunPath <证据目录> -RequireRuntimeFiles`。普通探针默认不启用本专项。
