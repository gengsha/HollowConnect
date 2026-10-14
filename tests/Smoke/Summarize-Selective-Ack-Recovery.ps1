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
$taskEvidence=Join-Path $taskRoot 'docs/evidence/selective-ack-recovery-2026-10-14.json'
$taskNames=@(
    'selective-ack-final-periodic-lifecycle-host-20261014',
    'selective-ack-final-periodic-trace-lifecycle-guest-20261014',
    'selective-ack-final-lifecycle-host-20261014',
    'selective-ack-final-lifecycle-guest-20261014',
    'selective-ack-final-boss-late-20261014',
    'selective-ack-final-grimm-host-20261014',
    'selective-ack-final-grimm-guest-20261014',
    'selective-ack-final-periodic-entry-host-20261014',
    'selective-ack-final-periodic-entry-guest-20261014'
)
$taskRuns=@(foreach($taskName in $taskNames){
    $taskDir=Join-Path "$taskRoot/.test-results" $taskName
    $taskEnvironment=Get-Content -LiteralPath "$taskDir/test-env.json" -Raw | ConvertFrom-Json
    $taskMode=if($taskName -like '*periodic-*'){'periodic'}else{'1'}
    $taskPattern=if($taskMode -eq 'periodic'){'periodic'}else{'fixed-seed'}
    if($taskEnvironment.HOLLOWCONNECT_NETWORK_FAULTS -ne $taskMode -or $taskEnvironment.HOLLOWCONNECT_NETWORK_TRACE -ne '1') {
        throw "Unexpected fault configuration: $taskName"
    }
    $taskRejected=$false; $taskRejection=$null
    try { & "$taskRoot/tests/Smoke/Assert-ProbeLogs.ps1" -RunPath $taskDir -RequireRuntimeFiles }
    catch {
        if(@(Get-ChildItem -LiteralPath $taskDir -Filter '*.failed').Count -eq 0){throw}
        $taskRejected=$true; $taskRejection=$_.Exception.Message
    }
    # These separate entry cases are the specific runtime acceptance for this
    # fix. A rejected broader stress run remains rejected in the same report.
    if($taskRejected -and $taskName -like '*periodic-entry-*'){throw "Entry acceptance failed: $taskName"}
    $taskBinaries=@(Get-Content -LiteralPath "$taskDir/binaries.json" -Raw | ConvertFrom-Json)
    if($taskBinaries.Count -ne 3){throw "Expected exactly three production DLLs: $taskName"}
    if((@($taskBinaries.name | Sort-Object) -join ',') -ne 'HKMP.dll,HollowConnect.Core.dll,HollowConnect.dll') {
        throw "Unexpected production DLL names: $taskName"
    }
    foreach($taskBinary in $taskBinaries){
        $taskProduction=Join-Path "$taskRoot/src/HollowConnect/bin/Release/net472" $taskBinary.name
        if((Get-FileHash -LiteralPath $taskProduction -Algorithm SHA256).Hash -ne $taskBinary.sha256){throw "Production hash changed: $taskName/$($taskBinary.name)"}
    }
    $taskRoles=@(foreach($taskRole in 'host','guest'){
        $taskLog=Get-Content -LiteralPath "$taskDir/$taskRole.log" -Raw
        $taskDone=Test-Path -LiteralPath "$taskDir/$taskRole.done"
        $taskFinalPass=$taskLog -match 'FINAL PASS'
        if(!$taskRejected -and (!$taskDone -or !$taskFinalPass)){throw "Missing terminal pass: $taskName/$taskRole"}
        $taskFault=[regex]::Match($taskLog,('NETWORK FAULTS pattern='+$taskPattern+' messages=(\d+) dropped=(\d+) reordered=(\d+) duplicated=(\d+) nestedResends=(\d+)'))
        if(!$taskFault.Success -and !$taskRejected){throw "Missing actual fault counts: $taskName/$taskRole"}
        $taskNumbers=if($taskFault.Success){@(1..5 | ForEach-Object {[int]$taskFault.Groups[$_].Value})}else{@($null,$null,$null,$null,$null)}
        if(!$taskRejected -and ($taskNumbers[1] -eq 0 -or $taskNumbers[2] -eq 0 -or $taskNumbers[3] -eq 0)){throw "A configured fault was not exercised: $taskName/$taskRole"}
        $taskUnityChecks=0
        if($taskName -like '*lifecycle-*' -or $taskName -like '*periodic-entry-*'){
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
                if($taskLog.Contains($taskCheck)){$taskUnityChecks++}
                elseif(!$taskRejected){throw "Missing Unity fixture $taskCheck in $taskName/$taskRole"}
            }
        }
        [pscustomobject][ordered]@{
            role=$taskRole;done=$taskDone;finalPass=$taskFinalPass;faultVerificationCompleted=$taskFault.Success
            messages=$taskNumbers[0];dropped=$taskNumbers[1];reordered=$taskNumbers[2];duplicated=$taskNumbers[3];nestedResends=$taskNumbers[4]
            takeoverRounds=([regex]::Matches($taskLog,'PASS reconnect transferred native authority collider-cycle\.')).Count
            naturalRecoveries=([regex]::Matches($taskLog,'PASS native Throw Recover after reconnect')).Count
            unityChecks=$taskUnityChecks
            failure=if(Test-Path -LiteralPath "$taskDir/$taskRole.failed"){Get-Content -LiteralPath "$taskDir/$taskRole.failed" -Raw}else{$null}
        }
    })
    if(!$taskRejected -and $taskEnvironment.HOLLOWCONNECT_COLLIDER_RECONNECT_STRESS -eq '1' -and
        (($taskRoles | Measure-Object -Property takeoverRounds -Sum).Sum -ne 8 -or
         ($taskRoles | Measure-Object -Property naturalRecoveries -Sum).Sum -ne 8)){
        throw "Eight authority exchanges and recoveries were not completed: $taskName"
    }
    [pscustomobject][ordered]@{name=$taskName;bothPassed=!$taskRejected;strictLogsPassed=!$taskRejected;rejection=$taskRejection;environment=$taskEnvironment;binaries=$taskBinaries;roles=$taskRoles}
})
$taskRegression=Get-Content -LiteralPath "$taskRoot/.test-results/selective-ack-final-unit-20261014.log" -Raw
if($taskRegression -notmatch 'RESULT 142 passed, 0 failed'){throw 'Final regression result missing'}
$taskUnitManifest=Get-Content -LiteralPath "$taskRoot/.test-results/selective-ack-final-unit-binaries-20261014.json" -Raw | ConvertFrom-Json
foreach($taskBinary in $taskUnitManifest) {
    if((Get-FileHash -LiteralPath (Join-Path "$taskRoot/src/HollowConnect/bin/Release/net472" $taskBinary.name)).Hash -ne $taskBinary.sha256) {
        throw 'Unit regression binary changed'
    }
}
$taskGate=Get-Content -LiteralPath "$taskRoot/.test-results/selective-ack-final-log-gate-20261014.log" -Raw
if($taskGate -notmatch 'FINAL PASS 14 strict release log gate checks'){throw 'Log gate self-tests missing'}
$taskVersion=(Get-Item -LiteralPath "$taskRoot/src/HollowConnect/bin/Release/net472/HKMP.dll").VersionInfo.ProductVersion
if($taskVersion -notmatch '\+([0-9a-f]{40})$'){throw 'DLL source revision missing'}
$taskRevision=$Matches[1]
if($taskRevision -ne '44ae5d17537f4f257c7d553e11694dc0c57abe98'){throw 'Unexpected acceptance build'}
if(@($taskUnitManifest).Count -ne 3){throw 'Unit regression binary manifest incomplete'}
foreach($taskBinary in $taskUnitManifest){if($taskBinary.productVersion -notlike ('*+'+$taskRevision)){throw 'Unit regression build does not match runtime acceptance'}}
foreach($taskRole in 'host','guest') {
    foreach($taskName in 'selective-ack-final-periodic-entry-host-20261014','selective-ack-final-periodic-entry-guest-20261014') {
        $taskText=Get-Content -LiteralPath "$taskRoot/.test-results/$taskName/$taskRole.log" -Raw
        foreach($taskStage in 'lifecycle-moss','lifecycle-hornet','lifecycle-retry') {
            if(!$taskText.Contains('PASS visible bosses/knight and unfrozen camera '+$taskStage)) {
                throw "Missing targeted room entry acceptance: $taskName/$taskRole/$taskStage"
            }
        }
    }
}

$taskBaselineLog='.test-results/selective-ack-baseline-v2-20261014.log'
$taskBaseline=Get-Content -LiteralPath (Join-Path $taskRoot $taskBaselineLog) -Raw
if($taskBaseline -notmatch 'RESULT 132 passed, 10 failed' -or
   $taskBaseline -notmatch 'server=0 client=0; client carriers=90,103,116,129,142,155,168,181,194,207') {
    throw 'Missing deterministic periodic loss failure baseline'
}
$taskOldDll=Join-Path $taskRoot '.test-results/selective-ack-old-binaries-20261014/HKMP.dll'
$taskOldVersion=(Get-Item -LiteralPath $taskOldDll).VersionInfo.ProductVersion
if($taskOldVersion -notlike '*+92a7822*'){throw 'Unexpected baseline sender build'}
$taskOriginal=Get-Content -LiteralPath "$taskRoot/.test-results/reliability-loss-lifecycle-host-trace-20261014/host.presence.log" -Raw
foreach($taskCarrier in 90,103,116,129){
    if($taskOriginal -notmatch "SEND ServerUpdatePacketId seq=$taskCarrier origin=90 enter" -or
       $taskOriginal -notmatch "DROP transport=269968512 message=\d+ seq=$taskCarrier\r?\n") {
        throw 'Missing traced runtime periodic failure'
    }
}
$taskContrastName='selective-ack-old-sender-seeded-trace-guest-20261014'
$taskContrastDir=Join-Path "$taskRoot/.test-results" $taskContrastName
$taskContrastBinaries=@(Get-Content -LiteralPath "$taskContrastDir/binaries.json" -Raw | ConvertFrom-Json)
if($taskContrastBinaries.Count -ne 3){throw 'Old-sender contrast binary manifest incomplete'}
foreach($taskBinary in $taskContrastBinaries) {
    $taskExpected=if($taskBinary.name -eq 'HKMP.dll'){$taskOldDll}else{Join-Path "$taskRoot/src/HollowConnect/bin/Release/net472" $taskBinary.name}
    if((Get-FileHash -LiteralPath $taskExpected).Hash -ne $taskBinary.sha256){throw 'Old-sender contrast binary mismatch'}
}
$taskContrastRoles=@(foreach($taskRole in 'host','guest') {
    [pscustomobject]@{role=$taskRole;done=(Test-Path -LiteralPath "$taskContrastDir/$taskRole.done");failure=if(Test-Path -LiteralPath "$taskContrastDir/$taskRole.failed"){Get-Content -LiteralPath "$taskContrastDir/$taskRole.failed" -Raw}else{$null}}
})
$taskContrastRejected=@($taskContrastRoles | Where-Object failure).Count -ne 0
if(!$taskContrastRejected) {
    if(@($taskContrastRoles | Where-Object {!$_.done}).Count){throw 'Old-sender contrast not finished'}
    & "$taskRoot/tests/Smoke/Assert-ProbeLogs.ps1" -RunPath $taskContrastDir -RequireRuntimeFiles
}
$taskReproducedBeforeFix=@($taskContrastRoles | Where-Object {$_.failure -match 'Timeout: native Throw Recover after reconnect \d+ state=Thrown'}).Count -gt 0
$taskResult=[ordered]@{
    sourceCommit=$taskRevision
    fix=[ordered]@{commit='44ae5d1';issue='Recover missing reliable data when a selective ACK report confirms three distinct newer datagrams';bossSpecific=$false;wirePayloadChanged=$false;newTimers=$false;timeoutsChanged=$false;faultScheduleChanged=$false;acceptanceWeakened=$false}
    regression=[ordered]@{passed=142;failed=0;log='.test-results/selective-ack-final-unit-20261014.log';binaries=$taskUnitManifest;logGateChecks=14}
    deterministicBaseline=[ordered]@{passed=132;failed=10;log=$taskBaselineLog;senderProductVersion=$taskOldVersion;senderSha256=(Get-FileHash -LiteralPath $taskOldDll).Hash;source90Carriers=@(90,103,116,129,142,155,168,181,194,207);serverEnters=0;clientEnters=0;dtlsBoundaryVirtual=$true;clockVirtual=$true;productionSendEncodeFrameDecodeAckAndDispatch=$true}
    excludedFixtureDiagnostics=@('.test-results/selective-ack-baseline-20261014.log: enter positions not initialized; not a production failure baseline','.test-results/selective-ack-fixed-build-20261014.log: generic type inference compilation error; not runtime evidence')
    runtimeBaseline=[ordered]@{name='reliability-loss-lifecycle-host-trace-20261014';source='c9dd07a';origin=90;carriers=@(90,103,116,129);resolved=$true;fix='44ae5d1';untracedFirstSampleUniquelyAttributed=$false}
    takeoverContrast=[ordered]@{name=$taskContrastName;senderProductVersion=$taskOldVersion;candidateAcceptance=$false;bothPassed=!$taskContrastRejected;sameThrowRecoveryFailureReproducedBeforeSenderFix=$taskReproducedBeforeFix;binaries=$taskContrastBinaries;roles=$taskContrastRoles;uniqueRootCauseEstablished=$false;introducedBySenderFix=$null;preexistingDefectProven=$false}
    faultInjection=[ordered]@{attemptedRuns=9;passedRuns=@($taskRuns | Where-Object bothPassed).Count;rejectedRuns=@($taskRuns | Where-Object {!$_.bothPassed}).Count;periodicEntryRunsPassed=2;periodicStressRunsAttempted=2;fixedSeedRunsAttempted=5;fixedSeedRunsPassed=@($taskRuns | Where-Object {$_.environment.HOLLOWCONNECT_NETWORK_FAULTS -eq '1' -and $_.bothPassed}).Count;periodicDropBurst=2;periodicDropInterval=13;periodicReorderInterval=9;periodicDuplicateInterval=17;seed='0x6D2B79F5';sameMachine=$true;frpVerified=$false;periodicStarvationResolved=$true;allPeriodicStressPassed=$false}
    runs=$taskRuns
    unresolved=@('Full takeover stress retains Thrown with inactive needle and times out in periodic host round 2 and fixed-seed guest round 4; not an entry starvation; not resolved','Original second native armor collapse failure lacks post-damage phase trace; later passing probes cannot uniquely attribute it','Historical camera FROZEN source remains unresolved','Other GenericData snapshots and initial/live snapshot interleaving','Real two-machine FRP, complete current-build 12-case release suite and wider Boss/Pantheon/multi-player coverage')
    isolatedClientsStopped=$true;isolatedSmokePluginsRemovedFromLoadablePayload=$true;isolatedSmokePluginDisposition='Renamed to HollowConnect.Smoke.44ae5d1.dll.disabled after stopping both isolated clients';logsScannedAfterShutdown=$true
    previousEvidence='docs/evidence/network-reliability-2026-10-14.json';releaseReady=$false;packaged=$false;installedToNormalGame=$false
}
$taskJson=($taskResult | ConvertTo-Json -Depth 18).Replace("`r`n","`n")+"`n"
[IO.File]::WriteAllText($taskEvidence,$taskJson,[Text.UTF8Encoding]::new($false))
Write-Output "Recorded $($taskRuns.Count) runs: $($taskResult.faultInjection.passedRuns) passed / $($taskResult.faultInjection.rejectedRuns) rejected; both targeted periodic entry cases passed; source $taskRevision; 142 regressions, 14 log-gate self-tests; releaseReady=false"
