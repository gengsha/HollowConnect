$ErrorActionPreference='Stop'
$taskRoot=Join-Path (Split-Path -Parent $PSScriptRoot) '.test-results/log-gate-self-test'
New-Item -ItemType Directory -Path $taskRoot -Force | Out-Null
$taskCases=@(
    @{Name='native warning';File='host-unity.log';Text='Warning: native UI layout';Reject=$false},
    @{Name='native FSM Stun Fail is diagnostic payload';File='host.log';Text='2026-10-13T00:00:00Z ACTIVATE state=Stun Fail active=False';Reject=$false},
    @{Name='mod stack across lines';File='guest-unity.log';Text="NullReferenceException: destroyed target`n  at Hkmp.Game.Client.Entity.Entity.InitializeHost ()`n";Reject=$true},
    @{Name='spawn expiry in separate engine log';File='guest-engine.log';Text='WARN Spawn expired without a matching source/prefab: 1';Reject=$true},
    @{Name='engine callback error';File='host-guard.log';Text='2026-10-13T00:00:00Z ERROR HKMP source missing';Reject=$true},
    @{Name='mod missing reference';File='host-unity.log';Text="MissingReferenceException: Object has been destroyed`n  at HollowConnect.CoopRuntime.Update ()`n";Reject=$true},
    @{Name='failure after final pass';File='host.log';Text='FINAL PASS';Reject=$true;Marker=$true}
)
foreach($taskCase in $taskCases){
    Get-ChildItem -LiteralPath $taskRoot -File | Remove-Item -Force
    Set-Content -LiteralPath (Join-Path $taskRoot $taskCase.File) -Value $taskCase.Text
    if($taskCase.Marker){Set-Content -LiteralPath (Join-Path $taskRoot 'host.done') -Value 'done';Set-Content -LiteralPath (Join-Path $taskRoot 'host.failed') -Value 'late callback'}
    $taskRejected=$false
    try { & (Join-Path $PSScriptRoot 'Smoke/Assert-ProbeLogs.ps1') -RunPath $taskRoot } catch { $taskRejected=$true }
    if($taskRejected -ne $taskCase.Reject){throw ('Wrong log verdict: '+$taskCase.Name)}
    Write-Output ('PASS '+$taskCase.Name)
}
Write-Output ('FINAL PASS '+$taskCases.Count+' strict release log gate checks')
