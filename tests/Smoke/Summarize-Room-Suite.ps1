$ErrorActionPreference='Stop'
$taskRoot=Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$taskDefinitions=@(
    @('final-native-host-013','Native first-Pantheon UI and entrance; host first; two attempts with reconnect; first five bosses, delayed snapshots/transitions, both real Hornet nails, native Hornet defeat to spa.'),
    @('final-native-guest-013','Same native entrance and repeated first-five-boss checks with guest first; both real Hornet nails; native defeat to spa.'),
    @('final-lifecycle-host-013','Host first; execute native AudioPlayInState, destroy its source before exit, native Moss death to Hornet, no old-room entities, same-room disconnect/reconnect.'),
    @('final-lifecycle-guest-013','Same destroyed native audio/cleanup/transition/reconnect probe with guest first.'),
    @('final-boss-013','Native False Knight armor/head phase transition and authority handoff in ordinary Crossroads room; stable detached-child references, single native terrain collider after takeover and no replica collider after recovery.'),
    @('final-boss-late-013','Guest joins an already-exposed False Knight after the hierarchy refresh interval; snapshot restores detached parent/reference, native takeover continues head recovery, and disconnect destroys the detached replica after entity table retirement.'),
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
    & (Join-Path $PSScriptRoot 'Assert-ProbeLogs.ps1') -RunPath $taskDir
    $taskHashes=@(Get-Content -LiteralPath (Join-Path $taskDir 'binaries.json') -Raw | ConvertFrom-Json)
    foreach($taskHash in $taskHashes){
        if($taskHash.sha256 -ne (Get-FileHash -LiteralPath (Join-Path $taskRoot ('src/HollowConnect/bin/Release/net472/'+$taskHash.name)) -Algorithm SHA256).Hash){throw ('Not final DLL '+$taskName)}
    }
    $taskRoleResults=@()
    foreach($taskRole in 'host','guest'){
        $taskLogPath=Join-Path $taskDir ($taskRole+'.log')
        $taskLines=@(Get-Content -LiteralPath $taskLogPath)
        if(!(Test-Path -LiteralPath (Join-Path $taskDir ($taskRole+'.done'))) -or (Test-Path -LiteralPath (Join-Path $taskDir ($taskRole+'.failed')))){throw ('Incomplete or failed '+$taskName+' '+$taskRole)}
        if(!($taskLines -match 'FINAL PASS')){throw ('Invalid logs '+$taskName+' '+$taskRole)}
        # Publish only probe assertions and lifecycle telemetry, not raw logs
        # which can contain connection identities or network endpoints.
        $taskEvidenceName=$taskName+'-'+$taskRole+'.txt'
        $taskPublished=@($taskLines | Where-Object {$_ -match '^[0-9TZ:.+-]+ (?:PASS |FINAL PASS|ROOM |FIXTURE |NATIVE |CINEMATIC |ENGINE Room prepared:|ENGINE Room snapshot committed:)'} )
        $taskPublished | Set-Content -LiteralPath (Join-Path $taskRoot ('docs/evidence/'+$taskEvidenceName)) -Encoding UTF8
        $taskRoleResults+=[ordered]@{role=$taskRole;done=$true;failed=$false;finalPass=@($taskLines -match 'FINAL PASS')[-1];assertionCount=@($taskLines -match ' PASS ').Count;evidence=$taskEvidenceName;sha256=(Get-FileHash -LiteralPath (Join-Path $taskRoot ('docs/evidence/'+$taskEvidenceName)) -Algorithm SHA256).Hash}
    }
    $taskRuns+=[ordered]@{name=$taskName;scope=$taskDefinition[1];bothPassed=$true;binaries=$taskHashes;roles=$taskRoleResults}
}
$taskExplorations=@(
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
[ordered]@{
    version='0.1.13';engineVersion='3.0.0-hc10';checkedAtUtc=[DateTime]::UtcNow.ToString('O');muted=$true
    scope='Two isolated game copies, new in-memory characters, all SaveGame overloads intercepted; actual same-machine UDP/DTLS. Production DLL hashes checked per run. Batch mode test-only rendering waits use simulation frames.'
    baseline=@{engineSha256='F80DA3601DF693BA318B61512C2721CA8B18653370CFE3DAB4A80CA6F648666B';failureInjection=$true;naturalFullPlayReproduction=$false;chain='AudioSource.Stop -> ActionInState.ExitState -> ForgetActions -> Entity.Destroy -> ClearEntities / OnSceneUnloaded -> later room/presence callbacks skipped'}
    runs=$taskRuns;unsuccessfulExploratoryRuns=$taskExplorations;supplementalDiagnostics='hierarchy-diagnostics-0.1.13.json'
    limits=@('No two-machine FRP validation.','Native entry uses the real door UI/cinematic but selects its menu programmatically and exposes the locked fresh-character door.','No boss FSM state forced; early-room completion uses extra damage and intentional Down flags to delay one transition.','Hornet has real nail checks from both roles; these do not verify all attacks of all bosses.','First five first-Pantheon bosses and spa, not complete first-to-fourth Pantheon runs.','No complete ordinary-story reward, all dream variants or midflight projectile takeover validation.','Native preload/UI and disconnect warnings may exist; passing probe assertions do not mean zero log warnings.')
} | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $taskRoot 'docs/evidence/runtime-room-0.1.13.json') -Encoding UTF8
Write-Output ('PASS '+$taskRuns.Count+' runtime records with final binary hashes, both completion markers and sanitized assertion evidence')
