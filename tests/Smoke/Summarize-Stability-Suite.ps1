param([Parameter(Mandatory)][ValidatePattern('^[a-z0-9][a-z0-9-]{0,50}$')][string]$RunPrefix,
    [Parameter(Mandatory)][string]$CoreLog,
    [ValidatePattern('^[a-z0-9][a-z0-9-]{0,95}$')][string]$PerformanceRun)
$ErrorActionPreference='Stop'
$summaryRoot=Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$summaryNames=@(
    'final-native-host-013','final-native-guest-013','final-lifecycle-host-013','final-lifecycle-guest-013',
    'final-boss-013','final-boss-late-013','final-grimm-host-013','final-grimm-guest-013',
    'final-rescue-013','final-death-story-013','final-full-013','final-join-013',
    'resources','full','defender','arena-host','arena-guest','combat-profile'
)
$summaryBinaries=@('HKMP.dll','HollowConnect.dll','HollowConnect.Core.dll') | ForEach-Object {
    [ordered]@{name=$_;sha256=(Get-FileHash -LiteralPath (Join-Path $summaryRoot ('src/HollowConnect/bin/Release/net472/'+$_))).Hash}
}
$summaryRuns=@()
if(!$PerformanceRun){$PerformanceRun=$RunPrefix+'-combat-profile'}
foreach($summaryCase in $summaryNames) {
    $summaryName=if($summaryCase -eq 'combat-profile'){$PerformanceRun}else{$RunPrefix+'-'+$summaryCase}
    $summaryDir=Join-Path $summaryRoot ('.test-results/'+$summaryName)
    & (Join-Path $PSScriptRoot 'Assert-ProbeLogs.ps1') -RunPath $summaryDir -RequireRuntimeFiles
    $summaryActual=@(Get-Content -LiteralPath (Join-Path $summaryDir 'binaries.json') -Raw | ConvertFrom-Json | Where-Object {$_.name -ne 'HollowConnect.Smoke.dll'})
    if($summaryActual.Count -ne 3){throw ('Incomplete binaries '+$summaryName)}
    foreach($summaryBinary in $summaryBinaries) {
        if(@($summaryActual | Where-Object {$_.name -eq $summaryBinary.name -and $_.sha256 -eq $summaryBinary.sha256}).Count -ne 1){throw ('Not final production DLL '+$summaryName)}
    }
    $summaryRoles=@()
    foreach($summaryRole in 'host','guest') {
        $summaryLines=@(Get-Content -LiteralPath (Join-Path $summaryDir ($summaryRole+'.log')))
        if(!(Test-Path -LiteralPath (Join-Path $summaryDir ($summaryRole+'.done'))) -or !($summaryLines -match 'FINAL PASS')){throw ('Incomplete role '+$summaryName+' '+$summaryRole)}
        $summaryRoles += [ordered]@{role=$summaryRole;assertions=@($summaryLines -match ' PASS ').Count;finalPass=[string]@($summaryLines -match 'FINAL PASS')[-1]}
    }
    $summaryRuns += [ordered]@{name=$summaryName;bothPassed=$true;binaries=$summaryActual;roles=$summaryRoles}
}
$summaryCore=Get-Content -LiteralPath $CoreLog -Raw
if($summaryCore -notmatch '(?m)^RESULT 200 passed, 0 failed\s*$'){throw 'Core regression result missing or changed; review the actual count.'}
$summaryFrames=@()
foreach($summaryRole in 'host','guest') {
    $summaryLines=Get-Content -LiteralPath (Join-Path $summaryRoot ('.test-results/'+$PerformanceRun+'/'+$summaryRole+'.log'))
    foreach($summaryLine in $summaryLines) {
        if($summaryLine -match 'FRAME SUMMARY (\S+) samples=(\d+) p95Ms=([\d.]+) p99Ms=([\d.]+) maxMs=([\d.]+) over16ms=(\d+) over33ms=(\d+) collections=(\d+)') {
            $summaryFrames += [ordered]@{role=$summaryRole;scene=$Matches[1];samples=[int]$Matches[2];p95Ms=[double]::Parse($Matches[3],[Globalization.CultureInfo]::InvariantCulture);p99Ms=[double]::Parse($Matches[4],[Globalization.CultureInfo]::InvariantCulture);maxMs=[double]::Parse($Matches[5],[Globalization.CultureInfo]::InvariantCulture);over16ms=[int]$Matches[6];over33ms=[int]$Matches[7];collections=[int]$Matches[8]}
        }
    }
}
if($summaryFrames.Count -ne 8){throw 'Missing performance windows'}
foreach($summaryRole in 'host','guest') {
    foreach($summaryScene in 'GG_Vengefly','GG_White_Defender','GG_Grimm','GG_Nailmasters') {
        if(@($summaryFrames | Where-Object {$_.role -eq $summaryRole -and $_.scene -eq $summaryScene -and $_.samples -gt 60}).Count -ne 1){throw 'Missing, duplicate or empty performance window'}
    }
}
$summaryLedger=Get-Content -LiteralPath (Join-Path $summaryRoot 'docs/evidence/runtime-blockers-0.1.21.json') -Raw | ConvertFrom-Json
$summaryOutput=[ordered]@{
    checkedAtUtc=[DateTime]::UtcNow.ToString('O');runPrefix=$RunPrefix;releaseReady=$false
    core=[ordered]@{passed=200;failed=0;logSha256=(Get-FileHash -LiteralPath $CoreLog).Hash}
    binaries=$summaryBinaries;runs=$summaryRuns
    performance=[ordered]@{environment='same machine, two isolated Unity clients, loopback UDP, batchmode 800x600';sampleSecondsPerScene=30;observations=$summaryFrames;windowsWithFramesOver33ms=@($summaryFrames | Where-Object {$_.over33ms -gt 0});allWindowsUnder33ms=(@($summaryFrames | Where-Object {$_.over33ms -gt 0}).Count -eq 0)}
    strictLogGate='Both roles done and FINAL PASS; all runtime logs scanned; errors, mod exception stacks, native crashes, expired spawns, unknown state indices and late failure markers rejected.'
    openHistoricalFindings=@($summaryLedger.findings | Where-Object {!$_.resolved} | ForEach-Object {$_.id})
    limits=@('No user save was loaded or repaired.','No real two-machine FRP/UDP or user-resolution frame-rate validation.','The twelve room cases cover their named routes and actions, not every boss attack or a complete playthrough.','A successful run does not uniquely explain earlier unconfirmed failures.','No installation or release approval is implied.')
}
$summaryPath=Join-Path $summaryRoot ('.test-results/'+$RunPrefix+'-summary.json')
$summaryOutput | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $summaryPath -Encoding UTF8
Write-Output ('PASS 18 dual-client cases, final production hashes and core regressions; summary '+$summaryPath)
$summaryFrames | ForEach-Object {[pscustomobject]$_} | Format-Table role,scene,p95Ms,p99Ms,maxMs,over16ms,over33ms
