$ErrorActionPreference='Stop'
$taskRoot=Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$taskNames=@(1..11 | ForEach-Object {'boss-motion-diagnostic-013-'+$_}) + @(1..3 | ForEach-Object {'boss-control-diagnostic-013-'+$_})
$taskRuns=@()
foreach($taskName in $taskNames){
    $taskDir=Join-Path $taskRoot ('.test-results/'+$taskName)
    $taskHashes=@(Get-Content -LiteralPath (Join-Path $taskDir 'binaries.json') -Raw | ConvertFrom-Json)
    $taskRoles=@()
    foreach($taskRole in 'host','guest'){
        $taskLines=@(Get-Content -LiteralPath (Join-Path $taskDir ($taskRole+'.log')))
        $taskEvidence=$taskName+'-'+$taskRole+'.txt'
        $taskPublished=@($taskLines | Where-Object {$_ -match '^[0-9TZ:.+-]+ (?:PASS |FAIL |FINAL PASS|AUTHORITY |REPLICA |TAKEOVER |BEFORE HANDOFF |AFTER HANDOFF |AFTER HEAD PHASE |SAME AUTHORITY RESULT |ARENA |TERRAIN REFERENCE |PHYSICS CONTACT |PHASE TICK |VELOCITY TARGET )'})
        $taskPath=Join-Path $taskRoot ('docs/evidence/'+$taskEvidence)
        $taskPublished | Set-Content -LiteralPath $taskPath -Encoding UTF8
        $taskDone=Test-Path -LiteralPath (Join-Path $taskDir ($taskRole+'.done'))
        $taskFailed=Test-Path -LiteralPath (Join-Path $taskDir ($taskRole+'.failed'))
        $taskRoles+=[ordered]@{role=$taskRole;done=$taskDone;failed=$taskFailed;passed=($taskDone -and !$taskFailed -and [bool]($taskLines -match 'FINAL PASS'));failures=@($taskLines -match '(^| )FAIL ');evidence=$taskEvidence;sha256=(Get-FileHash -LiteralPath $taskPath -Algorithm SHA256).Hash}
    }
    $taskRuns+=[ordered]@{name=$taskName;binaries=$taskHashes;roles=$taskRoles;bothPassed=(@($taskRoles | Where-Object {!$_.passed}).Count -eq 0)}
}
[ordered]@{
    version='0.1.13';checkedAtUtc=[DateTime]::UtcNow.ToString('O');muted=$true
    scope='Supplemental diagnostic builds, excluded from the twelve final-binary acceptance runs. No boss transform or FSM state forced. Same-authority controls use the same native armor/head extra damage and do not hand off authority.'
    findings=@(
        'Run 5 failed at native Rise 2; fixed updates and correct SetVelocity2d target continued while FK Terrain Block and the battle gate trapped the boss.'
        'Runs 6-9 recorded authority and replica positions/scales; run 9 identified an active detached replica FK Terrain Block after takeover and recovery, while native Terrain Block remained inactive under the native boss.'
        'Three same-authority controls completed native head recovery and movement. They are controls, not proof of every native edge case.'
        'Run 10 on pre-hierarchy HKMP SHA256 C5643BEDB65288361276F6F80732B118BDE18B3B4093F8DB42BD983C5C9676D4 failed the new native-reference/unique-active-collider assertion.'
        'Run 11 on an intermediate hierarchy-fix build passed the new collider ownership/removal assertions and phase movement. Final acceptance requires the separate final-binary suite.'
        'Earlier Run-state movement failure remains preserved separately; its incomplete hierarchy trace cannot establish a unique cause.'
    )
    runs=$taskRuns;twoMachineFrpValidation=$false
} | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $taskRoot 'docs/evidence/hierarchy-diagnostics-0.1.13.json') -Encoding UTF8
Write-Output ('Recorded '+$taskRuns.Count+' diagnostic runs, including failures and intermediate binary hashes; none counted as final acceptance.')
