# 皮肤兼容

## 使用兼容皮肤包

1. 将 ZIP 解压至 `hollow_knight_Data/Managed/Mods/HKMP/SkinPacks/`。以《死亡细胞》为例，解压后应有 `SkinPacks/死亡细胞/Knight.png`。多套一层解压目录也能识别，皮肤名称取直接存放 PNG 的文件夹名。
2. 重启游戏，在 **模组 → HKMP → 客户端设置 → 兼容皮肤（本机）** 选择皮肤。这个选项在发现皮肤包且 Custom Knight 未启用时显示。兼容入口也会读取 `Mods/CustomKnight/Skins/` 里已经解压的皮肤。
3. 联机时，双方使用支持此功能的 HollowConnect，并各自放置同一套同名皮肤。房间需要开启允许皮肤。联机传递皮肤目录名，接收端从本机读取图集。缺失或损坏的单张图集使用原版外观。

兼容入口支持角色、冲刺、原版血量 HUD、嚎叫和黑吼图集，对应 `Knight.png`、`Sprint.png`、`Hud.png`、`Wraiths.png`、`VoidSpells.png`。支持的部分图集可单独使用。视频、场景替换、护符、背包和皮肤自带脚本仍需要相应的 Custom Knight 功能。

在 Unity 6 上可直接用这个入口加载《死亡细胞》原包。旧版 Custom Knight 3.5.0 使用了 Unity 6 已变更的游戏类型，加载此插件需要对应游戏版本的构建。

## 使用 Custom Knight

Windows Legacy 1.5.78.11833 已验证可用 Custom Knight 3.5.0。按插件说明安装后，将皮肤解压至 `hollow_knight_Data/Managed/Mods/CustomKnight/Skins/<皮肤目录名>`，在 Custom Knight 菜单选择。HollowConnect 会同步该选择，队友安装同名皮肤包后即可看到。Custom Knight 负责其支持的本地附加素材和替换规则。
