$ErrorActionPreference='Stop'
$taskRoot=Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
foreach($taskRole in 'host','guest') {
    if(Test-Path -LiteralPath "$taskRoot/.test-results/game-$taskRole/hollow_knight_Data/Managed/Mods/HKMP/HollowConnect.Smoke.dll") {
        throw "Remove the isolated smoke plugin after stopping the test clients: $taskRole"
    }
}
foreach($taskProcess in @(Get-Process -Name hollow_knight -ErrorAction SilentlyContinue)) {
    if($taskProcess.Path -and $taskProcess.Path.StartsWith($taskRoot+'\.test-results\game-')) {
        throw 'Stop the isolated test clients before the final log scan'
    }
}
$taskEvidence=Join-Path $taskRoot 'docs/evidence/network-reliability-2026-10-14.json'
$taskNames=@(
    'reliability-launch-final-lifecycle-host-20261014',
    'reliability-launch-final-lifecycle-guest-20261014',
    'reliability-launch-final-boss-late-20261014',
    'reliability-launch-final-grimm-host-20261014',
    'reliability-launch-final-grimm-guest-20261014'
)
$taskRuns=@(foreach($taskName in $taskNames){
    $taskDir=Join-Path "$taskRoot/.test-results" $taskName
    $taskEnvironment=Get-Content -LiteralPath "$taskDir/test-env.json" -Raw | ConvertFrom-Json
    if($taskEnvironment.HOLLOWCONNECT_NETWORK_FAULTS -ne '1' -or $taskEnvironment.HOLLOWCONNECT_NETWORK_TRACE -ne '1') {
        throw "Unexpected fault configuration: $taskName"
    }
    & "$taskRoot/tests/Smoke/Assert-ProbeLogs.ps1" -RunPath $taskDir -RequireRuntimeFiles
    if(@(Get-ChildItem -LiteralPath $taskDir -Filter '*.failed').Count -ne 0){throw "Failed run: $taskName"}
    $taskBinaries=@(Get-Content -LiteralPath "$taskDir/binaries.json" -Raw | ConvertFrom-Json)
    if($taskBinaries.Count -ne 3){throw "Expected exactly three production DLLs: $taskName"}
    foreach($taskBinary in $taskBinaries){
        $taskProduction=Join-Path "$taskRoot/src/HollowConnect/bin/Release/net472" $taskBinary.name
        if((Get-FileHash -LiteralPath $taskProduction -Algorithm SHA256).Hash -ne $taskBinary.sha256){throw "Production hash changed: $taskName/$($taskBinary.name)"}
    }
    $taskRoles=@(foreach($taskRole in 'host','guest'){
        $taskLog=Get-Content -LiteralPath "$taskDir/$taskRole.log" -Raw
        if(!(Test-Path -LiteralPath "$taskDir/$taskRole.done") -or $taskLog -notmatch 'FINAL PASS'){throw "Missing terminal pass: $taskName/$taskRole"}
        $taskFault=[regex]::Match($taskLog,'NETWORK FAULTS pattern=fixed-seed messages=(\d+) dropped=(\d+) reordered=(\d+) duplicated=(\d+) nestedResends=(\d+)')
        if(!$taskFault.Success){throw "Missing actual fault counts: $taskName/$taskRole"}
        $taskNumbers=@(1..5 | ForEach-Object {[int]$taskFault.Groups[$_].Value})
        if($taskNumbers[1] -eq 0 -or $taskNumbers[2] -eq 0 -or $taskNumbers[3] -eq 0){throw "A configured fault was not exercised: $taskName/$taskRole"}
        $taskUnityChecks=0
        if($taskName -like '*lifecycle-*'){
            foreach($taskCheck in @(
                'PASS native state entry and child activation cannot be split by the sender',
                'PASS delayed combat snapshots preserve native and replica colliders and child activation through takeover',
                'PASS detached entity root restores parent and world position before native local tween completion',
                'PASS native state transition publishes inside variable sampling window',
                'PASS empty native state clears the previous attack snapshot',
                'PASS native reactivation publishes active and its new FSM state together',
                'PASS unfinished absolute tween resumes before native completion',
                'PASS dormant takeover stays stopped until native activation and then completes',
                'PASS native fixed-update deceleration resumes and completes'
            )){
                if(!$taskLog.Contains($taskCheck)){throw "Missing Unity fixture $taskCheck in $taskName/$taskRole"}
                $taskUnityChecks++
            }
        }
        [pscustomobject][ordered]@{
            role=$taskRole;done=$true;finalPass=$true
            messages=$taskNumbers[0];dropped=$taskNumbers[1];reordered=$taskNumbers[2];duplicated=$taskNumbers[3];nestedResends=$taskNumbers[4]
            takeoverRounds=([regex]::Matches($taskLog,'PASS reconnect transferred native authority collider-cycle\.')).Count
            naturalRecoveries=([regex]::Matches($taskLog,'PASS native Throw Recover after reconnect')).Count
            unityChecks=$taskUnityChecks
        }
    })
    if($taskName -like '*lifecycle-*' -and
        (($taskRoles | Measure-Object -Property takeoverRounds -Sum).Sum -ne 8 -or
         ($taskRoles | Measure-Object -Property naturalRecoveries -Sum).Sum -ne 8)){
        throw "Eight authority exchanges and recoveries were not completed: $taskName"
    }
    [ordered]@{name=$taskName;bothPassed=$true;strictLogsPassed=$true;environment=$taskEnvironment;binaries=$taskBinaries;roles=$taskRoles}
})
$taskRegression=Get-Content -LiteralPath "$taskRoot/.test-results/reliability-launch-final-20261014.log" -Raw
if($taskRegression -notmatch 'RESULT 130 passed, 0 failed'){throw 'Final regression result missing'}
$taskUnitManifest=Get-Content -LiteralPath "$taskRoot/.test-results/reliability-launch-final-unit-binaries-20261014.json" -Raw | ConvertFrom-Json
foreach($taskBinary in $taskUnitManifest.binaries) {
    if((Get-FileHash -LiteralPath (Join-Path "$taskRoot/src/HollowConnect/bin/Release/net472" $taskBinary.name)).Hash -ne $taskBinary.sha256) {
        throw 'Unit regression binary changed'
    }
}
$taskGate=Get-Content -LiteralPath "$taskRoot/.test-results/reliability-log-gate-20261014.log" -Raw
if($taskGate -notmatch 'FINAL PASS 14 strict release log gate checks'){throw 'Log gate self-tests missing'}
$taskVersion=(Get-Item -LiteralPath "$taskRoot/src/HollowConnect/bin/Release/net472/HKMP.dll").VersionInfo.ProductVersion
if($taskVersion -notmatch '\+([0-9a-f]{40})$'){throw 'DLL source revision missing'}
$taskRevision=$Matches[1]
if($taskRevision -ne '6a2ac9b9e2cf3c7d98221907597511f0a502ddbc'){throw 'Unexpected acceptance build'}
if($taskUnitManifest.sourceCommit -ne $taskRevision){throw 'Unit regression build does not match runtime acceptance'}
$taskBaselines=@(
    @{name='reliability-origin-baseline-r2-20261014.log';passed=113;failed=3;source='9bc0bdf';issue='Nested reliable batches lose their origin and replay actions'},
    @{name='reliability-dedup-baseline-20261014.log';passed=116;failed=3;source='13a4809';issue='Resent origins are not recorded as consumed'},
    @{name='reliability-snapshot-baseline-r2-20261014.log';passed=119;failed=4;source='89c7d66';issue='Independently delayed originals and resends roll back sparse entity snapshots'},
    @{name='reliability-close-baseline-20261014.log';passed=125;failed=1;source='2d342cb';issue='A closed DTLS peer remains registered until heartbeat timeout'},
    @{name='reliability-combat-baseline-20261014.log';passed=126;failed=4;source='09821ab';issue='Delayed complete CombatState snapshots roll back collider and child state'}
)
foreach($taskBaseline in $taskBaselines){
    $taskText=Get-Content -LiteralPath "$taskRoot/.test-results/$($taskBaseline.name)" -Raw
    if($taskText -notmatch "RESULT $($taskBaseline.passed) passed, $($taskBaseline.failed) failed"){throw "Baseline result mismatch: $($taskBaseline.name)"}
}
foreach($taskRole in 'host','guest') {
    $taskOldCombat="$taskRoot/.test-results/reliability-combat-unity-baseline-20261014"
    if(!(Test-Path -LiteralPath "$taskOldCombat/$taskRole.failed") -or
       (Get-Content -LiteralPath "$taskOldCombat/$taskRole.log" -Raw) -notmatch 'FAIL System.Exception: Delayed combat snapshot disabled a newer native/replica collider or child') {
        throw "Missing Unity combat failure baseline: $taskRole"
    }
}
if((Get-Content -LiteralPath "$taskRoot/.test-results/reliability-combat-final-lifecycle-host-20261014/host.log" -Raw) -notmatch
   'FAIL System.Exception: Native needle recovery completed in a stale parent space') {
    throw 'Missing saved-launch assertion failure baseline'
}
$taskResult=[ordered]@{
    sourceCommit=$taskRevision
    regression=[ordered]@{passed=130;failed=0;log='.test-results/reliability-launch-final-20261014.log';binaries=$taskUnitManifest.binaries;logGateChecks=14}
    fixes=@(
        @{commit='13a4809';issue='Preserve reliable origins through nested retransmissions'},
        @{commit='89c7d66';issue='Deduplicate consumed origins separately from datagram ACKs'},
        @{commit='4a47197';issue='Reject older sparse entity snapshot fields while preserving actions'},
        @{commit='ff332b8';issue='Retire closed DTLS sessions after their final queued datagram'},
        @{commit='b4bea7f';issue='Reject superseded complete CombatState snapshots while retaining actions'}
    )
    fixtureCorrections=@(@{commit='6a2ac9b';issue='Compare needle return with the saved native world-space launch target, keeping distance and timeout limits'})
    baselines=$taskBaselines
    runtimeBaselines=@(
        @{name='reliability-loss-lifecycle-host-20261014';source='c9dd07a';result='Remote knight invisible in lifecycle-moss';resolved=$false;classification='Periodic fault pattern; this first sample lacks origin trace'},
        @{name='reliability-loss-lifecycle-host-trace-20261014';source='c9dd07a';result='Room entry starved; origin 90 repeatedly retransmitted at 103,116,129 and dropped every time';resolved=$false;classification='Confirmed periodic loss aligned with retransmission cadence'},
        @{name='reliability-seeded-lifecycle-host-20261014';source='2d342cb';result='Wrong authority at collider-cycle.0 while closed previous owner remains registered';resolved=$true;fix='ff332b8'},
        @{name='reliability-closed-seeded-boss-late-20261014';source='09821ab';result='Second native armor collapse did not expose the head after takeover';resolved=$false;classification='Original run has no post-damage phase trace; later diagnostics cannot uniquely attribute this failure'},
        @{name='reliability-combat-unity-baseline-20261014';source='09821ab filter independently rebuilt';result='Both native and replica collider/child state rolled back';resolved=$true;fix='b4bea7f';binaries=@(Get-Content -LiteralPath "$taskRoot/.test-results/reliability-combat-unity-baseline-20261014/binaries.json" -Raw | ConvertFrom-Json)},
        @{name='reliability-combat-final-lifecycle-host-20261014';source='b4bea7f';result='Needle launch and return y=27.5; old assertion expected current boss y=26.6 minus 0.5';resolved=$true;classification='Incorrect fixture target; native Throw shrinks boss collider after saving the launch position';fix='6a2ac9b'}
    )
    faultInjection=[ordered]@{environment='HOLLOWCONNECT_NETWORK_FAULTS=1';pattern='fixed-seed';seed='0x6D2B79F5';dropBurst=2;dropIntervals=@(10,16);reorderIntervals=@(6,12);duplicateIntervals=@(12,22);sameMachine=$true;frpVerified=$false;periodicStarvationResolved=$false}
    runs=$taskRuns
    scope=[ordered]@{entityFields=@('active','position','animation','scale axes','FSM state','individual FSM variables and object references','complete CombatState subtree snapshots');otherGenericSnapshotsCovered=$false;initialLiveSnapshotInterleavingCovered=$false;actionsRetained=$true;sequenceWindow=32768;fullSequenceWrapChecked=$true;allBossesVerified=$false;completeReleaseSuite=$false;wirePayloadChanged=$false}
    historicalCameraFrozenResolved=$false
    isolatedClientsStopped=$true;isolatedSmokePluginsRemovedFromLoadablePayload=$true;isolatedSmokePluginDisposition='Renamed to HollowConnect.Smoke.dll.disabled after stopping both isolated clients';logsScannedAfterShutdown=$true
    releaseReady=$false;packaged=$false;installedToNormalGame=$false
}
$taskJson=($taskResult | ConvertTo-Json -Depth 18).Replace("`r`n","`n")+"`n"
[IO.File]::WriteAllText($taskEvidence,$taskJson,[Text.UTF8Encoding]::new($false))
Write-Output "Verified $($taskRuns.Count) fault-injection runs; source $taskRevision; 130 regressions, 14 log-gate self-tests; releaseReady=false"
