param(
    [Parameter(Mandatory)][string]$Prefix,
    [string]$PayloadZip='',
    [string]$PayloadPath='',
    [string[]]$Only=@(),
    [switch]$NoBuild
)
$ErrorActionPreference='Stop'
$taskRoot=Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if ($Prefix -notmatch '^[a-z0-9][a-z0-9-]{0,65}$') { throw 'Invalid evidence prefix.' }
if ($PayloadPath) {
    if ($PayloadZip) { throw 'Choose a payload directory or release ZIP.' }
    $PayloadPath=(Resolve-Path -LiteralPath $PayloadPath).Path
} else {
    if (!$PayloadZip) { $PayloadZip=Join-Path $taskRoot 'dist/HollowConnect-0.1.27-windows.zip' }
    $PayloadZip=(Resolve-Path -LiteralPath $PayloadZip).Path
}
$taskCases=@(
    @{Name='direct-guest-200';Start='GG_Watcher_Knights';First='guest';DelayMs=200},
    @{Name='direct-guest-600';Start='GG_Watcher_Knights';First='guest';DelayMs=600},
    @{Name='unn-guest-500';Start='GG_Unn';First='guest';DelayMs=500},
    @{Name='grimm-guest-500';Start='GG_Grimm';First='guest';DelayMs=500},
    @{Name='direct-host-500';Start='GG_Watcher_Knights';First='host';DelayMs=500},
    @{Name='grimm-host-to-guest';Start='GG_Grimm';First='guest';PreviousFirst='host';DelayMs=500},
    @{Name='grimm-guest-to-host';Start='GG_Grimm';First='host';PreviousFirst='guest';DelayMs=500},
    @{Name='unn-host-to-guest';Start='GG_Unn';First='guest';PreviousFirst='host';DelayMs=500},
    @{Name='context-guards';Start='GG_Watcher_Knights';First='host';DelayMs=500;Checks='1';Experimental=$true},
    @{Name='grimm-host-to-guest-native';Start='GG_Grimm';First='guest';PreviousFirst='host';DelayMs=500;NativeReturn='1';Experimental=$true},
    @{Name='grimm-guest-to-host-native';Start='GG_Grimm';First='host';PreviousFirst='guest';DelayMs=500;NativeReturn='1';Experimental=$true}
)
foreach ($taskName in $Only) { if ($taskName -notin $taskCases.Name) { throw ('Unknown case '+$taskName) } }
if ($Only.Count) { $taskCases=@($taskCases | Where-Object {$_.Name -in $Only}) }
else { $taskCases=@($taskCases | Where-Object {!$_.Experimental}) }
$taskFixture=Join-Path $taskRoot ('.test-results/'+$Prefix+'-fixture')
if (Test-Path -LiteralPath $taskFixture) { throw 'Preserve existing evidence; choose a new prefix.' }
[IO.Directory]::CreateDirectory($taskFixture) | Out-Null
$taskSourcePaths=@('src/HollowConnect/BossTravel.cs','src/HKMP/Game/Client/Entity/BossRoomSynchronizer.cs','src/HKMP/Game/Client/Entity/EntityManager.cs','src/HKMP/Networking/EntitySnapshotHistory.cs','src/HKMP/Game/Server/ServerManager.cs')
$taskBefore=@($taskSourcePaths | ForEach-Object { [pscustomobject]@{path=$_;sha256=(Get-FileHash -LiteralPath (Join-Path $taskRoot $_) -Algorithm SHA256).Hash} })
$taskBefore | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $taskFixture 'production-source-before.json') -Encoding UTF8
if (!$NoBuild) { & (Join-Path $taskRoot 'scripts/Build.ps1') -Target Smoke -NoRestore }
$taskProbe=Join-Path $PSScriptRoot 'bin/Release/net472/HollowConnect.Smoke.dll'
if (!(Test-Path -LiteralPath $taskProbe -PathType Leaf)) { throw 'Prepared Smoke build missing; build Smoke first.' }
$taskNewestSource=Get-ChildItem -LiteralPath $PSScriptRoot -File | Where-Object { $_.Extension -in '.cs','.csproj' } | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
if ($taskNewestSource.LastWriteTimeUtc -gt (Get-Item -LiteralPath $taskProbe).LastWriteTimeUtc) { throw ('Prepared Smoke is older than source: '+$taskNewestSource.Name+'; build Smoke first.') }
if ($PayloadPath) {
    foreach ($taskName in 'HKMP.dll','HollowConnect.dll','HollowConnect.Core.dll','BouncyCastle.Cryptography.dll') {
        Copy-Item -LiteralPath (Join-Path $PayloadPath $taskName) -Destination (Join-Path $taskFixture $taskName)
    }
} else {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $taskArchive=[IO.Compression.ZipFile]::OpenRead($PayloadZip)
    try {
        foreach ($taskName in 'HKMP.dll','HollowConnect.dll','HollowConnect.Core.dll','BouncyCastle.Cryptography.dll') {
            $taskEntry=$taskArchive.GetEntry('payload/HKMP/'+$taskName)
            if (!$taskEntry) { throw ('Release entry missing: '+$taskName) }
            [IO.Compression.ZipFileExtensions]::ExtractToFile($taskEntry,(Join-Path $taskFixture $taskName),$false)
        }
    } finally { $taskArchive.Dispose() }
}
[ordered]@{package=$PayloadZip;sha256=$(if($PayloadZip){(Get-FileHash -LiteralPath $PayloadZip -Algorithm SHA256).Hash}else{$null});payloadDirectory=$PayloadPath;probeSha256=(Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'bin/Release/net472/HollowConnect.Smoke.dll') -Algorithm SHA256).Hash} |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $taskFixture 'origin.json') -Encoding UTF8
$taskKeys=@('HOLLOWCONNECT_P5_REPRO_FIRST','HOLLOWCONNECT_P5_REPRO_PREVIOUS_FIRST','HOLLOWCONNECT_P5_REPRO_START','HOLLOWCONNECT_P5_REPRO_DELAY_MS','HOLLOWCONNECT_P5_REPRO_NATIVE_RETURN','HOLLOWCONNECT_P5_CONTEXT_CHECK','HOLLOWCONNECT_NETWORK_FAULTS')
$taskOriginal=@{}; foreach ($taskKey in $taskKeys) { $taskOriginal[$taskKey]=[Environment]::GetEnvironmentVariable($taskKey) }
$taskResults=@()
try {
    foreach ($taskCase in $taskCases) {
        $taskRun=$Prefix+'-'+$taskCase.Name
        $taskPath=Join-Path $taskRoot ('.test-results/'+$taskRun)
        $env:HOLLOWCONNECT_P5_REPRO_FIRST=$taskCase.First
        $env:HOLLOWCONNECT_P5_REPRO_PREVIOUS_FIRST=if($taskCase.PreviousFirst){$taskCase.PreviousFirst}else{$taskCase.First}
        $env:HOLLOWCONNECT_P5_REPRO_NATIVE_RETURN=if($taskCase.NativeReturn){$taskCase.NativeReturn}else{'0'}
        $env:HOLLOWCONNECT_P5_CONTEXT_CHECK=if($taskCase.Checks){$taskCase.Checks}else{'0'}
        $env:HOLLOWCONNECT_P5_REPRO_START=$taskCase.Start
        $env:HOLLOWCONNECT_P5_REPRO_DELAY_MS=[string]$taskCase.DelayMs
        $env:HOLLOWCONNECT_NETWORK_FAULTS='0'
        $taskOutcome='incomplete'; $taskError=''; $taskStrict=$false
        try {
            & (Join-Path $PSScriptRoot 'Run-Two.ps1') -RunName $taskRun -Mode 'p5-repro' -NoBuild -PayloadPath $taskFixture
            $taskCase | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $taskPath 'case.json') -Encoding UTF8
            $taskDeadline=[DateTime]::UtcNow.AddSeconds(240)
            while (!(Test-Path -LiteralPath (Join-Path $taskPath 'host.done')) -or !(Test-Path -LiteralPath (Join-Path $taskPath 'guest.done'))) {
                if (@(Get-ChildItem -LiteralPath $taskPath -Filter '*.failed').Count) { Start-Sleep -Seconds 1; throw ('Probe failure: '+$taskRun) }
                if ([DateTime]::UtcNow -gt $taskDeadline) { throw ('Probe timeout: '+$taskRun) }
                Start-Sleep -Seconds 1
            }
            $taskOutcome='native-next-room-both'
        } catch { $taskError=$_.Exception.Message; $taskOutcome='needs-inspection' }
        finally {
            foreach ($taskRole in 'guest','host') {
                $taskPidPath=Join-Path $taskPath ($taskRole+'.pid')
                if (!(Test-Path -LiteralPath $taskPidPath)) { continue }
                $taskProcessId=[int](Get-Content -LiteralPath $taskPidPath)
                $taskProcess=Get-Process -Id $taskProcessId -ErrorAction SilentlyContinue
                $taskExe=[IO.Path]::GetFullPath((Join-Path $taskRoot ('.test-results/game-'+$taskRole+'/hollow_knight.exe')))
                if ($taskProcess -and [string]::Equals($taskProcess.Path,$taskExe,[StringComparison]::OrdinalIgnoreCase)) { Stop-Process -Id $taskProcessId -Force; $taskProcess.WaitForExit() }
            }
        }
        try { & (Join-Path $PSScriptRoot 'Assert-ProbeLogs.ps1') -RunPath $taskPath -RequireRuntimeFiles; $taskStrict=$true }
        catch { $taskError+='; strict logs: '+$_.Exception.Message }
        $taskBinaries=@(Get-Content -LiteralPath (Join-Path $taskPath 'binaries.json') -Raw | ConvertFrom-Json)
        foreach ($taskRole in 'host','guest') {
            foreach ($taskBinary in $taskBinaries) {
                $taskActual=Join-Path $taskRoot ('.test-results/game-'+$taskRole+'/hollow_knight_Data/Managed/Mods/HKMP/'+$taskBinary.name)
                if ((Get-FileHash -LiteralPath $taskActual -Algorithm SHA256).Hash -ne $taskBinary.sha256) { throw 'Isolated binary hash changed during reproduction.' }
            }
        }
        $taskTimes=@{}
        foreach ($taskRole in 'host','guest') {
            $taskTimePath=Join-Path $taskPath ($taskRole+'.watcher-active')
            if (Test-Path -LiteralPath $taskTimePath) { $taskTimes[$taskRole]=[DateTime]::Parse((Get-Content -LiteralPath $taskTimePath -Raw),[Globalization.CultureInfo]::InvariantCulture,[Globalization.DateTimeStyles]::RoundtripKind) }
        }
        $taskActualGap=if ($taskTimes.Count -eq 2) { ($taskTimes.host-$taskTimes.guest).TotalMilliseconds } else { $null }
        $taskResult=[ordered]@{name=$taskRun;outcome=$taskOutcome;strictLogs=$taskStrict;error=$taskError;hostMinusGuestWatcherEntryMs=$taskActualGap;binaries=$taskBinaries}
        $taskResult | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $taskPath 'reproduction-result.json') -Encoding UTF8
        $taskResults+=$taskResult
        Write-Output ($taskRun+' '+$taskOutcome+' strict='+$taskStrict+' entryGapMs='+$taskActualGap+' '+$taskError)
    }
} finally {
    foreach ($taskKey in $taskKeys) { [Environment]::SetEnvironmentVariable($taskKey,$taskOriginal[$taskKey]) }
    foreach ($taskSource in $taskBefore) {
        if ((Get-FileHash -LiteralPath (Join-Path $taskRoot $taskSource.path) -Algorithm SHA256).Hash -ne $taskSource.sha256) { throw 'Production source changed during reproduction.' }
    }
    $taskResults | ConvertTo-Json -Depth 7 | Set-Content -LiteralPath (Join-Path $taskFixture 'results.json') -Encoding UTF8
}
