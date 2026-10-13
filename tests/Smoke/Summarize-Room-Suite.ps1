$ErrorActionPreference='Stop'
$taskRoot=Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$taskBlockerPath=Join-Path $taskRoot 'docs/evidence/runtime-blockers-0.1.13.json'
$taskBlockers=Get-Content -LiteralPath $taskBlockerPath -Raw | ConvertFrom-Json
$taskDefinitions=@(
    @('final-native-host-013','Native first-Pantheon UI and entrance; host first; two attempts with reconnect; first five bosses, delayed snapshots/transitions, both real Hornet nails, native Hornet defeat to spa.'),
    @('final-native-guest-013','Same native entrance and repeated first-five-boss checks with guest first; both real Hornet nails; native defeat to spa.'),
    @('final-lifecycle-host-013','Host first; actual root/child runtime tween cloning preserves native arguments and fresh replica animation; destroyed native AudioPlayInState source, native Moss death to Hornet, native moving A Dash retained through registration pause, six additional natural needle-throw reconnect cycles with matching static identities, valid detached references, resumed motion and Throw Recover; cloned identity metadata isolation.'),
    @('final-lifecycle-guest-013','Same tween cloning, cleanup/transition, native dash pause and six natural needle-throw reconnect cycles with guest first; static IDs and both roles checked after every reconnect; cloned metadata isolation.'),
    @('final-boss-013','Native False Knight armor/head phase transition and authority handoff in ordinary Crossroads room; stable detached-child references, single native terrain collider after takeover and no replica collider after recovery.'),
    @('final-boss-late-013','Guest joins an already-exposed False Knight after the hierarchy refresh interval; delayed handoff allows the native five-second exposure timeout, verifies preserved health, normal resumed combat and a second native exposure, terrain ownership, phase advancement and detached-replica cleanup.'),
    @('final-grimm-host-013','Host first; native Grimm launch force, ongoing horizontal fireballs/firebats and replica motion; no midflight takeover.'),
    @('final-grimm-guest-013','Same native Grimm motion probe with guest first.'),
    @('final-rescue-013','Actual nail rescue in both roles; 33 soul attribution, one-mask revive, original Shade feedback, stale/duplicate confirmation rejection.'),
    @('final-death-story-013','Ordinary native personal deaths and separate benches; original Shade and currency/soul branches; downed feedback and entry protection.'),
    @('final-full-013','Full co-op probe: real remote nail, authority transfer, shared ability, map, personal F6, nail rescue, both wipe orders, F7/F8, disconnect cleanup.'),
    @('final-join-013','New in-memory characters, menu return/reconnect, host timeout and reopening the same port; avatars and authority initialize.')
)
$taskRuns=@()
foreach($taskDefinition in $taskDefinitions){
    $taskName=$taskDefinition[0]
    $taskDir=Join-Path $taskRoot ('.test-results/'+$taskName)
    $taskOrigin=if(Test-Path -LiteralPath (Join-Path $taskDir 'origin.json')){Get-Content -LiteralPath (Join-Path $taskDir 'origin.json') -Raw | ConvertFrom-Json}else{$null}
    & (Join-Path $PSScriptRoot 'Assert-ProbeLogs.ps1') -RunPath $taskDir -RequireRuntimeFiles
    $taskHashes=Get-Content -LiteralPath (Join-Path $taskDir 'binaries.json') -Raw | ConvertFrom-Json
    if($taskHashes.Count -ne 3){throw ('Incomplete DLL evidence '+$taskName)}
    foreach($taskRequired in 'HKMP.dll','HollowConnect.dll','HollowConnect.Core.dll'){
        if(@($taskHashes | Where-Object {$_.name -eq $taskRequired}).Count -ne 1){throw ('Missing or duplicate DLL '+$taskName)}
    }
    foreach($taskHash in $taskHashes){
        if($taskHash.sha256 -ne (Get-FileHash -LiteralPath (Join-Path $taskRoot ('src/HollowConnect/bin/Release/net472/'+$taskHash.name)) -Algorithm SHA256).Hash){throw ('Not final DLL '+$taskName)}
    }
    $taskRoleResults=@()
    foreach($taskRole in 'host','guest'){
        $taskLogPath=Join-Path $taskDir ($taskRole+'.log')
        $taskLines=@(Get-Content -LiteralPath $taskLogPath)
        if($taskName -like 'final-grimm-*' -and ($taskLines -match 'FIXTURE PROFILE MENU')){throw ('Unexpected profile selection during gameplay '+$taskName+' '+$taskRole)}
        if(!(Test-Path -LiteralPath (Join-Path $taskDir ($taskRole+'.done'))) -or (Test-Path -LiteralPath (Join-Path $taskDir ($taskRole+'.failed')))){throw ('Incomplete or failed '+$taskName+' '+$taskRole)}
        if(!($taskLines -match 'FINAL PASS')){throw ('Invalid logs '+$taskName+' '+$taskRole)}
        # Publish only probe assertions and lifecycle telemetry, not raw logs
        # which can contain connection identities or network endpoints.
        $taskEvidenceName=$taskName+'-'+$taskRole+'.txt'
        $taskPublished=@($taskLines | Where-Object {$_ -match '^[0-9TZ:.+-]+ (?:PASS |FINAL PASS|TWEEN CLONE |ROOM |FIXTURE |NATIVE |CINEMATIC |COLLIDER |CAMERA |HAZARD |ENGINE Room prepared:|ENGINE Room snapshot committed:)'} )
        [IO.File]::WriteAllText((Join-Path $taskRoot ('docs/evidence/'+$taskEvidenceName)),(($taskPublished | ForEach-Object {([string]$_).TrimEnd()}) -join "`n")+"`n",[Text.UTF8Encoding]::new($false))
        $taskRoleResults+=[ordered]@{role=$taskRole;done=$true;failed=$false;finalPass=[string]@($taskLines -match 'FINAL PASS')[-1];assertionCount=@($taskLines -match ' PASS ').Count;evidence=$taskEvidenceName;sha256=(Get-FileHash -LiteralPath (Join-Path $taskRoot ('docs/evidence/'+$taskEvidenceName)) -Algorithm SHA256).Hash}
    }
    $taskRuns+=[ordered]@{name=$taskName;origin=$taskOrigin;scope=$taskDefinition[1];bothPassed=$true;binaries=@($taskHashes | ForEach-Object {[ordered]@{name=$_.name;sha256=$_.sha256}});roles=$taskRoleResults}
}
$taskExplorations=@(
    @{name='camera-gate-persistent-time-fixture-failure-013';bothPassed=$false;causeConfirmed=$true;cause='Time-scale fault injection did not form a persistent freeze: native code restored timeScale and recovery completed. The fixture explicitly failed. A corrected test-only wrapper blocks the actual hazard coroutine and both peers reject it after 15 real-time seconds. Negative controls remain outside the twelve battle acceptance cases.'},
    @{name='grimm-guest-profile-menu-crash-013';bothPassed=$false;causeConfirmed=$true;cause='Gameplay fixture called Connect after selecting an unsaved character but allowed asynchronous save selection again. Native host log records GoToProfileMenu failures and an access violation; both connections then timed out. Actual menu-request baseline confirms the fixture flaw. Corrected gameplay fixtures preserve selected characters; affected Boss/Grimm cases rerun. Does not establish the cause of historical user menu returns.'},
    @{name='lifecycle-tween-clone-final-failure-013';bothPassed=$false;causeConfirmed=$true;cause='Actual root/child runtime iTween controllers were copied into an inactive replica but retained no clone-targeted private argument table. Replica Awake raised NullReferenceException during reconnect snapshot activation. Old-engine production cloning fixture repeats the error; cleanup removes only copied controllers before Awake and leaves native arguments and new replica tween creation intact.'},
    @{name='lifecycle-native-motion-final-failure-013';bothPassed=$false;causeConfirmed=$true;cause='Guest-first fifth throw reconnect timed out: both static Needle registrations used different IDs after native detachment and clearing connection bindings. New static-identity baseline rejects this on the first reconnect. Native object lifetime metadata now preserves the first ID; cloned metadata validates source instance/scene. Both-role six-cycle verification and final-hash suite remain separate requirements.'},
    @{name='native-reentry-camera-failure-013';bothPassed=$false;causeConfirmed=$false;cause='Second-attempt host-first Vengefly room had visible boss/peer and converged camera locks, but camera mode remained FROZEN at the room assertion. Passive trace and repeated-entry probes do not establish the original cause. Failure is retained in the release blocker ledger.'},
    @{name='join-shutdown-reset-failure-013';bothPassed=$false;causeConfirmed=$true;cause='Strict guard rejected a UDP reset during shutdown while receiving remained active behind the final update flush. A failed receive also queued empty data. Actual-socket regression baselines confirm both defects; receiving now cancels before flush and failed receives are never enqueued. The guest guard stopped subsequent reconnect; the later wait timeout alone is not an independent reconnect failure.'},
    @{name='baseline-lifecycle-012';productionVersion='0.1.12';bothPassed=$false;cause='Controlled native audio-source destruction made replica action exit and room cleanup throw. Hornet then had stale room registration; host timed out waiting for authority and guest could not see the remote knight.'},
    @{name='lifecycle-013-guest';bothPassed=$false;cause='Probe reconnect username contained a hyphen and was rejected by native username validation; fixture now uses alphanumeric names.'},
    @{name='lifecycle-013-guest-r2';bothPassed=$false;cause='Reconnect room visibly restored; snapshot wrapper was installed after already-loaded room entry and missed the new snapshot. Wrapper now installs via ConnectEvent before publishing entry.'},
    @{name='native-pantheon-013-host / -r2 / -r3';bothPassed=$false;cause='Fresh character test door was locked/inactive or selected the wrong UI instance. Fixture now enables only the real door hierarchy, starts its FSM and reads its own spawnedUI.'},
    @{name='native-pantheon-013-host-r4 / -cine / -host-final / -dialog';bothPassed=$false;cause='Native entrance reached first-entry dialogue. Automated fixture did not provide the page-control NEXT input; added native dialogue input events.'},
    @{name='native-pantheon-013-rendered';bothPassed=$false;cause='Hidden nonbatch test did not progress startup; no conclusion about production graphics behavior. Final tests use batch mode.'},
    @{name='native-pantheon-013-host-r5';bothPassed=$false;cause='Second entrant traversed real entrance dialogue for longer than the original 20-second synthetic-entry wait; native entrance fixture now allows 75 seconds.'},
    @{name='native-pantheon-013-spa-barrier';bothPassed=$false;cause='First Hornet real nails and native completion passed. One probe left spa for Town before the other delayed snapshot was committed; added a both-ready barrier before probes leave spa.'},
    @{name='native-pantheon-013-ungraceful-host';bothPassed=$false;cause='All two-attempt battle assertions reached FINAL PASS. The launcher then forcibly stopped the still-connected server before the guest and caused a guest UDP ConnectionReset ERROR / failed marker after done. This run is excluded; probe now disconnects both peers, waits for server acknowledgement and stops server before done.'},
    @{name='native-pantheon-013-ungraceful-guest';bothPassed=$false;cause='Suite interrupted to correct teardown after discovering the preceding run acquired a failed marker after done. Incomplete guest-first run is excluded.'},
    @{name='boss-013-motion-failure';bothPassed=$false;causeConfirmed=$false;cause='Native False Knight armor/head and authority transfer passed, but after the next phase it stayed in Run at x=43.1 with zero measured velocity. A repeat with extra diagnostics passed at x=28.9; initial cause is not yet established and is not counted as a passed run.'},
    @{name='boss-motion-diagnostic-013-5';bothPassed=$false;causeConfirmed=$true;cause='Rise 2 remained stuck between a detached FK Terrain Block and the battle gate while native fixed updates continued. Later hierarchy diagnostics confirmed a leftover detached replica collider and stale native reference during takeover; final suite adds direct ownership/removal assertions.'},
    @{name='boss-motion-diagnostic-013-10';bothPassed=$false;causeConfirmed=$true;cause='Pre-hierarchy build failed the explicit native-reference/unique-active-collider handoff assertion. This baseline is excluded from final acceptance and preserved in the supplemental diagnostic ledger.'},
    @{name='lifecycle-013-host';bothPassed=$false;cause='Exploratory run interrupted when launching the next isolated probe; no complete success claimed.'}
)
$taskSummary=[ordered]@{
    version='0.1.13';engineVersion='3.0.0-hc10';checkedAtUtc=[DateTime]::UtcNow.ToString('O');muted=$true
    testedBuildInformation=@('HKMP.dll','HollowConnect.dll','HollowConnect.Core.dll') | ForEach-Object {
        $taskPath=Join-Path $taskRoot ('src/HollowConnect/bin/Release/net472/'+$_)
        [ordered]@{name=$_;productVersion=[Diagnostics.FileVersionInfo]::GetVersionInfo($taskPath).ProductVersion;sha256=(Get-FileHash -LiteralPath $taskPath -Algorithm SHA256).Hash}
    }
    status=if(@($taskBlockers.findings | Where-Object {!$_.resolved}).Count){'runtime-suite-passed-release-blocked'}else{'verified'}
    releaseReady=(@($taskBlockers.findings | Where-Object {!$_.resolved}).Count -eq 0)
    remainingBlockers=@($taskBlockers.findings | Where-Object {!$_.resolved});blockerLedger='runtime-blockers-0.1.13.json'
    logGate='All probe modes install the shared runtime guard; all per-run .log files are scanned, including separate engine/Unity/guard logs. Required Unity logs and per-run binary artifacts must exist. Mod exceptions, HKMP ERROR, Spawn expired, native crash/access violation signatures and late .failed markers reject the run. Fourteen synthetic gate checks pass; line-based exception scanning preserves blank/timestamp boundaries without unbounded word backtracking.'
    scope='Two isolated game copies, new in-memory characters, all SaveGame overloads intercepted; actual same-machine UDP/DTLS. Production DLL hashes checked per run. Batch mode test-only rendering waits use simulation frames.'
    baseline=@{engineSha256='F80DA3601DF693BA318B61512C2721CA8B18653370CFE3DAB4A80CA6F648666B';failureInjection=$true;naturalFullPlayReproduction=$false;chain='AudioSource.Stop -> ActionInState.ExitState -> ForgetActions -> Entity.Destroy -> ClearEntities / OnSceneUnloaded -> later room/presence callbacks skipped'}
    runs=$taskRuns;unsuccessfulExploratoryRuns=$taskExplorations;supplementalDiagnostics='hierarchy-diagnostics-0.1.13.json';cameraGateEvidence='camera-gate-0.1.13.json'
    limits=@('No two-machine FRP validation.','Native entry uses the real door UI/cinematic but selects its menu programmatically and exposes the locked fresh-character door.','No boss FSM state forced; early-room completion uses extra damage and intentional Down flags to delay one transition.','Hornet has real nail checks from both roles; these do not verify all attacks of all bosses.','First five first-Pantheon bosses and spa, not complete first-to-fourth Pantheon runs.','No complete ordinary-story reward, all dream variants or midflight projectile takeover validation.','Native preload/UI and disconnect warnings may exist; passing probe assertions do not mean zero log warnings.')
} | ConvertTo-Json -Depth 10
[IO.File]::WriteAllText((Join-Path $taskRoot 'docs/evidence/runtime-room-0.1.13.json'),$taskSummary.Replace("`r`n","`n")+"`n",[Text.UTF8Encoding]::new($false))
Write-Output ('PASS '+$taskRuns.Count+' runtime records with final binary hashes, both completion markers and sanitized assertion evidence')
if(@($taskBlockers.findings | Where-Object {!$_.resolved}).Count){Write-Output 'BLOCKED release: unresolved findings remain in runtime-blockers-0.1.13.json'}
