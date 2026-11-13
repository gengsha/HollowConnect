# HollowConnect 0.3.1 のインストール

[Releases](https://github.com/gengsha/HollowConnect/releases/tag/v0.3.1)から **Windows版の完全なZIP**をダウンロードしてください。ゲームが1.5.78.11833なら **Legacy**、1.5.12620なら **Unity6** を選択し、すべて展開してゲームを終了します。

## インストーラー

`HollowConnectInstaller.exe`（`一键安装.exe`も同じ内容）を起動し、言語欄で **日本語** を選択します。ゲームフォルダーを指定し、「インストール / 更新」を押してください。付属ファイルを移動しないでください。置換前のファイルはバックアップされ、アンインストール時も現在のセーブは保持されます。

## スクリプトで導入（インストーラー不要）

展開したフォルダーでPowerShellを開き、実際のゲームパスを指定します。

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Install.ps1 -GamePath "E:\SteamLibrary\steamapps\common\Hollow Knight"
```

APIの検証、バックアップ、導入が実行されます。元に戻す場合は同じ `-GamePath` を指定して `Uninstall.ps1` を実行します。

## 手動で導入（インストーラー不要）

1. セーブ、既存の `Mods/HKMP`、置換するManaged内のファイルをバックアップします。
2. `dependencies/` 内の **同梱Modding API ZIP**を `hollow_knight_Data/Managed` に展開し、構造を保って同名のAPIファイルを置換します。Legacyはv77、Unity6は同梱の固定APIを使います。混在させないでください。
3. **`payload/HKMP` フォルダー全体**を `hollow_knight_Data/Managed/Mods/HKMP` にコピーします。更新時は古いHKMPを先に退避し、新しいフォルダーを丸ごと配置します。HollowConnect.dllだけでは動作しません。
4. ゲームを起動します。手動導入では自動バックアップ履歴を作成しません。削除時は自分で保存したバックアップを復元します。

## 遊び方

全員が同じMODバージョンと各ゲームに対応するパッケージを使ってください。メインメニューからLANマルチプレイ（IP / UDPポート）またはSteamを選択します。部屋の設定はホスト前に「オプション → MOD → HollowConnect」で変更します。進行状況を共有する前にセーブをバックアップしてください。

ゲーム内表示はゲームの言語に追従します。F6で救援待ちを放棄、F7で仲間を選択、許可されていればF8で転送します。通常の状態通知はログに記録します。F5長押しで死亡ランキング、F9で診断を表示します。別の部屋の仲間の位置はマップを開いて確認できます。

[更新内容と検証範囲](UPDATE-0.3.1.md) · [既知の問題](KNOWN-ISSUES.md)
