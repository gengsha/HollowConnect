$ErrorActionPreference='Stop'
$taskRoot=Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$taskNames=@(1..11 | ForEach-Object {'boss-motion-diagnostic-013-'+$_}) + @(1..3 | ForEach-Object {'boss-control-diagnostic-013-'+$_}) + @(
    'boss-late-hc10-initial-snapshot-failure-013','boss-late-takeover-trace-013','boss-late-takeover-timeout-trace-013',
    'boss-late-expired-phase-diagnostic-013','boss-late-expired-phase-diagnostic-013-r2',
    'grimm-013-floating-target-host-pass','grimm-013-ad-retry-failure','grimm-ad-retry-diagnostic-013'
    'native-reentry-camera-failure-013','grimm-balloon-proximity-fixture-failure-013',
    'join-shutdown-reset-failure-013','native-camera-ring-stress-013','native-camera-ring-memory-013',
    'lifecycle-guest-setcollider-failure-013','lifecycle-guest-collider-trace-013',
    'lifecycle-needle-collider-stress-013','lifecycle-needle-binding-trace-013',
    'detached-reference-baseline-013','detached-reference-fixed-013','detached-reference-fixed-r2-013',
    'lifecycle-needle-binding-fixed-013','lifecycle-needle-binding-fixed-r2-013',
    'lifecycle-needle-binding-transfer-013','lifecycle-native-enable-trace-013',
    'lifecycle-native-resume-fixed-013','lifecycle-native-resume-fixed-r2-013',
    'lifecycle-native-motion-fixed-013','lifecycle-native-motion-fixed-host-013',
    'native-camera-hazard-013','native-camera-shared-gate-013',
    'lifecycle-native-motion-final-failure-013','lifecycle-static-identity-baseline-013',
    'lifecycle-static-identity-baseline-r2-013','lifecycle-static-identity-fixed-013','lifecycle-static-identity-fixed-host-013',
    'native-dash-registration-baseline-013','native-dash-registration-fixed-013',
    'lifecycle-tween-clone-final-failure-013','tween-clone-baseline-013','tween-clone-fixed-013',
    'grimm-guest-profile-menu-crash-013','grimm-profile-menu-baseline-013','grimm-host-old-gameplay-fixture-013',
    'camera-gate-nonhazard-013','camera-gate-persistent-013','camera-gate-native-recovery-013',
    'camera-gate-persistent-time-fixture-failure-013'
)
$taskRuns=@()
foreach($taskName in $taskNames){
    $taskDir=Join-Path $taskRoot ('.test-results/'+$taskName)
    $taskHashes=Get-Content -LiteralPath (Join-Path $taskDir 'binaries.json') -Raw | ConvertFrom-Json
    $taskRoles=@()
    foreach($taskRole in 'host','guest'){
        $taskLines=@(Get-Content -LiteralPath (Join-Path $taskDir ($taskRole+'.log')))
        $taskEvidence=$taskName+'-'+$taskRole+'.txt'
        $taskPublished=@($taskLines | Where-Object {$_ -match '^[0-9TZ:.+-]+ (?:PASS |FAIL |FINAL PASS|UNITY EXCEPTION |TWEEN CLONE |AUTHORITY |REPLICA |TAKEOVER |BEFORE HANDOFF |AFTER HANDOFF |AFTER HEAD PHASE |SAME AUTHORITY RESULT |ARENA |TERRAIN REFERENCE |PHYSICS CONTACT |PHASE TICK |VELOCITY TARGET |PART |ACTIVATE |HANDOFF |REOPENED |DIAGNOSTIC |MOTION |FIXTURE |NATIVE LAUNCH |NATIVE NEEDLE |NATIVE DASH |COLLIDER |CAMERA |HAZARD |ROOM )|^(?:iTween\.(?:RetrieveArgs|Awake)|UnityEngine.GameObject:SetActive|Hkmp.Game.Client.Entity.Entity:UpdateIsActive)|^\s+at (?:CameraController[.+]|GameManager\+<PlayerDeadFromHazard>|HeroController\+<DieFromHazard>|Hkmp.Ui.UiManager\+<GoToSaveMenu>)'})
        $taskPath=Join-Path $taskRoot ('docs/evidence/'+$taskEvidence)
        [IO.File]::WriteAllText($taskPath,(($taskPublished | ForEach-Object {([string]$_).TrimEnd()}) -join "`n")+"`n",[Text.UTF8Encoding]::new($false))
        $taskDone=Test-Path -LiteralPath (Join-Path $taskDir ($taskRole+'.done'))
        $taskFailed=Test-Path -LiteralPath (Join-Path $taskDir ($taskRole+'.failed'))
        $taskLogGatePassed=$true
        try { & (Join-Path $PSScriptRoot 'Assert-ProbeLogs.ps1') -RunPath $taskDir -RequireRuntimeFiles } catch { $taskLogGatePassed=$false }
        $taskRoles+=[ordered]@{role=$taskRole;done=$taskDone;failed=$taskFailed;logGatePassed=$taskLogGatePassed;passed=($taskDone -and !$taskFailed -and $taskLogGatePassed -and [bool]($taskLines -match 'FINAL PASS'));failures=@($taskLines -match '(^| )FAIL ' | ForEach-Object {[string]$_});evidence=$taskEvidence;sha256=(Get-FileHash -LiteralPath $taskPath -Algorithm SHA256).Hash}
    }
    $taskRuns+=[ordered]@{name=$taskName;binaries=@($taskHashes | ForEach-Object {[ordered]@{name=$_.name;sha256=$_.sha256}});roles=$taskRoles;bothPassed=(@($taskRoles | Where-Object {!$_.passed}).Count -eq 0);expectedRejection=($taskName -in @('camera-gate-nonhazard-013','camera-gate-persistent-013'));nativeCrashDetected=(@(Get-ChildItem -LiteralPath $taskDir -Filter '*.log' -File | Where-Object {(Get-Content -LiteralPath $_.FullName -Raw) -match 'Crash!!!|A crash has been intercepted by the crash handler|caused an Access Violation'}).Count -gt 0)}
}
$taskSummary=[ordered]@{
    version='0.1.13';checkedAtUtc=[DateTime]::UtcNow.ToString('O');muted=$true
    scope='Supplemental diagnostic builds, excluded from the twelve final-binary acceptance runs. No boss transform or FSM state forced. Same-authority controls use the same native armor/head extra damage and do not hand off authority.'
    findings=@(
        'Run 5 failed at native Rise 2; fixed updates and correct SetVelocity2d target continued while FK Terrain Block and the battle gate trapped the boss.'
        'Runs 6-9 recorded authority and replica positions/scales; run 9 identified an active detached replica FK Terrain Block after takeover and recovery, while native Terrain Block remained inactive under the native boss.'
        'Three same-authority controls completed native head recovery and movement. They are controls, not proof of every native edge case.'
        'Run 10 on pre-hierarchy HKMP SHA256 C5643BEDB65288361276F6F80732B118BDE18B3B4093F8DB42BD983C5C9676D4 failed the new native-reference/unique-active-collider assertion.'
        'Run 11 on an intermediate hierarchy-fix build passed the new collider ownership/removal assertions and phase movement. Final acceptance requires the separate final-binary suite.'
        'Earlier Run-state movement failure remains preserved separately; its incomplete hierarchy trace cannot establish a unique cause.'
        'Late-entry initial assertion required active terrain despite the native five-second Opened timeout. A traced delayed handoff recorded native Stun Fail deactivating terrain before transfer; inactive terrain and matching current FSM state were correct. The corrected fixture checks timeout recovery, preserved health, second native exposure, terrain ownership, phase advancement and movement.'
        'First corrected late-entry diagnostic still expected head health 33 after a second seven-point hit. Native timeout preserves the earlier damage, so the second head has 26; this failed fixture is retained. The next diagnostic verifies the pre-transfer 33 health and the new hit arithmetic separately.'
        'Grimm guest-authority run had zero launches and failed. Its zero-gravity test target was pushed to x64; native AD Pos uses target X plus/minus 7.5..9 with a 75..99 valid range, so no landing was possible. A same-build diagnostic passed; final acceptance keeps the invulnerable stationary target inside the arena and records resolved FSM targets.'
        'One final-build host-first second attempt failed Frozen camera despite visible boss/peer and matching camera locks. The following passive trace run passed both attempts; the original freeze source was not recorded, so its unique cause remains unconfirmed and is not erased by the retry.'
        'First permanently fixed Grimm target (85,8) also yielded zero launches and failed: native Balloon Check rejects distance less than 6 from (85.77,13.5), so Balloon Retry was correct. Final fixture uses (80,8), still inside the AD landing range and about 8 units from the balloon center; no production boss logic changed.'
        'Strict logging rejected a guest UDP ConnectionReset after ServerClientDisconnect Shutdown. NetClient waited for a final update send before cancelling receiving; socket receive errors also fabricated empty datagrams. Two actual-socket regressions failed before correction and pass afterwards. Later reconnect timeout in that failed probe is not independent proof of a reconnect defect, because the guard stopped that guest.'
        'Natural Hornet throw baseline failed when reference initialization cleared a live detached Needle. Cached FindChild initialization now rebinds available hierarchy matches and preserves live detached targets. A second pass resolves initialization dependencies. Needle Tink can also be detached to scene root; an early fixture incorrectly required its old parent and is retained as a failed test.'
        'Reference caching alone did not prevent same-room reconnect from restarting an already-running native root FSM on SetActive. OnEnable traces confirmed variable clearing and entry restart, including a GG Fall at negative Y. Registration now temporarily suspends RestartOnEnable only for started native root FSMs, restores it after initialization or destruction, and initializes references before state recovery.'
        'Preserved FSM state exposed a missing velocity snapshot: Hornet resumed A Dash at zero speed. All native rigidbody entities now include existing velocity/gravity components, publish their initial state and restore it before FSM recovery. Velocity changes are rate limited to 20 Hz. No new protocol fields or forced boss states.'
        'Two intermediate builds completed host-first and guest-first lifecycle probes with six natural needle-throw disconnect/reconnect cycles each. Both roles required real post-reconnect Throw Recover, live Needle/Tink references, visible bosses/avatars, matching health and clean strict logs. Interrupted and earlier failed runs remain recorded, and these builds are not the final suite.'
        'Real Cloud Hazard spike collisions confirmed the native FreezeInPlace -> PlayerDeadFromHazard path and natural input/camera recovery on both peers. The old failing coordinate also triggered a hazard on the guest. The shared room gate only permits a known hazard animation up to 15 seconds; non-hazard or persistent freezes still fail. This confirms a transient-native-freeze false-positive risk but cannot retrospectively prove the unique source of the old host sample.'
        'Final guest-first cycle 4 timed out waiting for Throw Recover despite valid references. Its host registered Needle ID 4568293017055036489 under Hornet, while the guest registered detached Needle ID 6622943944039214165 at scene root. Same-scene disconnect cleared the connection bindings and registration recomputed IDs from mutable hierarchy paths.'
        'The first new identity fixture accidentally overwrote payload files with barrier ready markers; both peers reported other=ready in the initial Moss room. Correcting separate payload/barrier names exposed the real Needle identity mismatch on the first same-room reconnect, before the six-cycle stress. Both failures remain retained.'
        'Static identity correction stores the initial key on the native GameObject lifetime, validates scene and object instance for copied metadata, and leaves explicitly spawned dynamic lifetimes unchanged. Shared static-entity identity checks and natural needle recovery checks are required after reconnect. Current diagnostics do not replace the final-hash suite.'
        'Guest-first static identity correction completed six real needle reconnect cycles. Host-first then stopped in A Dash during the initial reconnect, before the six cycles. A targeted natural-dash baseline on the identity-only HKMP DLL recorded (-23.5,-18.7) before disconnect and (0,0) after the previous authority re-registered first, without changing A Dash; natural completion failed within 8 seconds.'
        'Native registration pause now captures active rigidbodies before disabling the parent and restores them after all independently managed children initialize. Local paused motion is discarded for replicas or network takeover; a changed native FSM state takes precedence. The targeted intermediate correction passed the same natural-dash motion/completion assertion. Final lifecycle cases require this explicit dash sample plus six real needle reconnect cycles in both entry orders.'
        'The next final lifecycle failed strict logs after natural dash and first needle recovery: activating a reconnect replica ran copied iTween.Awake without arguments targeted at the clone. A deterministic fixture using actual production Entity cloning reproduced both root and child errors on the old engine. Inactive-clone immediate component removal passed both peers, preserving native runtime IDs/global arguments and allowing a fresh replica tween to move. Neither fixture counts as final-hash acceptance.'
        'A subsequent guest-first Grimm run crashed the isolated host in native profile UI and timed out both connections. Gameplay-only fixtures had already selected unsaved characters but invoked Connect without the auto-connect flag, opening asynchronous save selection again. Crash reports remain private; actual profile requests are traced in a separate failing fixture. This does not establish the cause of any historical user disconnect.'
        'Camera gate time-scale fault injection did not form the intended persistent freeze: the game restored timeScale and native recovery completed normally. The probe explicitly failed rather than counting that as a valid negative test. Corrected test-only injection blocks the real PlayerDeadFromHazard coroutine after a real spike collision; expected failures are separate from battle acceptance.'
    )
    runs=$taskRuns;twoMachineFrpValidation=$false
} | ConvertTo-Json -Depth 10
[IO.File]::WriteAllText((Join-Path $taskRoot 'docs/evidence/hierarchy-diagnostics-0.1.13.json'),$taskSummary.Replace("`r`n","`n")+"`n",[Text.UTF8Encoding]::new($false))
Write-Output ('Recorded '+$taskRuns.Count+' diagnostic runs, including failures, fixture corrections and intermediate binary hashes; none counted as final acceptance.')
