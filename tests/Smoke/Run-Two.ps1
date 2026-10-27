param([string]$RunName, [string]$Mode='full', [string]$EnginePath='', [switch]$NoBuild, [string]$PayloadPath='', [ValidateSet(2,3)][int]$Players=2)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if ($RunName -notmatch '^[a-z0-9][a-z0-9-]{0,95}$') { throw 'Invalid isolated run directory name.' }
$testRoot=Join-Path "$root\.test-results" $RunName
if (Test-Path -LiteralPath $testRoot) { throw 'Preserve existing evidence; choose a new run name.' }
if ($PayloadPath) {
    $PayloadPath=(Resolve-Path -LiteralPath $PayloadPath).Path
    foreach ($taskName in 'HKMP.dll','HollowConnect.dll','HollowConnect.Core.dll','BouncyCastle.Cryptography.dll') {
        if (!(Test-Path -LiteralPath (Join-Path $PayloadPath $taskName) -PathType Leaf)) { throw ('Pinned payload dependency missing: '+$taskName) }
    }
    if ($EnginePath) { throw 'Use either a pinned payload or a separate engine override.' }
}
if (!$NoBuild) {
    & (Join-Path $root 'scripts\Build.ps1') -Target Smoke
} else {
    foreach ($taskName in 'HKMP.dll','HollowConnect.dll','HollowConnect.Core.dll','BouncyCastle.Cryptography.dll','HollowConnect.Smoke.dll') {
        $taskPrepared=Join-Path $PSScriptRoot ('bin\Release\net472\'+$taskName)
        if (!(Test-Path -LiteralPath $taskPrepared -PathType Leaf)) { throw 'Prepared Smoke build missing; run scripts/Build.ps1 -Target Smoke first.' }
        if ($taskName -ne 'HollowConnect.Smoke.dll' -and !($EnginePath -and $taskName -eq 'HKMP.dll')) {
            $taskProduction=Join-Path $root ('src\HollowConnect\bin\Release\net472\'+$taskName)
            if ((Get-FileHash -LiteralPath $taskProduction).Hash -ne (Get-FileHash -LiteralPath $taskPrepared).Hash) { throw ('Prepared Smoke dependency is stale: '+$taskName) }
        }
    }
}
$taskIsolatedExecutables=@('host','guest') | ForEach-Object { [IO.Path]::GetFullPath((Join-Path $root ('.test-results/game-'+$_+'/hollow_knight.exe'))) }
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
$taskBinaries=@('HKMP.dll','HollowConnect.dll','HollowConnect.Core.dll') | ForEach-Object {
    $taskDllPath=if($PayloadPath){Join-Path $PayloadPath $_}elseif($_ -eq 'HKMP.dll' -and $EnginePath){$EnginePath}else{Join-Path $root ('tests/Smoke/bin/Release/net472/'+$_)}
    [pscustomobject]@{name=$_;sha256=(Get-FileHash -LiteralPath $taskDllPath -Algorithm SHA256).Hash}
}
$taskBinaries | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $testRoot 'binaries.json') -Encoding UTF8
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
    foreach ($dll in 'HKMP.dll','HollowConnect.dll','HollowConnect.Core.dll','BouncyCastle.Cryptography.dll','HollowConnect.Smoke.dll') {
        $taskDllPath=if($PayloadPath -and $dll -ne 'HollowConnect.Smoke.dll'){Join-Path $PayloadPath $dll}elseif($dll -eq 'HKMP.dll' -and $EnginePath){$EnginePath}else{"$root\tests\Smoke\bin\Release\net472\$dll"}
        Copy-Item -LiteralPath $taskDllPath -Destination "$gameDir\hollow_knight_Data\Managed\Mods\HKMP\$dll" -Force
    }
}
foreach ($testRole in $taskRoles) {
    $taskCopy=if($testRole -eq 'host'){'host'}else{'guest'}
    $gameDir=Join-Path "$root\.test-results" "game-$taskCopy"
    $env:HOLLOWCONNECT_SMOKE_ROLE=$testRole
    $taskArguments=@('-screen-fullscreen','0','-screen-width','800','-screen-height','600','-logFile',"$testRoot\$testRole-unity.log")
    if($env:HOLLOWCONNECT_RENDERED_TEST -ne '1'){$taskArguments=@('-batchmode')+$taskArguments}
    $testProcess=Start-Process -FilePath "$gameDir\hollow_knight.exe" -ArgumentList $taskArguments -WorkingDirectory $gameDir -WindowStyle Hidden -PassThru
    Set-Content -LiteralPath "$testRoot\$testRole.pid" -Value $testProcess.Id
    Write-Output "$testRole started: $($testProcess.Id)"
    if ($testRole -ne $taskRoles[-1]) {
        # Both API instances rotate the same native ModLog before plugins load.
        # Wait for the first probe to load before starting the second process;
        # simultaneous rotations can abort ModLoader with AccessDenied.
        $taskStartupDeadline=[DateTime]::UtcNow.AddSeconds(45)
        while (!(Test-Path -LiteralPath (Join-Path $testRoot ($testRole+'.log')))) {
            if ($testProcess.HasExited -or [DateTime]::UtcNow -ge $taskStartupDeadline) {
                throw ('Probe did not load; inspect '+(Join-Path $testRoot ($testRole+'-unity.log')))
            }
            Start-Sleep -Milliseconds 200
        }
    }
}
