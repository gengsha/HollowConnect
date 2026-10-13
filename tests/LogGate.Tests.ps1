$ErrorActionPreference='Stop'
$taskRoot=Join-Path (Split-Path -Parent $PSScriptRoot) '.test-results/log-gate-self-test'
New-Item -ItemType Directory -Path $taskRoot -Force | Out-Null
$taskCases=@(
    @{Name='native warning';File='host-unity.log';Text='Warning: native UI layout';Reject=$false},
    @{Name='native FSM Stun Fail is diagnostic payload';File='host.log';Text='2026-10-13T00:00:00Z ACTIVATE state=Stun Fail active=False';Reject=$false},
    @{Name='mod stack across lines';File='guest-unity.log';Text="NullReferenceException: destroyed target`n  at Hkmp.Game.Client.Entity.Entity.InitializeHost ()`n";Reject=$true},
    @{Name='spawn expiry in separate engine log';File='guest-engine.log';Text='WARN Spawn expired without a matching source/prefab: 1';Reject=$true},
    @{Name='engine callback error';File='host-guard.log';Text='2026-10-13T00:00:00Z ERROR HKMP source missing';Reject=$true},
    @{Name='engine ERROR inside probe log';File='host.log';Text='2026-10-13T00:00:00Z ENGINE ERROR failed initialization';Reject=$true},
    @{Name='native Unity crash';File='host-unity.log';Text="Crash!!!`nA crash has been intercepted by the crash handler.";Reject=$true},
    @{Name='native access violation';File='host-unity.log';Text='hollow_knight.exe caused an Access Violation (0xc0000005)';Reject=$true},
    @{Name='mod missing reference';File='host-unity.log';Text="MissingReferenceException: Object has been destroyed`n  at HollowConnect.CoopRuntime.Update ()`n";Reject=$true},
    @{Name='native exception ends at blank line';File='host-unity.log';Text="NullReferenceException: native preload`n  at GameManager.SetupHeroRefs ()`n`n  at Hkmp.UnrelatedDiagnostic ()";Reject=$false},
    @{Name='native exception ends at new timestamp';File='host-unity.log';Text="NullReferenceException: native preload`n  at GameManager.SetupHeroRefs ()`n2026-10-13T00:00:00Z INFO Hkmp.UnrelatedDiagnostic";Reject=$false},
    @{Name='failure after final pass';File='host.log';Text='FINAL PASS';Reject=$true;Marker=$true},
    @{Name='exact camera fault injection is a control, not a battle pass';File='host.log';Text='2026-10-13T00:00:00Z FAIL System.Exception: Frozen camera without native hazard recovery injected-nonhazard';Reject=$false;Camera=$true},
    @{Name='camera fault injection cannot mask another mod error';File='host.log';Text="2026-10-13T00:00:00Z FAIL System.Exception: Frozen camera without native hazard recovery injected-nonhazard`nNullReferenceException: destroyed target`n  at Hkmp.Game.Client.Entity.Entity.InitializeHost ()";Reject=$true;Camera=$true}
)
foreach($taskCase in $taskCases){
    Get-ChildItem -LiteralPath $taskRoot -File | Remove-Item -Force
    Set-Content -LiteralPath (Join-Path $taskRoot $taskCase.File) -Value $taskCase.Text
    if($taskCase.Marker){Set-Content -LiteralPath (Join-Path $taskRoot 'host.done') -Value 'done';Set-Content -LiteralPath (Join-Path $taskRoot 'host.failed') -Value 'late callback'}
    $taskOptions=@{}
    if($taskCase.Camera){
        $taskOptions.ExpectedFailureReason='System.Exception: Frozen camera without native hazard recovery injected-nonhazard'
        foreach($taskRole in 'host','guest'){Set-Content -LiteralPath (Join-Path $taskRoot ($taskRole+'.failed')) -Value $taskOptions.ExpectedFailureReason}
    }
    $taskRejected=$false
    try { & (Join-Path $PSScriptRoot 'Smoke/Assert-ProbeLogs.ps1') -RunPath $taskRoot @taskOptions } catch { $taskRejected=$true }
    if($taskRejected -ne $taskCase.Reject){throw ('Wrong log verdict: '+$taskCase.Name)}
    Write-Output ('PASS '+$taskCase.Name)
}
Write-Output ('FINAL PASS '+$taskCases.Count+' strict release log gate checks')
