param([string]$RunName, [string]$Mode='full', [string]$EnginePath='', [switch]$NoBuild, [string]$PayloadPath='', [ValidateSet(2,3)][int]$Players=2, [switch]$SteamModule, [string]$FrameTracePath='', [ValidateSet('Legacy','Unity6')][string]$GameVersion='Legacy')
$ErrorActionPreference='Stop'
$root=Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if ($RunName -notmatch '^[a-z0-9][a-z0-9-]{0,95}$') { throw 'Invalid isolated run directory name.' }
$taskOutput=if($GameVersion -eq 'Unity6'){'bin/Unity6/Release/net472'}else{'bin/Release/net472'}
$testRoot=Join-Path "$root\.test-results" $RunName
if (Test-Path -LiteralPath $testRoot) { throw 'Preserve existing evidence; choose a new run name.' }
if ($FrameTracePath) {
    $FrameTracePath=(Resolve-Path -LiteralPath $FrameTracePath).Path
    if (!(Test-Path -LiteralPath $FrameTracePath -PathType Leaf) -or [IO.Path]::GetFileName($FrameTracePath) -ne 'HollowConnect.FrameTrace.dll') { throw 'Expected the standalone HollowConnect.FrameTrace.dll.' }
}
if ($PayloadPath) {
    $PayloadPath=(Resolve-Path -LiteralPath $PayloadPath).Path
    foreach ($taskName in 'HKMP.dll','HollowConnect.dll','HollowConnect.Core.dll','BouncyCastle.Cryptography.dll') {
        if (!(Test-Path -LiteralPath (Join-Path $PayloadPath $taskName) -PathType Leaf)) { throw ('Pinned payload dependency missing: '+$taskName) }
    }
    if ($EnginePath) { throw 'Use either a pinned payload or a separate engine override.' }
}
if (!$NoBuild) {
    & (Join-Path $root 'scripts\Build.ps1') -Target Smoke -GameVersion $GameVersion
} else {
    foreach ($taskName in 'HKMP.dll','HollowConnect.dll','HollowConnect.Core.dll','BouncyCastle.Cryptography.dll','HollowConnect.Smoke.dll') {
        $taskPrepared=Join-Path $PSScriptRoot ($taskOutput+'/'+$taskName)
        if (!(Test-Path -LiteralPath $taskPrepared -PathType Leaf)) { throw 'Prepared Smoke build missing; run scripts/Build.ps1 -Target Smoke -GameVersion $GameVersion first.' }
        if ($taskName -ne 'HollowConnect.Smoke.dll' -and !($EnginePath -and $taskName -eq 'HKMP.dll')) {
            $taskProduction=Join-Path $root ('src/HollowConnect/'+$taskOutput+'/'+$taskName)
            if ((Get-FileHash -LiteralPath $taskProduction).Hash -ne (Get-FileHash -LiteralPath $taskPrepared).Hash) { throw ('Prepared Smoke dependency is stale: '+$taskName) }
        }
    }
}
$taskIsolatedExecutables=@('host','guest') | ForEach-Object { [IO.Path]::GetFullPath((Join-Path $root ('.test-results/game-'+$_+'/hollow_knight.exe'))) }
foreach($taskRole in 'host','guest') {
    $taskGame=Join-Path $root ".test-results/game-$taskRole"
    $taskUnityVersion=[Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $taskGame 'UnityPlayer.dll')).ProductVersion
    if(($GameVersion -eq 'Unity6') -ne $taskUnityVersion.StartsWith('6000.')) { throw 'Selected build profile does not match the isolated game engine.' }
}
foreach ($p in Get-Process -Name hollow_knight -ErrorAction SilentlyContinue) {
    if ($p.Path -and $p.Path -in $taskIsolatedExecutables) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue; $p.WaitForExit() }
}
# Language initialization writes Config.ini. Test copies must not share its
# hardlink, or two clients can lock the same file during simultaneous startup.
foreach ($testRole in 'host','guest') {
    $config=Join-Path $root ".test-results\game-$testRole\hollow_knight_Data\Config.ini"
    Copy-Item -LiteralPath $config -Destination ($config+'.isolated') -Force
    Remove-Item -LiteralPath $config
    Move-Item -LiteralPath ($config+'.isolated') -Destination $config
}
if ([IO.Path]::GetFullPath($testRoot) -notlike ($root+'\.test-results\*')) { throw 'Run directory escapes the isolated workspace.' }
New-Item -ItemType Directory -Path $testRoot | Out-Null
$taskProductionNames=@('HKMP.dll','HollowConnect.dll','HollowConnect.Core.dll')
if($SteamModule){
    if($EnginePath){throw 'Steam module checks require a complete matched production payload.'}
    $taskSteamPrepared=if($PayloadPath){Join-Path $PayloadPath 'HollowConnect.Steam.dll'}else{Join-Path $PSScriptRoot "$taskOutput/HollowConnect.Steam.dll"}
    $taskSteamProduction=Join-Path $root "src/HollowConnect.Steam/$taskOutput/HollowConnect.Steam.dll"
    if(!(Test-Path -LiteralPath $taskSteamPrepared) -or
        (!$PayloadPath -and (Get-FileHash -LiteralPath $taskSteamPrepared).Hash -ne (Get-FileHash -LiteralPath $taskSteamProduction).Hash)){throw 'Prepared Steam module is missing or stale.'}
    $taskProductionNames+='HollowConnect.Steam.dll'
}
if($GameVersion -eq 'Unity6' -and !$SteamModule){
    # Unity 6 scans every loaded assembly's types for fade bridges. Smoke has
    # optional Steam fixture types, so its dependency must resolve even when
    # the Steam mod is not enabled. Managed/ resolves it without ModLoader
    # discovering/initializing an optional Mod in the Mods directory.
    $taskSteamPrepared=Join-Path $PSScriptRoot "$taskOutput/HollowConnect.Steam.dll"
    $taskSteamProduction=Join-Path $root "src/HollowConnect.Steam/$taskOutput/HollowConnect.Steam.dll"
    if((Get-FileHash -LiteralPath $taskSteamPrepared).Hash -ne (Get-FileHash -LiteralPath $taskSteamProduction).Hash){throw 'Prepared fixture Steam dependency is stale.'}
    $taskProductionNames+='HollowConnect.Steam.dll'
}
$taskBinaries=$taskProductionNames | ForEach-Object {
    $taskDllPath=if($PayloadPath){Join-Path $PayloadPath $_}elseif($_ -eq 'HKMP.dll' -and $EnginePath){$EnginePath}else{Join-Path $root ('tests/Smoke/'+$taskOutput+'/'+$_)}
    [pscustomobject]@{name=$_;sha256=(Get-FileHash -LiteralPath $taskDllPath -Algorithm SHA256).Hash}
}
if ($FrameTracePath) { $taskBinaries=@($taskBinaries)+@([pscustomobject]@{name='HollowConnect.FrameTrace.dll';sha256=(Get-FileHash -LiteralPath $FrameTracePath).Hash}) }
$taskBinaries | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $testRoot 'binaries.json') -Encoding UTF8
$taskGameFiles=@('UnityPlayer.dll','hollow_knight_Data/Managed/Assembly-CSharp.dll','hollow_knight_Data/Managed/TeamCherry.TK2D.dll','hollow_knight_Data/Managed/MonoMod.RuntimeDetour.dll')
@('host','guest') | ForEach-Object {
    $taskRole=$_
    foreach($taskFile in $taskGameFiles){
        $taskPath=Join-Path $root ".test-results/game-$taskRole/$taskFile"
        if(Test-Path -LiteralPath $taskPath){[pscustomobject]@{role=$taskRole;file=$taskFile;sha256=(Get-FileHash -LiteralPath $taskPath).Hash}}
    }
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $testRoot 'game-dependencies.json')
$env:SteamAppId='367520'
$env:SteamGameId='367520'
$env:HOLLOWCONNECT_SMOKE_ROOT=$testRoot
$env:HOLLOWCONNECT_SMOKE_MODE=$Mode
$taskRoles=if($Players -eq 3){@('host','guest','third')}else{@('host','guest')}
$env:HOLLOWCONNECT_SMOKE_ROLES=$taskRoles -join ','
# Prepare both copies before starting any process: guest and third share the
# read-only game directory, while all probe state, identity and logs are per role.
foreach ($testRole in 'host','guest') {
    $gameDir=Join-Path "$root\.test-results" "game-$testRole"
    if($GameVersion -eq 'Unity6' -and !$SteamModule){
        Copy-Item -LiteralPath $taskSteamPrepared -Destination "$gameDir/hollow_knight_Data/Managed/HollowConnect.Steam.dll" -Force
    }
    elseif($SteamModule -and (Test-Path -LiteralPath "$gameDir/hollow_knight_Data/Managed/HollowConnect.Steam.dll")){
        Move-Item -LiteralPath "$gameDir/hollow_knight_Data/Managed/HollowConnect.Steam.dll" -Destination (Join-Path $testRoot ($testRole+'-Steam-fixture-dependency.inactive'))
    }
    foreach ($dll in 'HKMP.dll','HollowConnect.dll','HollowConnect.Core.dll','BouncyCastle.Cryptography.dll','HollowConnect.Smoke.dll') {
        $taskDllPath=if($PayloadPath -and $dll -ne 'HollowConnect.Smoke.dll'){Join-Path $PayloadPath $dll}elseif($dll -eq 'HKMP.dll' -and $EnginePath){$EnginePath}else{"$root/tests/Smoke/$taskOutput/$dll"}
        Copy-Item -LiteralPath $taskDllPath -Destination "$gameDir\hollow_knight_Data\Managed\Mods\HKMP\$dll" -Force
    }
    $taskSteamIsolated=Join-Path $gameDir 'hollow_knight_Data/Managed/Mods/HKMP/HollowConnect.Steam.dll'
    if($SteamModule){Copy-Item -LiteralPath $taskSteamPrepared -Destination $taskSteamIsolated -Force}
    elseif(Test-Path -LiteralPath $taskSteamIsolated){
        # Preserve the optional binary while keeping ordinary fixture runs
        # free of an unrecorded module left by an earlier Steam check.
        Move-Item -LiteralPath $taskSteamIsolated -Destination (Join-Path $testRoot ($testRole+'-HollowConnect.Steam.dll.inactive'))
    }
    $taskTraceDir=Join-Path $gameDir 'hollow_knight_Data/Managed/Mods/HollowConnect.FrameTrace'
    $taskTraceDll=Join-Path $taskTraceDir 'HollowConnect.FrameTrace.dll'
    if($FrameTracePath){
        New-Item -ItemType Directory -Path $taskTraceDir -Force | Out-Null
        Copy-Item -LiteralPath $FrameTracePath -Destination $taskTraceDll -Force
    } elseif(Test-Path -LiteralPath $taskTraceDll){
        Move-Item -LiteralPath $taskTraceDll -Destination (Join-Path $testRoot ($testRole+'-HollowConnect.FrameTrace.dll.inactive'))
    }
}
foreach ($testRole in $taskRoles) {
    $taskCopy=if($testRole -eq 'host'){'host'}else{'guest'}
    $gameDir=Join-Path "$root\.test-results" "game-$taskCopy"
    $env:HOLLOWCONNECT_SMOKE_ROLE=$testRole
    $taskArguments=@('-screen-fullscreen','0','-screen-width','800','-screen-height','600','-logFile',"$testRoot\$testRole-unity.log")
    if($env:HOLLOWCONNECT_RENDERED_TEST -ne '1'){$taskArguments=@('-batchmode')+$taskArguments}
    $taskWindowStyle=if($env:HOLLOWCONNECT_HUD_AUDIT -eq '1' -or $env:HOLLOWCONNECT_RENDERED_TEST -eq '1'){'Normal'}else{'Hidden'}
    $testProcess=Start-Process -FilePath "$gameDir\hollow_knight.exe" -ArgumentList $taskArguments -WorkingDirectory $gameDir -WindowStyle $taskWindowStyle -PassThru
    Set-Content -LiteralPath "$testRoot\$testRole.pid" -Value $testProcess.Id
    Write-Output "$testRole started: $($testProcess.Id)"
    if ($testRole -ne $taskRoles[-1]) {
        # Both API instances rotate the same native ModLog before plugins load.
        # Wait for the first probe to load before starting the second process;
        # simultaneous rotations can abort ModLoader with AccessDenied.
        $taskStartupDeadline=[DateTime]::UtcNow.AddSeconds(120)
        while (!(Test-Path -LiteralPath (Join-Path $testRoot ($testRole+'.log')))) {
            if ($testProcess.HasExited -or [DateTime]::UtcNow -ge $taskStartupDeadline) {
                throw ('Probe did not load; inspect '+(Join-Path $testRoot ($testRole+'-unity.log')))
            }
            Start-Sleep -Milliseconds 200
        }
    }
}
