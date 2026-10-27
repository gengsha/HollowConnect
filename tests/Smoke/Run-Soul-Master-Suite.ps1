param([Parameter(Mandatory)][string]$Prefix,[string[]]$Only=@(),[switch]$NoBuild)
$ErrorActionPreference='Stop'
$taskRoot=Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$taskCases=@(
    @{Name='outside-off-host';First='host';Share='0';Arrival='outside';Boundary='1'},
    @{Name='outside-on-host';First='host';Share='1';Arrival='outside'},
    @{Name='outside-off-guest';First='guest';Share='0';Arrival='outside'},
    @{Name='outside-on-guest';First='guest';Share='1';Arrival='outside'},
    @{Name='phase1-off-host';First='host';Share='0';Arrival='phase1'},
    @{Name='phase2-on-guest';First='guest';Share='1';Arrival='phase2'},
    @{Name='complete-off-guest';First='guest';Share='0';Arrival='complete'},
    @{Name='complete-on-host';First='host';Share='1';Arrival='complete'},
    @{Name='personal-fake-off-host';First='host';Share='0';Arrival='outside';SecondFake='1';Boundary='1'}
)
foreach($taskName in $Only){if($taskName -notin $taskCases.Name){throw ('Unknown Soul Master case '+$taskName)}}
if($Only.Count){$taskCases=@($taskCases|Where-Object {$_.Name -in $Only})}
$taskKeys=@('HOLLOWCONNECT_SOUL_MASTER_CHECK','HOLLOWCONNECT_SOUL_FIRST','HOLLOWCONNECT_SOUL_SHARED','HOLLOWCONNECT_SOUL_ARRIVAL','HOLLOWCONNECT_SOUL_SECOND_FAKE','HOLLOWCONNECT_LOCAL_BARRIER_REPLAY_CHECK','HOLLOWCONNECT_NETWORK_FAULTS')
$taskOriginal=@{};foreach($taskKey in $taskKeys){$taskOriginal[$taskKey]=[Environment]::GetEnvironmentVariable($taskKey)}
try {
    if(!$NoBuild){& (Join-Path $taskRoot 'scripts/Build.ps1') -Target Smoke -NoRestore}
    foreach($taskCase in $taskCases){
        $taskName=$Prefix+'-'+$taskCase.Name
        $taskPath=Join-Path $taskRoot ('.test-results/'+$taskName)
        $env:HOLLOWCONNECT_SOUL_MASTER_CHECK='verify';$env:HOLLOWCONNECT_SOUL_FIRST=$taskCase.First
        $env:HOLLOWCONNECT_SOUL_SHARED=$taskCase.Share;$env:HOLLOWCONNECT_SOUL_ARRIVAL=$taskCase.Arrival
        $env:HOLLOWCONNECT_SOUL_SECOND_FAKE=$taskCase.SecondFake
        $env:HOLLOWCONNECT_LOCAL_BARRIER_REPLAY_CHECK=$taskCase.Boundary;$env:HOLLOWCONNECT_NETWORK_FAULTS='0'
        & (Join-Path $PSScriptRoot 'Run-Two.ps1') -RunName $taskName -Mode rescue -NoBuild
        $taskCase|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $taskPath 'case.json') -Encoding UTF8
        (Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'bin/Release/net472/HollowConnect.Smoke.dll')).Hash|Set-Content -LiteralPath (Join-Path $taskPath 'smoke.sha256')
        try {
            $taskDeadline=[DateTime]::UtcNow.AddSeconds(250)
            while(!(Test-Path -LiteralPath (Join-Path $taskPath 'host.done')) -or !(Test-Path -LiteralPath (Join-Path $taskPath 'guest.done'))){
                if(@(Get-ChildItem -LiteralPath $taskPath -Filter '*.failed').Count){throw ('Soul Master failed '+$taskName)}
                if([DateTime]::UtcNow -gt $taskDeadline){throw ('Soul Master timed out '+$taskName)}
                Start-Sleep -Seconds 1
            }
        } finally {
            foreach($taskRole in 'host','guest'){
                $taskPid=[int](Get-Content -LiteralPath (Join-Path $taskPath ($taskRole+'.pid')))
                $taskProcess=Get-Process -Id $taskPid -ErrorAction SilentlyContinue
                $taskExe=Join-Path $taskRoot ('.test-results/game-'+$taskRole+'/hollow_knight.exe')
                if($taskProcess -and [string]::Equals($taskProcess.Path,$taskExe,[StringComparison]::OrdinalIgnoreCase)){Stop-Process -Id $taskPid -Force;$taskProcess.WaitForExit()}
            }
        }
        & (Join-Path $PSScriptRoot 'Assert-ProbeLogs.ps1') -RunPath $taskPath -RequireRuntimeFiles
        'PASS: both native pickups, control recovery and physical gate passage; strict runtime logs.'|Set-Content -LiteralPath (Join-Path $taskPath 'strict-pass.txt')
        Write-Output ('PASS '+$taskName)
    }
} finally {foreach($taskKey in $taskKeys){[Environment]::SetEnvironmentVariable($taskKey,$taskOriginal[$taskKey])}}
