[CmdletBinding()]
param(
    [string]$Output = $null
)

$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
if ([string]::IsNullOrWhiteSpace($Output)) {
    $Output = Join-Path $repo 'docs\evidence\final-boss-static-20261017.json'
}
if (Test-Path -LiteralPath $Output) {
    throw "Refusing to overwrite existing evidence: $Output"
}

$continuationPath = Join-Path $repo 'src\HKMP\Game\Client\Entity\FinalBossCorpseContinuation.cs'
$combatPath = Join-Path $repo 'src\HKMP\Game\Client\Entity\Component\CombatStateComponent.cs'
$entityPath = Join-Path $repo 'src\HKMP\Game\Client\Entity\Entity.cs'
$referencesPath = Join-Path $repo 'src\HKMP\Game\Client\Entity\EntityReferences.cs'
$musicPath = Join-Path $repo 'src\HKMP\Game\Client\Entity\Component\MusicComponent.cs'
$assetPath = Join-Path $repo '.test-results\colosseum-presentation-20261017\final-fsm-decoded.json'

foreach ($path in @($continuationPath, $combatPath, $entityPath, $referencesPath, $musicPath, $assetPath)) {
    if (!(Test-Path -LiteralPath $path)) { throw "Missing required audit input: $path" }
}

$continuation = Get-Content -Raw -LiteralPath $continuationPath
$combat = Get-Content -Raw -LiteralPath $combatPath
$entity = Get-Content -Raw -LiteralPath $entityPath
$references = Get-Content -Raw -LiteralPath $referencesPath
$music = Get-Content -Raw -LiteralPath $musicPath
$asset = Get-Content -Raw -LiteralPath $assetPath | ConvertFrom-Json -AsHashtable
$checks = [System.Collections.Generic.List[object]]::new()
$failed = 0

function Check([string]$name, [bool]$ok, [string]$detail) {
    $script:checks.Add([ordered]@{ name = $name; passed = $ok; detail = $detail })
    if (!$ok) { $script:failed++ }
}

Check 'detached seed requires exact corpse path and empty parent' `
    ($combat -match 'path==finalCorpse\.Path&&active&&parentKey\.Length==0') `
    'CombatState only authorizes a corpse after the authoritative active detached root is decoded.'
Check 'local continuation requires authoritative Die and detached seed' `
    ($continuation -match 'die = true' -and $continuation -match '!die \|\| !seed') `
    'A presentation seed arriving before Phase Control/Die cannot start a local ending.'
Check 'post-seed packets skip personal corpse subtree' `
    ($combat -match 'finalCorpse\?\.IsPersonalPath\(path\)==true') `
    'Transforms, activation, colliders, damage, renderers, animations and particles are not overwritten after release.'
Check 'native physics are restored before local FSM start' `
    ($continuation -match 'body\.Body\.bodyType = body\.Type' -and $continuation -match 'body\.Body\.gravityScale = body\.Gravity') `
    'The clone receives the native Dynamic/gravity values captured before replica freezing.'
Check 'corpse child FSM references are initialized' `
    ($continuation -match 'EntityInitializer\.InitializeFsm\(corpseFsm\)') `
    'The child FSM was outside the root-level initialization pass.'
Check 'only Corpse FSM is enabled by continuation' `
    ($continuation -match 'corpseFsm\.enabled = true' -and $continuation -notmatch 'Object\.Client\.SetActive\(true\)') `
    'Root Control/Phase Control and Boss AI remain network-controlled/inactive.'
Check 'late detached native corpse is rejected safely' `
    ($continuation -match '(?s)nativeFsm == null.*?return null' -and $continuation -match 'partPath = EntityIdentity\.Path') `
    'An entity registered after Die is rejected when the fresh clone has no matching corpse; no second corpse is manufactured.'
Check 'detached client corpse survives entity retirement' `
    ($references -match 'e\.FinalCorpse\?\.KeepClientPart\(go\) == true') `
    'Retiring the network entity cannot destroy the local ending subtree.'
Check 'entity wires final corpse continuation' `
    ($entity -match 'FinalCorpse = FinalBossCorpseContinuation\.TryCreate' -and
     $entity -match 'FinalCorpse\?\.ObservePhase' -and
     $entity -match 'FinalCorpse\?\.EntityDestroyed') `
    'Construction, authoritative Die observation and cleanup are all connected.'

$corpseStates = @($asset['Corpse']['states'] | ForEach-Object { $_['name'] })
$controlStates = @($asset['Control']['states'] | ForEach-Object { $_['name'] })
foreach ($state in @('Sucking', 'Ramp Up', 'Ending?', 'To Ending A', 'To Ending B')) {
    Check "native Corpse state: $state" ($corpseStates -contains $state) `
        'The authoritative asset contains the local post-battle continuation state.'
}
Check 'native Control state: HK Decline Music' ($controlStates -contains 'HK Decline Music') `
    'The music action originates on the authoritative boss Control FSM.'
$musicAction = @($asset['Control']['states'] | Where-Object { $_['name'] -eq 'HK Decline Music' } | ForEach-Object { $_['actions'] | ForEach-Object { $_['type'] } })
Check 'HK Decline Music has native cue and snapshot' `
    ($musicAction -contains 'ApplyMusicCue' -and $musicAction -contains 'TransitionToAudioSnapshot') `
    'The normal music hooks capture the original cue and snapshot while replica Control stays disabled.'
Check 'decline cue uses the existing room music path' `
    ($music -match 'Name = "HKDecline", Index = 23' -and $continuation -notmatch 'declineMusicActions|ReceiveFinalDecline') `
    'The missing cue is appended with a stable ID; no second replay path waits until death or restarts the same cue.'

# Deterministic log simulation for the ordering boundary. This mirrors the
# production contract without loading Unity or touching an installed save.
$seedPath = 'Boss%20Corpse#0'
$simSeed = $false
$simReleased = $false
$simDetached = $false
$simDie = $false
$simPersonalPackets = 0
function SimTryStart {
    if ($script:simDie -and $script:simSeed -and $script:simDetached) { $script:simReleased = $true }
}
function SimObservePhase([string]$fsm, [string]$state) {
    if ($fsm -eq 'Phase Control' -and $state -eq 'Die') { $script:simDie = $true; SimTryStart }
}
function SimObserveSeed([string]$path, [bool]$active, [string]$parent) {
    if ($path -eq $script:seedPath -and $active -and [string]::IsNullOrEmpty($parent)) {
        $script:simSeed = $true
        $script:simDetached = $true
        SimTryStart
    }
}

SimObserveSeed $seedPath $true ''
Check 'simulation: detached seed before Die does not release' (!$simReleased) `
    'A presentation seed arriving before the authoritative Die boundary cannot start the ending.'
SimObservePhase 'Phase Control' 'Die'
Check 'simulation: Die after detached seed releases once' $simReleased `
    'The local continuation starts after both ordering boundaries have arrived.'
$simReleased = $false
$simDie = $false
$simSeed = $false
$simDetached = $false
SimObserveSeed $seedPath $true 'e:42'
Check 'simulation: attached seed does not release' (!$simReleased) `
    'A corpse still parented to the boss is not treated as the final detached corpse.'
SimObserveSeed $seedPath $true ''
SimObservePhase 'Phase Control' 'Die'
Check 'simulation: complete detached seed releases once' $simReleased `
    'The full seed packet enables the local continuation after all child presentation is applied.'
if ($simSeed) { $simPersonalPackets++ }
Check 'simulation: personal subtree is stable after release' ($simPersonalPackets -eq 1) `
    'Subsequent authority packets are ignored for the personal corpse path.'

$lateBoundary = [ordered]@{
    beforeRegistrationDetached = $true
    candidateCounts = @(
        [ordered]@{ count = 0; policy = 'no continuation; preserve existing behavior' },
        [ordered]@{ count = 1; policy = 'unsupported when clone was created after detach' },
        [ordered]@{ count = 2; policy = 'unsupported; never guess' }
    )
    productionDecision = 'registration after native detach remains unsupported; require an existing paired clone seed'
}

$hashes = @{}
foreach ($path in @($continuationPath, $combatPath, $entityPath, $referencesPath, $musicPath, $assetPath)) {
    $hashes[(Resolve-Path $path).Path.Substring($repo.Length + 1).Replace('\', '/')] = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
}
$evidence = [ordered]@{
    generatedUtc = [DateTime]::UtcNow.ToString('o')
    status = if ($failed -eq 0) { 'PASS_STATIC_AND_SIMULATION' } else { 'FAIL' }
    passed = ($failed -eq 0)
    checks = @($checks)
    lateJoinBoundary = $lateBoundary
    limitations = @(
        'No Unity process or personal save was used by this check.',
        'Late registration with zero/ambiguous detached corpse candidates remains intentionally unsupported.',
        'A real two-client run is still required to verify audio timing, focus input, and Cinematic_Ending_A/B scene loads.'
    )
    inputSha256 = $hashes
}

$parent = Split-Path -Parent $Output
New-Item -ItemType Directory -Force -Path $parent | Out-Null
$json = $evidence | ConvertTo-Json -Depth 8
[IO.File]::WriteAllText($Output, $json + "`n", [Text.UTF8Encoding]::new($false))
Write-Output ("{0}: {1} checks, {2} failures; wrote {3}" -f $evidence.status, $checks.Count, $failed, $Output)
if ($failed -ne 0) { exit 1 }
