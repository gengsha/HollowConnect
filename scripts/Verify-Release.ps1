param([switch]$Playtest, [string]$PlaytestBuild='')
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$version='0.2.1'
if($PlaytestBuild -and (!$Playtest -or $PlaytestBuild -notmatch '^[a-z0-9][a-z0-9-]{0,63}$')){throw 'Invalid playtest build label.'}
$taskPackageName="HollowConnect-$version"+$(if($PlaytestBuild){'-playtest-'+$PlaytestBuild}else{''})
$package=Join-Path $root ('dist\'+$taskPackageName)
$taskBlockers=Get-Content -LiteralPath (Join-Path $root "docs/evidence/runtime-blockers-$version.json") -Raw | ConvertFrom-Json
if($taskBlockers.version -ne $version){throw 'Wrong blocker ledger version.'}
$taskUnresolved=@($taskBlockers.findings | Where-Object {$_.resolved -ne $true})
if($taskUnresolved.Count -and !$Playtest){throw 'Unresolved runtime blocker; release cannot be verified.'}
$manifest=Get-Content -LiteralPath (Join-Path $package 'manifest.json') -Raw | ConvertFrom-Json
if ($manifest.version -ne $version) { throw 'Wrong version.' }
if($manifest.releaseChannel -ne $(if($Playtest){'playtest'}else{'release'})){throw 'Wrong release channel.'}
if($manifest.buildLabel -ne $PlaytestBuild){throw 'Wrong playtest build label.'}
if($Playtest){
    $taskAuthorizationRelative="docs/evidence/playtest-authorization-$version"+$(if($PlaytestBuild){'-'+$PlaytestBuild}else{''})+'.json'
    $taskAuthorization=Get-Content -LiteralPath (Join-Path $root $taskAuthorizationRelative) -Raw | ConvertFrom-Json
    if($taskAuthorization.version -ne $version -or $taskAuthorization.action -ne 'package-for-user-playtest' -or @($taskUnresolved | Where-Object {$_.id -notin $taskAuthorization.acceptedUnresolvedFindings}).Count){throw 'Playtest request does not cover current unresolved findings.'}
    if($PlaytestBuild -and $taskAuthorization.buildLabel -ne $PlaytestBuild){throw 'Playtest build label differs from the request record.'}
    if($manifest.releaseReady -ne $false){throw 'A playtest cannot claim release readiness.'}
    if($manifest.playtestAuthorizationSha256 -ne (Get-FileHash -LiteralPath (Join-Path $package $taskAuthorizationRelative) -Algorithm SHA256).Hash -or $manifest.playtestAuthorizationSha256 -ne (Get-FileHash -LiteralPath (Join-Path $root $taskAuthorizationRelative) -Algorithm SHA256).Hash){throw 'Playtest authorization hash differs.'}
    if(@(Compare-Object -ReferenceObject $taskUnresolved -DifferenceObject @($manifest.knownIssues) -Property id,resolved).Count){throw 'Playtest known issues differ from unresolved ledger.'}
}
$taskSkipRetest=$Playtest -and $taskAuthorization.retestOmitted -eq $true
if([bool]$manifest.retestOmitted -ne [bool]$taskSkipRetest){throw 'Retest status differs from request.'}
if (@($manifest.files).Count -ne 7) { throw 'Incomplete payload manifest.' }
foreach ($entry in $manifest.files) {
    $payload=Join-Path $package "payload\HKMP\$($entry.name)"
    if ((Get-FileHash -LiteralPath $payload -Algorithm SHA256).Hash -ne $entry.sha256) { throw "Bad payload: $($entry.name)" }
    $tested=Join-Path $root "tests\Smoke\bin\Release\net472\$($entry.name)"
    if (!$taskSkipRetest -and (Test-Path -LiteralPath $tested) -and (Get-FileHash -LiteralPath $tested -Algorithm SHA256).Hash -ne $entry.sha256) {
        throw "Payload differs from native test build: $($entry.name)"
    }
}
if($Playtest){
    foreach($binary in $taskAuthorization.binaries){
        if(@($manifest.files | Where-Object {$_.name -eq $binary.name -and $_.sha256 -eq $binary.sha256}).Count -ne 1){throw 'Payload differs from authorized package build.'}
    }
}
$taskRuntimeSource=if($Playtest -and $taskAuthorization.runtimeValidation){$taskAuthorizationRelative}else{"docs/evidence/runtime-room-$version.json"}
if($manifest.runtimeValidationSource -ne $taskRuntimeSource){throw 'Wrong runtime validation source.'}
$runtime=Get-Content -LiteralPath (Join-Path $package $taskRuntimeSource) -Raw | ConvertFrom-Json
if($Playtest -and $runtime.runtimeValidation){$runtime=$runtime.runtimeValidation}
$taskScopedPlaytest=$Playtest -and $taskAuthorization.runtimeValidation
if($taskSkipRetest){
    if(!$taskAuthorization.userRequest -or !$runtime.retestOmitted -or $runtime.version -ne $version -or $runtime.engineVersion -ne '3.0.0-hc13' -or $runtime.releaseReady -ne $false -or @($runtime.runs).Count -ne 0){throw 'Invalid no-retest packaging record.'}
    if(!$runtime.previousValidationSource -or (Get-FileHash -LiteralPath (Join-Path $package $runtime.previousValidationSource) -Algorithm SHA256).Hash -ne $runtime.previousValidationSha256){throw 'Previous validation reference differs.'}
}else{
if ((!$Playtest -and $runtime.releaseReady -ne $true) -or $runtime.releaseReady -ne $manifest.releaseReady -or $runtime.version -ne $version -or $runtime.engineVersion -ne '3.0.0-hc13' -or @($runtime.runs).Count -lt 1 -or (!$taskScopedPlaytest -and @($runtime.runs).Count -ne 12)) { throw 'Incomplete or blocked runtime evidence.' }
foreach ($run in $runtime.runs) {
    if (!$run.bothPassed -or @($run.binaries).Count -ne 3) { throw 'Incomplete runtime result.' }
    $runDir=Join-Path $root ('.test-results\'+$run.name)
    & (Join-Path $root 'tests/Smoke/Assert-ProbeLogs.ps1') -RunPath $runDir -RequireRuntimeFiles
    $actual=Get-Content -LiteralPath (Join-Path $runDir 'binaries.json') -Raw | ConvertFrom-Json
    if (@(Compare-Object -ReferenceObject $actual -DifferenceObject $run.binaries -Property name,sha256).Count) { throw 'Summary differs from actual runtime hashes.' }
    foreach ($binary in $actual) {
        if (@($manifest.files | Where-Object { $_.name -eq $binary.name -and $_.sha256 -eq $binary.sha256 }).Count -ne 1) { throw 'Runtime binary differs from release.' }
    }
    foreach ($role in 'host','guest') {
        if (!(Test-Path -LiteralPath (Join-Path $runDir ($role+'.done'))) -or (Test-Path -LiteralPath (Join-Path $runDir ($role+'.failed')))) { throw 'Runtime completion or failure markers disagree.' }
        if (!(Select-String -LiteralPath (Join-Path $runDir ($role+'.log')) -SimpleMatch 'FINAL PASS' -Quiet)) { throw 'Runtime final pass missing.' }
    }
}
}
if ((Get-FileHash -LiteralPath (Join-Path $package 'dependencies\moddingapi.v77.windows.zip') -Algorithm SHA256).Hash -ne $manifest.apiZipSha256) {
    throw 'Bad API archive.'
}
Add-Type -AssemblyName System.IO.Compression.FileSystem
function Hash-Stream($stream) {
    $sha=[Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-','') }
    finally { $sha.Dispose() }
}
$counts=@{}
foreach ($kind in 'windows','source') {
    $archive=[IO.Compression.ZipFile]::OpenRead((Join-Path $root ('dist\'+$taskPackageName+'-'+$kind+'.zip')))
    try {
        $files=@($archive.Entries | Where-Object { $_.Name.Length -gt 0 })
        $counts[$kind]=$files.Count
        if ($files.Count -eq 0) { throw "Empty $kind archive." }
        foreach ($entry in $files) {
            $relative=$entry.FullName.Replace('\','/')
            if ($relative -match '(^|/)(\.test-results|vendor|bin|obj)/|HollowConnect\.Smoke\.dll$|user\d+.*\.dat$|(?:unity|engine)\.log$|\.draft$') { throw "Private/build/draft file in $kind : $relative" }
            if ($kind -eq 'source' -and $relative -match '\.(dll|exe|pdb)$') { throw "Binary in source archive: $relative" }
            $base=if ($kind -eq 'windows') { $package } else { $root }
            $original=Join-Path $base $relative
            if (!(Test-Path -LiteralPath $original -PathType Leaf)) { throw "Stale archive file: $relative" }
            $stream=$entry.Open()
            try { $hash=Hash-Stream $stream } finally { $stream.Dispose() }
            if ($hash -ne (Get-FileHash -LiteralPath $original -Algorithm SHA256).Hash) { throw "Stale archive content: $relative" }
        }
        if ($kind -eq 'windows') {
            foreach ($name in 'manifest.json','Install.ps1','GamePath.ps1',([string][char]0x4e00+[char]0x952e+[char]0x5b89+[char]0x88c5+'.exe')) {
                if (!($files | Where-Object { $_.FullName.Replace('\','/') -eq $name })) { throw "Missing installer file: $name" }
            }
        }
    } finally { $archive.Dispose() }
}
$markdown=@(Get-Item -LiteralPath (Join-Path $package 'README.md')) + @(Get-ChildItem -LiteralPath (Join-Path $package 'docs') -File -Recurse -Filter '*.md')
foreach ($file in $markdown) {
    $content=Get-Content -LiteralPath $file.FullName -Raw
    foreach ($match in [regex]::Matches($content,'\]\(([^)]+)\)')) {
        $target=$match.Groups[1].Value
        # Developer reports in the package may link to repository-relative source
        # and private test evidence. Those files are present in the source archive
        # or intentionally kept out of the installer package; validate links that
        # are expected to resolve inside the installer payload itself.
        if ($target -match '^(?:https?://|#|/|[A-Za-z]:|\.\./)') { continue }
        # Avoid treating method-call examples in fenced/inline code as Markdown
        # links (for example: GetComponents[T](List[T] results)).
        if ($target -match '\s') { continue }
        $target=$target.Split('#')[0]
        if ($target -and !(Test-Path -LiteralPath (Join-Path $file.DirectoryName $target))) { throw "Broken markdown link: $($file.Name) -> $target" }
    }
}
$ledger=Get-Content -LiteralPath (Join-Path $package 'docs\AUDIT-REMEDIATION-0.1.7.md') -Raw
$ids=@([regex]::Matches($ledger,'(?m)^\| (S[1-5]-\d{2}) \|') | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)
if ($ids.Count -ne 49) { throw "Unexpected audit ledger count: $($ids.Count)" }
foreach ($kind in 'windows','source') {
    $name=$taskPackageName+'-'+$kind+'.zip'
    $hash=(Get-FileHash -LiteralPath (Join-Path $root "dist\$name") -Algorithm SHA256).Hash.ToLowerInvariant()
    if (!(Get-Content -LiteralPath (Join-Path $root 'dist\SHA256SUMS.txt') | Where-Object { $_ -eq "$hash  $name" })) { throw "Bad published checksum: $name" }
}
Write-Output "PASS $($manifest.releaseChannel) $version $PlaytestBuild`: 7 payload files; $(@($runtime.runs).Count) new runtime records (retestOmitted=$taskSkipRetest); API hash; $($counts.windows) installer archive entries; $($counts.source) source archive entries; all archive content and markdown links; 49 audit entries; published checksums; no test plugin or private saves."
