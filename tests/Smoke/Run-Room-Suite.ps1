param([switch]$ReuseNativeHost,[string]$StartAt,[string[]]$Only)
$ErrorActionPreference='Stop'
$taskRoot=Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$taskCases=@(
    @{Name='final-native-host-013';Mode='pantheon';Existing=[bool]$ReuseNativeHost;Env=@{HOLLOWCONNECT_NATIVE_ENTRANCE='1';HOLLOWCONNECT_PANTHEON_AUTHORITY='host';HOLLOWCONNECT_CAMERA_RING='1'}},
    @{Name='final-native-guest-013';Mode='pantheon';Env=@{HOLLOWCONNECT_NATIVE_ENTRANCE='1';HOLLOWCONNECT_PANTHEON_AUTHORITY='guest';HOLLOWCONNECT_CAMERA_RING='1'}},
    @{Name='final-lifecycle-host-013';Mode='pantheon';Env=@{HOLLOWCONNECT_LIFECYCLE_CHECK='1';HOLLOWCONNECT_FSM_CONTINUATION_CHECK='1';HOLLOWCONNECT_FSM_SNAPSHOT_CHECK='1';HOLLOWCONNECT_FSM_HIERARCHY_CHECK='1';HOLLOWCONNECT_FSM_ENTRY_CHECK='1';HOLLOWCONNECT_FSM_CONTINUATION_STRESS='1';HOLLOWCONNECT_PANTHEON_AUTHORITY='host';HOLLOWCONNECT_COLLIDER_RECONNECT_STRESS='1';HOLLOWCONNECT_STATIC_IDENTITY_CHECK='1';HOLLOWCONNECT_PAUSE_MOTION_CHECK='1';HOLLOWCONNECT_TWEEN_CLONE_CHECK='1'}},
    @{Name='final-lifecycle-guest-013';Mode='pantheon';Env=@{HOLLOWCONNECT_LIFECYCLE_CHECK='1';HOLLOWCONNECT_FSM_CONTINUATION_CHECK='1';HOLLOWCONNECT_FSM_SNAPSHOT_CHECK='1';HOLLOWCONNECT_FSM_HIERARCHY_CHECK='1';HOLLOWCONNECT_FSM_ENTRY_CHECK='1';HOLLOWCONNECT_FSM_CONTINUATION_STRESS='1';HOLLOWCONNECT_PANTHEON_AUTHORITY='guest';HOLLOWCONNECT_COLLIDER_RECONNECT_STRESS='1';HOLLOWCONNECT_STATIC_IDENTITY_CHECK='1';HOLLOWCONNECT_PAUSE_MOTION_CHECK='1';HOLLOWCONNECT_TWEEN_CLONE_CHECK='1'}},
    @{Name='final-boss-013';Mode='boss';Env=@{}},
    @{Name='final-boss-late-013';Mode='boss';Env=@{HOLLOWCONNECT_SMOKE_BOSS_LATE='1';HOLLOWCONNECT_SMOKE_HANDOFF_DELAY='2.5'}},
    @{Name='final-grimm-host-013';Mode='grimm-motion';Env=@{HOLLOWCONNECT_GRIMM_SCENE='GG_Grimm';HOLLOWCONNECT_GRIMM_AUTHORITY='host';HOLLOWCONNECT_GRIMM_ENTRY_DELAY='3'}},
    @{Name='final-grimm-guest-013';Mode='grimm-motion';Env=@{HOLLOWCONNECT_GRIMM_SCENE='GG_Grimm';HOLLOWCONNECT_GRIMM_AUTHORITY='guest';HOLLOWCONNECT_GRIMM_ENTRY_DELAY='3'}},
    @{Name='final-rescue-013';Mode='rescue';Env=@{}},
    @{Name='final-death-story-013';Mode='death';Env=@{}},
    @{Name='final-full-013';Mode='full';Env=@{}},
    @{Name='final-join-013';Mode='join';Env=@{}}
)
if($Only){
    foreach($taskName in $Only){if(!($taskCases | Where-Object {$_.Name -eq $taskName})){throw 'Unknown selected suite case'}}
    $taskCases=@($taskCases | Where-Object {$Only -contains $_.Name})
}
if($StartAt -and !($taskCases | Where-Object {$_.Name -eq $StartAt})){throw 'Unknown suite starting case'}
$taskStarted=!$StartAt
foreach($taskCase in $taskCases){
    if($taskCase.Name -eq $StartAt){$taskStarted=$true}
    if(!$taskStarted){continue}
    if(!$taskCase.Existing){
        foreach($taskEnv in @(Get-ChildItem Env: | Where-Object {$_.Name -like 'HOLLOWCONNECT_*'})){
            [Environment]::SetEnvironmentVariable($taskEnv.Name,$null,'Process')
        }
        foreach($taskKey in $taskCase.Env.Keys){[Environment]::SetEnvironmentVariable($taskKey,$taskCase.Env[$taskKey],'Process')}
        & (Join-Path $PSScriptRoot 'Run-Two.ps1') -RunName $taskCase.Name -Mode $taskCase.Mode *> (Join-Path $taskRoot ('.test-results/'+$taskCase.Name+'.launch.log'))
    }
    $taskDir=Join-Path $taskRoot ('.test-results/'+$taskCase.Name)
    Write-Output ('RUN '+$taskCase.Name)
    $taskDeadline=[DateTime]::UtcNow.AddMinutes(12)
    while(!(Test-Path -LiteralPath (Join-Path $taskDir 'host.done')) -or !(Test-Path -LiteralPath (Join-Path $taskDir 'guest.done'))){
        foreach($taskFailure in @(Get-ChildItem -LiteralPath $taskDir -Filter '*.failed')){throw ($taskCase.Name+': '+(Get-Content -LiteralPath $taskFailure.FullName -Raw))}
        if([DateTime]::UtcNow -ge $taskDeadline){throw ('Timeout: '+$taskCase.Name)}
        Start-Sleep -Seconds 1
    }
    Start-Sleep -Seconds 2
    if(@(Get-ChildItem -LiteralPath $taskDir -Filter '*.failed').Count){throw ('Failure: '+$taskCase.Name)}
    & (Join-Path $PSScriptRoot 'Assert-ProbeLogs.ps1') -RunPath $taskDir -RequireRuntimeFiles
    foreach($taskRole in 'host','guest'){
        $taskLog=Join-Path $taskDir ($taskRole+'.log')
        if(!(Select-String -LiteralPath $taskLog -SimpleMatch 'FINAL PASS' -Quiet)){throw ('Missing final pass '+$taskCase.Name+' '+$taskRole)}
    }
    foreach($taskBinary in (Get-Content -LiteralPath (Join-Path $taskDir 'binaries.json') -Raw | ConvertFrom-Json)){
        $taskProduction=Join-Path $taskRoot ('src/HollowConnect/bin/Release/net472/'+$taskBinary.name)
        if((Get-FileHash -LiteralPath $taskProduction -Algorithm SHA256).Hash -ne $taskBinary.sha256){throw ('Wrong runtime DLL '+$taskCase.Name+' '+$taskBinary.name)}
    }
    Write-Output ('PASS BOTH '+$taskCase.Name+'; final production hashes verified')
}
Write-Output 'FINAL PASS selected final-binary dual-client runs; full evidence requires all 12 records'
