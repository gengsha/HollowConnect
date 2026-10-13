param([switch]$ReuseNonHazard,[switch]$ReuseAll)
$ErrorActionPreference='Stop'
$taskRoot=Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$taskCases=@(
    @{Name='camera-gate-nonhazard-013';Negative='nonhazard';Expected='System.Exception: Frozen camera without native hazard recovery injected-nonhazard'},
    @{Name='camera-gate-persistent-013';Negative='persistent';Expected='System.Exception: Timeout: native hazard camera recovery injected-persistent-hazard'},
    @{Name='camera-gate-native-recovery-013';Negative='';Expected=''}
)
$taskResults=@()
foreach($taskCase in $taskCases){
    foreach($taskEnv in @(Get-ChildItem Env: | Where-Object {$_.Name -like 'HOLLOWCONNECT_*'})){
        [Environment]::SetEnvironmentVariable($taskEnv.Name,$null,'Process')
    }
    $env:HOLLOWCONNECT_PANTHEON_AUTHORITY='host'
    $env:HOLLOWCONNECT_CAMERA_RING='1'
    if($taskCase.Negative){$env:HOLLOWCONNECT_CAMERA_GATE_NEGATIVE=$taskCase.Negative}
    if($taskCase.Negative -ne 'nonhazard'){$env:HOLLOWCONNECT_CAMERA_HAZARD='1'}
    if(!$ReuseAll -and !($ReuseNonHazard -and $taskCase.Negative -eq 'nonhazard')){
        & (Join-Path $PSScriptRoot 'Run-Two.ps1') -RunName $taskCase.Name -Mode pantheon *> (Join-Path $taskRoot ('.test-results/'+$taskCase.Name+'.launch.log'))
    }
    $taskDir=Join-Path $taskRoot ('.test-results/'+$taskCase.Name)
    $taskDeadline=[DateTime]::UtcNow.AddMinutes(3)
    while(@('host','guest' | Where-Object {!(Test-Path -LiteralPath (Join-Path $taskDir ($_+'.failed'))) -and !(Test-Path -LiteralPath (Join-Path $taskDir ($_+'.done')))}).Count){
        if([DateTime]::UtcNow -ge $taskDeadline){throw ('Camera fixture timeout '+$taskCase.Name)}
        Start-Sleep -Seconds 1
    }
    Start-Sleep -Seconds 2
    foreach($taskRequired in 'binaries.json','host.log','guest.log','host-unity.log','guest-unity.log'){
        if(!(Test-Path -LiteralPath (Join-Path $taskDir $taskRequired))){throw ('Missing camera fixture log/artifact '+$taskRequired)}
    }
    $taskHashes=Get-Content -LiteralPath (Join-Path $taskDir 'binaries.json') -Raw | ConvertFrom-Json
    if($taskHashes.Count -ne 3){throw 'Incomplete camera fixture production hashes'}
    foreach($taskRequired in 'HKMP.dll','HollowConnect.dll','HollowConnect.Core.dll'){
        if(@($taskHashes | Where-Object {$_.name -eq $taskRequired}).Count -ne 1){throw 'Missing or duplicate camera fixture production hash'}
    }
    foreach($taskBinary in $taskHashes){
        if((Get-FileHash -LiteralPath (Join-Path $taskRoot ('src/HollowConnect/bin/Release/net472/'+$taskBinary.name)) -Algorithm SHA256).Hash -ne $taskBinary.sha256){throw 'Camera fixture used another production build'}
    }
    $taskRoleProofs=@()
    if($taskCase.Expected){
        foreach($taskRole in 'host','guest'){
            $taskFailure=Get-Content -LiteralPath (Join-Path $taskDir ($taskRole+'.failed')) -Raw
            if(!$taskFailure.StartsWith($taskCase.Expected)){throw ('Unexpected camera failure '+$taskCase.Name+' '+$taskRole)}
            if(Test-Path -LiteralPath (Join-Path $taskDir ($taskRole+'.done'))){throw 'Injected camera failure was accepted'}
            $taskSeconds=$null
            if($taskCase.Negative -eq 'persistent'){
                $taskLines=@(Get-Content -LiteralPath (Join-Path $taskDir ($taskRole+'.log')))
                if(!($taskLines -match 'CAMERA GATE native hazard recovery blocked before HazardRespawn')){throw 'Native hazard recovery was not actually blocked'}
                $taskStart=@($taskLines -match 'CAMERA FROZEN injected-persistent-hazard')[-1]
                $taskEnd=@($taskLines -match 'FAIL System.Exception: Timeout: native hazard camera recovery injected-persistent-hazard')[-1]
                $taskSeconds=([DateTimeOffset]::Parse($taskEnd.Split(' ')[0])-[DateTimeOffset]::Parse($taskStart.Split(' ')[0])).TotalSeconds
                if($taskSeconds -lt 15 -or $taskSeconds -gt 22){throw ('Camera gate did not enforce its real-time bound: '+$taskSeconds)}
            }
            $taskRoleProofs+=[ordered]@{role=$taskRole;expectedFailureMatched=$true;realTimeSeconds=$taskSeconds}
        }
        & (Join-Path $PSScriptRoot 'Assert-ProbeLogs.ps1') -RunPath $taskDir -RequireRuntimeFiles -ExpectedFailureReason $taskCase.Expected
    }else{
        & (Join-Path $PSScriptRoot 'Assert-ProbeLogs.ps1') -RunPath $taskDir -RequireRuntimeFiles
        foreach($taskRole in 'host','guest'){
            if(!(Select-String -LiteralPath (Join-Path $taskDir ($taskRole+'.log')) -SimpleMatch 'FINAL PASS actual native spike collision' -Quiet)){throw 'Missing actual native recovery proof'}
            $taskRoleProofs+=[ordered]@{role=$taskRole;done=$true;strictLogGatePassed=$true;actualNativeRecovery=$true}
        }
    }
    $taskResults+=[ordered]@{name=$taskCase.Name;expectedRejection=[bool]$taskCase.Expected;expectedReason=$taskCase.Expected;bothMatchedExpectation=$true;roles=$taskRoleProofs;binaries=@($taskHashes | ForEach-Object {[ordered]@{name=$_.name;sha256=$_.sha256}})}
    Write-Output ('PASS camera gate expectation '+$taskCase.Name)
}
$taskSummary=[ordered]@{version='0.1.13';checkedAtUtc=[DateTime]::UtcNow.ToString('O');muted=$true;cases=$taskResults;
    scope='Real Unity camera and native spike recovery; deliberate non-hazard FreezeInPlace and explicitly blocked native hazard recovery coroutine must be rejected. Both persistent fixtures enforce a 15-second real-time rejection bound. Expected negative failures remain preserved, excluded from battle acceptance. No boss FSM state is forced.';
    historicalLimit='The unique cause of the old single-frame host observation cannot be reconstructed from logs that did not record hazard state or call origin. These differential checks validate the corrected acceptance rule; they do not assign that old sample a proven cause.'
} | ConvertTo-Json -Depth 10
[IO.File]::WriteAllText((Join-Path $taskRoot 'docs/evidence/camera-gate-0.1.13.json'),$taskSummary.Replace("`r`n","`n")+"`n",[Text.UTF8Encoding]::new($false))
Write-Output 'FINAL PASS camera gate: both peers reject unrelated and persistent freezes; actual native hazard recovers without forced camera changes.'
