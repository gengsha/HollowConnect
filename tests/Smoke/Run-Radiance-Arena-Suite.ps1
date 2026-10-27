param([Parameter(Mandatory)][string]$Prefix, [string[]]$Only = @(), [switch]$NoBuild,
    [ValidateSet('phase-two','before-challenge')][string]$ArrivalStage='phase-two')
$ErrorActionPreference = 'Stop'
$taskRepo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$taskCases = @(
    @{ Name='statue-f8-host'; Scene='GG_Radiance'; Authority='host'; Entry='f8'; P5='0'; Faults='0' },
    @{ Name='statue-native-guest'; Scene='GG_Radiance'; Authority='guest'; Entry='native'; P5='0'; Faults='0' },
    @{ Name='p5-f8-guest-fault'; Scene='GG_Radiance'; Authority='guest'; Entry='f8'; P5='1'; Faults='1' },
    @{ Name='p5-native-host'; Scene='GG_Radiance'; Authority='host'; Entry='native'; P5='1'; Faults='0' },
    @{ Name='story-f8-host'; Scene='Dream_Final_Boss'; Authority='host'; Entry='f8'; P5='0'; Faults='0' },
    @{ Name='story-native-guest'; Scene='Dream_Final_Boss'; Authority='guest'; Entry='native'; P5='0'; Faults='0' }
)
if ($ArrivalStage -eq 'before-challenge') {
    $taskCases = @(
        @{ Name='story-f8-host'; Scene='Dream_Final_Boss'; Authority='host'; Entry='f8'; P5='0'; Faults='0' },
        @{ Name='story-native-host'; Scene='Dream_Final_Boss'; Authority='host'; Entry='native'; P5='0'; Faults='0' },
        @{ Name='story-f8-guest'; Scene='Dream_Final_Boss'; Authority='guest'; Entry='f8'; P5='0'; Faults='0' },
        @{ Name='story-native-guest'; Scene='Dream_Final_Boss'; Authority='guest'; Entry='native'; P5='0'; Faults='0' }
    )
}
if ($Only.Count) {
    foreach ($taskName in $Only) { if ($taskName -notin $taskCases.Name) { throw "Unknown case: $taskName" } }
    $taskCases = @($taskCases | Where-Object { $_.Name -in $Only })
}
$taskKeys = @('HOLLOWCONNECT_RADIANCE_FRESH_LATE','HOLLOWCONNECT_RADIANCE_FRESH_SCENE',
    'HOLLOWCONNECT_RADIANCE_FRESH_ENTRY','HOLLOWCONNECT_RADIANCE_FRESH_P5',
    'HOLLOWCONNECT_COVERAGE_AUTHORITY','HOLLOWCONNECT_NETWORK_FAULTS','HOLLOWCONNECT_RADIANCE_SUITE_EXIT',
    'HOLLOWCONNECT_RADIANCE_ENTRY_LAYOUT','HOLLOWCONNECT_RADIANCE_CAPTURE')
$taskOriginal = @{}
foreach ($taskKey in $taskKeys) { $taskOriginal[$taskKey] = [Environment]::GetEnvironmentVariable($taskKey) }
try {
    if (!$NoBuild) { & (Join-Path $taskRepo 'scripts/Build.ps1') -Target Smoke -NoRestore }
    foreach ($taskCase in $taskCases) {
        $taskRun = $Prefix + '-' + $taskCase.Name
        $taskPath = Join-Path $taskRepo ('.test-results/' + $taskRun)
        [Environment]::SetEnvironmentVariable('HOLLOWCONNECT_RADIANCE_FRESH_LATE','1')
        [Environment]::SetEnvironmentVariable('HOLLOWCONNECT_RADIANCE_ENTRY_LAYOUT',$(if ($ArrivalStage -eq 'before-challenge') {'1'}else{'0'}))
        [Environment]::SetEnvironmentVariable('HOLLOWCONNECT_RADIANCE_CAPTURE','1')
        [Environment]::SetEnvironmentVariable('HOLLOWCONNECT_RADIANCE_SUITE_EXIT','1')
        [Environment]::SetEnvironmentVariable('HOLLOWCONNECT_RADIANCE_FRESH_SCENE',$taskCase.Scene)
        [Environment]::SetEnvironmentVariable('HOLLOWCONNECT_RADIANCE_FRESH_ENTRY',$taskCase.Entry)
        [Environment]::SetEnvironmentVariable('HOLLOWCONNECT_RADIANCE_FRESH_P5',$taskCase.P5)
        [Environment]::SetEnvironmentVariable('HOLLOWCONNECT_COVERAGE_AUTHORITY',$taskCase.Authority)
        [Environment]::SetEnvironmentVariable('HOLLOWCONNECT_NETWORK_FAULTS',$taskCase.Faults)
        & (Join-Path $PSScriptRoot 'Run-Two.ps1') -RunName $taskRun -Mode coverage -NoBuild
        $taskCase | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $taskPath 'case.json') -Encoding UTF8
        Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'bin/Release/net472/HollowConnect.Smoke.dll') |
            Select-Object Hash | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $taskPath 'probe-binary.json') -Encoding UTF8
        $taskDeadline = [DateTime]::UtcNow.AddSeconds(240)
        try {
            while (!(Test-Path -LiteralPath (Join-Path $taskPath 'host.done')) -or !(Test-Path -LiteralPath (Join-Path $taskPath 'guest.done'))) {
                if (@(Get-ChildItem -LiteralPath $taskPath -Filter '*.failed').Count) { throw "Probe failed: $taskRun" }
                if ([DateTime]::UtcNow -gt $taskDeadline) { throw "Probe timed out: $taskRun" }
                Start-Sleep -Seconds 1
            }
            'graceful transport teardown' | Set-Content -LiteralPath (Join-Path $taskPath 'shutdown.requested')
            $taskExitBy = [DateTime]::UtcNow.AddSeconds(25)
            foreach ($taskRole in 'guest','host') {
                $taskProcess = Get-Process -Id ([int](Get-Content -LiteralPath (Join-Path $taskPath ($taskRole + '.pid')))) -ErrorAction SilentlyContinue
                while ($taskProcess -and !$taskProcess.HasExited -and [DateTime]::UtcNow -lt $taskExitBy) { Start-Sleep -Seconds 1 }
                if ($taskProcess -and !$taskProcess.HasExited) { throw "Graceful teardown timed out: $taskRun" }
            }
        } finally {
            foreach ($taskRole in 'guest','host') {
                $taskPid = [int](Get-Content -LiteralPath (Join-Path $taskPath ($taskRole + '.pid')))
                $taskProcess = Get-Process -Id $taskPid -ErrorAction SilentlyContinue
                if ($taskProcess -and $taskProcess.Path.StartsWith((Join-Path $taskRepo '.test-results/game-'))) {
                    Stop-Process -Id $taskPid -Force -ErrorAction SilentlyContinue
                    $taskProcess.WaitForExit()
                }
            }
        }
        & (Join-Path $PSScriptRoot 'Assert-ProbeLogs.ps1') -RunPath $taskPath -RequireRuntimeFiles
        'PASS: both endpoints done and strict logs accepted.' | Set-Content -LiteralPath (Join-Path $taskPath 'strict-pass.txt')
        Write-Output "PASS $taskRun"
    }
} finally {
    foreach ($taskKey in $taskKeys) { [Environment]::SetEnvironmentVariable($taskKey,$taskOriginal[$taskKey]) }
}
