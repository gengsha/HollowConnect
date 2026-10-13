param([string]$OutputRoot)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (!$OutputRoot) { $OutputRoot = Join-Path $root ('.test-results\installer-portable-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff')) }
New-Item -ItemType Directory -Path $OutputRoot | Out-Null
$OutputRoot = (Resolve-Path -LiteralPath $OutputRoot).ProviderPath
. (Join-Path $root 'scripts\GamePath.ps1')
$script:passed = 0
function Assert($Condition, [string]$Message) { if (!$Condition) { throw $Message } }
function Pass([string]$Message) { $script:passed++; Write-Output "PASS $Message" }
function Write-Text([string]$Path, [string]$Text) {
    [IO.Directory]::CreateDirectory((Split-Path -Parent $Path)) | Out-Null
    [IO.File]::WriteAllText($Path, $Text, [Text.UTF8Encoding]::new($false))
}
function Hash([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash }
function New-Game([string]$Path) {
    Write-Text (Join-Path $Path 'hollow_knight.exe') 'fixture executable, never launched'
    Write-Text (Join-Path $Path 'hollow_knight_Data\Managed\Assembly-CSharp.dll') 'original assembly'
    Write-Text (Join-Path $Path 'hollow_knight_Data\Managed\Mods\HKMP\old-mod.txt') 'original mod'
}
function Invoke-Installer([string]$Package, [string]$Mode, [string]$Game) {
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = Join-Path $Package '一键安装.exe'
    $start.Arguments = if ($Game) { $Mode + ' "' + $Game + '"' } else { $Mode }
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true
    # The GUI must work even when its parent has a Core-only/broken module path.
    $start.EnvironmentVariables['PSModulePath'] = Join-Path $OutputRoot 'nonexistent-core-modules'
    $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
    $start.StandardOutputEncoding = [Text.Encoding]::UTF8; $start.StandardErrorEncoding = [Text.Encoding]::UTF8
    $start.WorkingDirectory = $OutputRoot
    $process = [Diagnostics.Process]::Start($start)
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync(); $stderr = $process.StandardError.ReadToEndAsync()
        if (!$process.WaitForExit(30000)) { $process.Kill(); throw 'Installer timed out' }
        $result = [pscustomobject]@{Code=$process.ExitCode; Output=$stdout.GetAwaiter().GetResult()+$stderr.GetAwaiter().GetResult()}
        Assert (!$result.Output.Contains('#< CLIXML')) 'PowerShell serialized progress leaked into installer output'
        $result.Output | Add-Content -LiteralPath (Join-Path $OutputRoot 'operations.log') -Encoding UTF8
        return $result
    } finally { $process.Dispose() }
}

$steam = Join-Path $OutputRoot 'Steam custom'
$library = Join-Path $OutputRoot "另一个游戏库 [1] O'Brien & Co"
$game = Join-Path $library 'steamapps\common\Knight 中文 [1]'
New-Game $game
$escapedLibrary = $library.Replace('\','\\')
Write-Text (Join-Path $steam 'steamapps\libraryfolders.vdf') ('"libraryfolders" { "1" { "path" "' + $escapedLibrary + '" "apps" { "367520" "1" } } }')
Write-Text (Join-Path $library 'steamapps\appmanifest_367520.acf') '"AppState" { "appid" "367520" "installdir" "Knight 中文 [1]" }'
$found = @(Get-HollowKnightInstallations -SteamRoots @($steam))
Assert ($found.Count -eq 1 -and $found[0] -eq $game) 'Modern multi-library manifest discovery failed'
Pass 'modern Steam libraries find a custom game folder with Chinese, spaces and brackets'
Write-Text (Join-Path $steam 'steamapps\libraryfolders.vdf') ('"LibraryFolders" { "1" "' + $escapedLibrary + '" }')
Assert (@(Get-HollowKnightInstallations -SteamRoots @($steam)).Count -eq 1) 'Legacy library discovery failed'
Pass 'legacy Steam library format remains supported'
$second = Join-Path $steam 'steamapps\common\Hollow Knight'; New-Game $second
Assert (@(Get-HollowKnightInstallations -SteamRoots @($steam,$steam)).Count -eq 2) 'Multiple installations or deduplication failed'
Pass 'multiple game copies are returned without duplicate entries'
Assert (@(Get-HollowKnightInstallations -SteamRoots @((Join-Path $OutputRoot 'missing'))).Count -eq 0) 'Missing roots produced false detection'
Assert ((Resolve-HollowKnightDirectory (Join-Path $game 'hollow_knight.exe')) -eq $game) 'Executable selection did not resolve game directory'
$rejected = $false
try { Resolve-HollowKnightDirectory $OutputRoot | Out-Null } catch { $rejected = $true }
Assert $rejected 'Invalid manual game directory accepted'
Pass 'manual executable selection works and invalid game folders are rejected'

$package = Join-Path $OutputRoot "安装包 [2] O'Brien & Co"
New-Item -ItemType Directory -Path $package | Out-Null
foreach ($name in 'Install.ps1','Uninstall.ps1','GamePath.ps1') { Copy-Item -LiteralPath (Join-Path $root "scripts\$name") -Destination $package }
foreach ($name in '一键安装.exe','一键安装.exe.config') { Copy-Item -LiteralPath (Join-Path $root "src\HollowConnect.Installer\bin\Release\net472\$name") -Destination $package }
$api = Join-Path $OutputRoot 'api-fixture'
Write-Text (Join-Path $api 'Assembly-CSharp.dll') 'API assembly'
Write-Text (Join-Path $api 'Modding.dll') 'API dependency'
New-Item -ItemType Directory -Path (Join-Path $package 'dependencies') | Out-Null
$zip = Join-Path $package 'dependencies\moddingapi.v77.windows.zip'
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($api, $zip)
$payloadFile = Join-Path $package 'payload\HKMP\HollowConnect.dll'
Write-Text $payloadFile 'mod version one'
$manifest = [ordered]@{version='fixture1';vanillaAssemblySha256=(Hash (Join-Path $game 'hollow_knight_Data\Managed\Assembly-CSharp.dll'));apiAssemblySha256=(Hash (Join-Path $api 'Assembly-CSharp.dll'));apiZipSha256=(Hash $zip);files=@(@{name='HollowConnect.dll';sha256=(Hash $payloadFile)})}
$manifestPath = Join-Path $package 'manifest.json'
$manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $manifestPath -Encoding UTF8
$result = Invoke-Installer $package '--install' $game
Assert ($result.Code -eq 0) ('Initial install failed: ' + $result.Output)
Pass 'installer uses system Windows PowerShell modules despite an incompatible parent module path'
$managed = Join-Path $game 'hollow_knight_Data\Managed'; $mod = Join-Path $managed 'Mods\HKMP'
Assert ((Hash (Join-Path $mod 'HollowConnect.dll')) -eq (Hash $payloadFile)) 'Installed payload differs'
$pointer = Join-Path $game '.HollowConnectBackups\latest.txt'; $pointerHash = Hash $pointer
$originalBackup = (Get-Content -LiteralPath $pointer -Raw).Trim()
Assert ((Get-Content -LiteralPath (Join-Path $originalBackup 'original-HKMP\old-mod.txt') -Raw) -eq 'original mod') 'Original mod was not backed up'
Pass 'EXE installs from an unrelated working directory with apostrophes and ampersands in both paths'
Write-Text (Join-Path $mod 'skins\custom.txt') 'keep player skin'
Write-Text (Join-Path $mod 'authorized.json') 'keep local server configuration'
Write-Text $payloadFile 'mod version two'; $manifest.version='fixture2'; $manifest.files[0].sha256=Hash $payloadFile
$manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $manifestPath -Encoding UTF8
$result = Invoke-Installer $package '--install' $game
Assert ($result.Code -eq 0) ('Update failed: ' + $result.Output)
Assert ((Hash $pointer) -eq $pointerHash) 'Update replaced the original uninstall backup'
Assert ((Hash (Join-Path $mod 'HollowConnect.dll')) -eq (Hash $payloadFile)) 'Update did not apply new payload'
Assert ((Get-Content -LiteralPath (Join-Path $mod 'skins\custom.txt') -Raw) -eq 'keep player skin') 'Update lost custom skin'
Assert ((Get-Content -LiteralPath (Join-Path $mod 'authorized.json') -Raw) -eq 'keep local server configuration') 'Update lost local settings'
Pass 'updates preserve the original uninstall backup and player skins/server configuration'
$beforeBackups = @(Get-ChildItem -LiteralPath (Join-Path $game '.HollowConnectBackups') -Directory).Count
$goodPayload = [IO.File]::ReadAllText($payloadFile); Write-Text $payloadFile 'tampered'
$result = Invoke-Installer $package '--install' $game
Assert ($result.Code -ne 0) 'Tampered payload accepted'
Assert (@(Get-ChildItem -LiteralPath (Join-Path $game '.HollowConnectBackups') -Directory).Count -eq $beforeBackups) 'Rejected package created a backup'
Assert ((Hash $pointer) -eq $pointerHash) 'Rejected package changed backup pointer'
Write-Text $payloadFile $goodPayload
Pass 'checksum failure rejects the package before modifying game files'

$installScript = Join-Path $package 'Install.ps1'
$originalScript = [IO.File]::ReadAllText($installScript)
$needle = "Copy-Item -LiteralPath (Join-Path `$package 'payload\HKMP') -Destination (Join-Path `$managed 'Mods') -Recurse -Force"
Assert ($originalScript.Contains($needle)) 'Failure injection point missing'
Write-Text $installScript ($originalScript.Replace($needle, "throw 'Injected installer failure'"))
$installedHash = Hash (Join-Path $mod 'HollowConnect.dll')
$result = Invoke-Installer $package '--install' $game
Assert ($result.Code -ne 0 -and $result.Output.Contains('Injected installer failure')) 'Injected update failure not reached'
Assert ((Hash (Join-Path $mod 'HollowConnect.dll')) -eq $installedHash -and (Hash $pointer) -eq $pointerHash) 'Failed update did not restore current version'
Assert (Test-Path -LiteralPath (Join-Path $mod 'skins\custom.txt')) 'Failed update lost skin'
Pass 'failed update restores the current mod and keeps the original backup pointer'
$failureGame = Join-Path $OutputRoot 'fresh failure game'; New-Game $failureGame
$result = Invoke-Installer $package '--install' $failureGame
Assert ($result.Code -ne 0 -and $result.Output.Contains('Injected installer failure')) 'Injected initial install failure not reached'
Assert ((Hash (Join-Path $failureGame 'hollow_knight_Data\Managed\Assembly-CSharp.dll')) -eq $manifest.vanillaAssemblySha256) 'Failed initial install changed assembly'
Assert (!(Test-Path -LiteralPath (Join-Path $failureGame 'hollow_knight_Data\Managed\Modding.dll'))) 'Failed initial install left new API dependency'
Assert (Test-Path -LiteralPath (Join-Path $failureGame 'hollow_knight_Data\Managed\Mods\HKMP\old-mod.txt')) 'Failed initial install lost original mod'
Assert (!(Test-Path -LiteralPath (Join-Path $failureGame '.HollowConnectBackups\latest.txt'))) 'Failed initial install published backup pointer'
Pass 'failed initial install restores original files and removes newly introduced API files'
$lockedGame = Join-Path $OutputRoot 'locked rollback game'; New-Game $lockedGame
$lockInjection = @'
$rollbackTestLock = [IO.File]::Open((Join-Path $managed 'Assembly-CSharp.dll'), [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
throw 'Injected installer failure with rollback lock'
'@
Write-Text $installScript ($originalScript.Replace($needle, $lockInjection))
$result = Invoke-Installer $package '--install' $lockedGame
Assert ($result.Code -ne 0 -and $result.Output.Contains('Injected installer failure with rollback lock')) 'Locked rollback did not preserve the original installation failure'
Assert ($result.Output.Contains('Recovery incomplete') -and $result.Output.Contains('Restore Assembly-CSharp.dll')) 'Locked rollback did not report the failed recovery step'
$lockedManaged = Join-Path $lockedGame 'hollow_knight_Data\Managed'
Assert (Test-Path -LiteralPath (Join-Path $lockedManaged 'Mods\HKMP\old-mod.txt')) 'Locked API rollback prevented original HKMP recovery'
Assert (!(Test-Path -LiteralPath (Join-Path $lockedManaged 'Modding.dll'))) 'Locked API rollback prevented cleanup of the next API file'
Assert (!(Test-Path -LiteralPath (Join-Path $lockedGame '.HollowConnectBackups\latest.txt'))) 'Incomplete recovery published the uninstall pointer'
$failedBackup = @(Get-ChildItem -LiteralPath (Join-Path $lockedGame '.HollowConnectBackups') -Directory)
Assert ($failedBackup.Count -eq 1) 'Expected one locked rollback backup'
Assert ((Hash (Join-Path $failedBackup[0].FullName 'original-managed\Assembly-CSharp.dll')) -eq $manifest.vanillaAssemblySha256) 'Locked recovery lost the original assembly backup'
$recoveryReport = [IO.File]::ReadAllText((Join-Path $failedBackup[0].FullName 'install-failure.txt'))
Assert ($recoveryReport.Contains('Injected installer failure with rollback lock') -and $recoveryReport.Contains($failedBackup[0].FullName) -and $recoveryReport.Contains('Restore Assembly-CSharp.dll')) 'Recovery report lacks the original error, backup location or failed file'
Pass 'a real file lock during API rollback still restores HKMP and other API files, and reports incomplete recovery'
Write-Text $installScript $originalScript
$result = Invoke-Installer $package '--uninstall' $game
Assert ($result.Code -eq 0) ('Uninstall failed: '+$result.Output)
Assert ((Hash (Join-Path $managed 'Assembly-CSharp.dll')) -eq $manifest.vanillaAssemblySha256) 'Uninstall did not restore original assembly'
Assert (!(Test-Path -LiteralPath (Join-Path $managed 'Modding.dll'))) 'Uninstall left new dependency'
Assert ((Get-Content -LiteralPath (Join-Path $mod 'old-mod.txt') -Raw) -eq 'original mod') 'Uninstall did not restore original mod'
Assert (!(Test-Path -LiteralPath (Join-Path $mod 'HollowConnect.dll'))) 'Uninstall left payload'
Pass 'EXE uninstall after updates restores the first installation baseline'

# Exercise the packaged EXE discovery/UTF-8 path channel without touching games.
$detected = Invoke-Installer $package '--detect' $null
Assert ($detected.Code -eq 0) 'EXE automatic detection failed'
Pass 'standalone EXE automatic discovery runs through Windows PowerShell without developer tools'
Write-Output "RESULT $script:passed passed, 0 failed"
Write-Output "Evidence: $OutputRoot"
