param([Parameter(Mandatory)][ValidatePattern('^[a-z0-9][a-z0-9-]{0,50}$')][string]$Prefix,
    [string[]]$Only=@(),[switch]$NoBuild)
$ErrorActionPreference='Stop'
$taskRoot=Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$taskCases=@(
    @{Name='boundary';Mode='rescue';Env=@{HOLLOWCONNECT_LOCAL_BATTLE_CHECK='boundary';HOLLOWCONNECT_LOCAL_BATTLE_AUTHORITY='host'}},
    @{Name='reentry-host';Mode='rescue';Env=@{HOLLOWCONNECT_LOCAL_BATTLE_CHECK='reentry';HOLLOWCONNECT_LOCAL_BATTLE_AUTHORITY='host'}},
    @{Name='reentry-guest';Mode='rescue';Env=@{HOLLOWCONNECT_LOCAL_BATTLE_CHECK='reentry';HOLLOWCONNECT_LOCAL_BATTLE_AUTHORITY='guest'}},
    @{Name='disconnect-host';Mode='rescue';Env=@{HOLLOWCONNECT_LOCAL_BATTLE_CHECK='disconnect';HOLLOWCONNECT_LOCAL_BATTLE_AUTHORITY='host'}},
    @{Name='disconnect-guest';Mode='rescue';Env=@{HOLLOWCONNECT_LOCAL_BATTLE_CHECK='disconnect';HOLLOWCONNECT_LOCAL_BATTLE_AUTHORITY='guest'}},
    @{Name='mother-host';Mode='rescue';Env=@{HOLLOWCONNECT_LOCAL_BATTLE_CHECK='mother';HOLLOWCONNECT_LOCAL_BATTLE_AUTHORITY='host'}},
    @{Name='mother-guest';Mode='rescue';Env=@{HOLLOWCONNECT_LOCAL_BATTLE_CHECK='mother';HOLLOWCONNECT_LOCAL_BATTLE_AUTHORITY='guest'}},
    @{Name='phase-handoff';Mode='boss';Env=@{}},
    @{Name='phase-late-handoff';Mode='boss';Env=@{HOLLOWCONNECT_SMOKE_BOSS_LATE='1';HOLLOWCONNECT_SMOKE_HANDOFF_DELAY='2.5'}},
    @{Name='waves-host';Mode='colosseum';Env=@{HOLLOWCONNECT_COLOSSEUM_CASE='victory';HOLLOWCONNECT_COLOSSEUM_FIRST='host';HOLLOWCONNECT_COLOSSEUM_TRIAL='Bronze';HOLLOWCONNECT_COLOSSEUM_SHARE='1'}},
    @{Name='waves-guest';Mode='colosseum';Env=@{HOLLOWCONNECT_COLOSSEUM_CASE='victory';HOLLOWCONNECT_COLOSSEUM_FIRST='guest';HOLLOWCONNECT_COLOSSEUM_TRIAL='Bronze';HOLLOWCONNECT_COLOSSEUM_SHARE='1'}}
)
foreach($taskName in $Only){if($taskName -notin $taskCases.Name){throw ('Unknown case '+$taskName)}}
if($Only.Count){$taskCases=@($taskCases | Where-Object {$_.Name -in $Only})}
$taskOriginal=@{}
foreach($taskEnv in Get-ChildItem Env: | Where-Object {$_.Name -like 'HOLLOWCONNECT_*'}){$taskOriginal[$taskEnv.Name]=$taskEnv.Value}
try {
    if(!$NoBuild){& (Join-Path $taskRoot 'scripts/Build.ps1') -Target Smoke -NoRestore}
    foreach($taskCase in $taskCases) {
        foreach($taskEnv in @(Get-ChildItem Env: | Where-Object {$_.Name -like 'HOLLOWCONNECT_*'})){
            [Environment]::SetEnvironmentVariable($taskEnv.Name,$null)
        }
        foreach($taskKey in $taskCase.Env.Keys){[Environment]::SetEnvironmentVariable($taskKey,$taskCase.Env[$taskKey])}
        $env:HOLLOWCONNECT_NETWORK_FAULTS='0'
        $taskRun=$Prefix+'-'+$taskCase.Name
        $taskPath=Join-Path $taskRoot ('.test-results/'+$taskRun)
        & (Join-Path $PSScriptRoot 'Run-Two.ps1') -RunName $taskRun -Mode $taskCase.Mode -NoBuild
        $taskCase | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $taskPath 'case.json') -Encoding UTF8
        (Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'bin/Release/net472/HollowConnect.Smoke.dll')).Hash |
            Set-Content -LiteralPath (Join-Path $taskPath 'probe-sha256.txt')
        try {
            $taskDeadline=[DateTime]::UtcNow.AddSeconds(270)
            while(!(Test-Path -LiteralPath (Join-Path $taskPath 'host.done')) -or !(Test-Path -LiteralPath (Join-Path $taskPath 'guest.done'))){
                if(@(Get-ChildItem -LiteralPath $taskPath -Filter '*.failed').Count){throw ('Probe failed '+$taskRun)}
                if([DateTime]::UtcNow -gt $taskDeadline){throw ('Probe timed out '+$taskRun)}
                Start-Sleep -Seconds 1
            }
        } finally {
            foreach($taskRole in 'guest','host') {
                $taskPid=[int](Get-Content -LiteralPath (Join-Path $taskPath ($taskRole+'.pid')))
                $taskProcess=Get-Process -Id $taskPid -ErrorAction SilentlyContinue
                $taskExe=[IO.Path]::GetFullPath((Join-Path $taskRoot ('.test-results/game-'+$taskRole+'/hollow_knight.exe')))
                if($taskProcess -and [string]::Equals($taskProcess.Path,$taskExe,[StringComparison]::OrdinalIgnoreCase)){
                    Stop-Process -Id $taskPid -Force; $taskProcess.WaitForExit()
                }
            }
        }
        & (Join-Path $PSScriptRoot 'Assert-ProbeLogs.ps1') -RunPath $taskPath -RequireRuntimeFiles
        foreach($taskBinary in (Get-Content -LiteralPath (Join-Path $taskPath 'binaries.json') -Raw | ConvertFrom-Json)) {
            $taskProduction=Join-Path $taskRoot ('src/HollowConnect/bin/Release/net472/'+$taskBinary.name)
            if((Get-FileHash -LiteralPath $taskProduction).Hash -ne $taskBinary.sha256){throw ('Production hash mismatch '+$taskBinary.name)}
        }
        'PASS: both endpoints, strict logs and current production hashes.' | Set-Content -LiteralPath (Join-Path $taskPath 'strict-pass.txt')
        Write-Output ('PASS '+$taskRun)
    }
}finally{
    foreach($taskEnv in @(Get-ChildItem Env: | Where-Object {$_.Name -like 'HOLLOWCONNECT_*'})){[Environment]::SetEnvironmentVariable($taskEnv.Name,$null)}
    foreach($taskKey in $taskOriginal.Keys){[Environment]::SetEnvironmentVariable($taskKey,$taskOriginal[$taskKey])}
}
