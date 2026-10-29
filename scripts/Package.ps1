param([switch]$Playtest, [string]$PlaytestBuild='', [switch]$RebuildChecksums, [switch]$UserRequestedRelease)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'PackageTools.ps1')
$version = '0.2.4'
if($UserRequestedRelease -and $Playtest){throw 'Choose one release channel.'}
if($PlaytestBuild -and (!$Playtest -or $PlaytestBuild -notmatch '^[a-z0-9][a-z0-9-]{0,63}$')){throw 'Invalid playtest build label.'}
$env:MSBuildEnableWorkloadResolver = 'false'
$taskEvidencePath=Join-Path $root "docs/evidence/runtime-room-$version.json"
$taskEvidence=Get-Content -LiteralPath $taskEvidencePath -Raw | ConvertFrom-Json
$taskBlockers=Get-Content -LiteralPath (Join-Path $root "docs/evidence/runtime-blockers-$version.json") -Raw | ConvertFrom-Json
if($taskBlockers.version -ne $version){throw 'Wrong blocker ledger version.'}
$taskUnresolved=@($taskBlockers.findings | Where-Object {$_.resolved -ne $true})
if($taskUnresolved.Count -and !$Playtest -and !$UserRequestedRelease){throw 'Unresolved runtime blocker; no release package created.'}
$taskAuthorizationRelative="docs/evidence/playtest-authorization-$version"+$(if($PlaytestBuild){'-'+$PlaytestBuild}else{''})+'.json'
$taskAuthorizationPath=Join-Path $root $taskAuthorizationRelative
if($UserRequestedRelease){
    $taskAuthorizationRelative="docs/evidence/release-authorization-$version.json"
    $taskAuthorizationPath=Join-Path $root $taskAuthorizationRelative
    $taskAuthorization=Get-Content -LiteralPath $taskAuthorizationPath -Raw | ConvertFrom-Json
    if($taskAuthorization.version -ne $version -or $taskAuthorization.action -ne 'package-release' -or $taskAuthorization.releaseChannel -ne 'release' -or !$taskAuthorization.userRequest -or $taskAuthorization.retestOmitted -ne $true -or @($taskUnresolved | Where-Object {$_.id -notin $taskAuthorization.retainedUnresolvedFindings}).Count){throw 'Release request does not match the version, channel or retained findings.'}
    if((Get-FileHash -LiteralPath (Join-Path $root "docs/evidence/runtime-blockers-$version.json")).Hash -ne $taskAuthorization.blockerLedgerSha256){throw 'Release findings differ from the recorded scope.'}
}
if($Playtest){
    $taskAuthorization=Get-Content -LiteralPath $taskAuthorizationPath -Raw | ConvertFrom-Json
    if($taskAuthorization.version -ne $version -or $taskAuthorization.action -ne 'package-for-user-playtest' -or @($taskUnresolved | Where-Object {$_.id -notin $taskAuthorization.acceptedUnresolvedFindings}).Count){throw 'Playtest request does not cover current unresolved findings.'}
    if($PlaytestBuild -and $taskAuthorization.buildLabel -ne $PlaytestBuild){throw 'Playtest build label differs from the request record.'}
}
if($Playtest -or $UserRequestedRelease){
    foreach($taskRequired in 'HKMP.dll','HollowConnect.dll','HollowConnect.Core.dll'){
        $taskHash=(Get-FileHash -LiteralPath (Join-Path $root ('src/HollowConnect/bin/Release/net472/'+$taskRequired)) -Algorithm SHA256).Hash
        if(@($taskAuthorization.binaries | Where-Object {$_.name -eq $taskRequired -and $_.sha256 -eq $taskHash}).Count -ne 1){throw 'Production DLL differs from the requested package build.'}
    }
}
$taskScopedPlaytest=$Playtest -and $taskAuthorization.runtimeValidation
$taskRuntime=if($taskScopedPlaytest){$taskAuthorization.runtimeValidation}else{$taskEvidence}
# A user-requested package may retain earlier validation without claiming the new hashes were tested.
$taskSkipRetest=($Playtest -or $UserRequestedRelease) -and $taskAuthorization.retestOmitted -eq $true
if($taskSkipRetest){
    if(!$taskAuthorization.userRequest -or !$taskEvidence.retestOmitted -or $taskEvidence.version -ne $version -or $taskEvidence.engineVersion -ne '3.0.0-hc13' -or $taskEvidence.releaseReady -ne $false -or @($taskEvidence.runs).Count -ne 0){throw 'Invalid no-retest packaging record.'}
    if(!$taskEvidence.previousValidationSource -or (Get-FileHash -LiteralPath (Join-Path $root $taskEvidence.previousValidationSource) -Algorithm SHA256).Hash -ne $taskEvidence.previousValidationSha256){throw 'Previous validation reference differs.'}
}else{
if((!$Playtest -and $taskEvidence.releaseReady -ne $true) -or $taskRuntime.version -ne $version -or $taskRuntime.engineVersion -ne '3.0.0-hc13' -or @($taskRuntime.runs).Count -lt 1 -or (!$taskScopedPlaytest -and @($taskRuntime.runs).Count -ne 12)){throw 'Runtime acceptance is incomplete or blocked; no package created.'}
foreach($taskRun in $taskRuntime.runs){
    if(!$taskRun.bothPassed){throw 'A runtime acceptance case failed; no release package created.'}
    if(@($taskRun.binaries).Count -ne 3){throw 'Runtime DLL evidence is incomplete; no release package created.'}
    foreach($taskRequired in 'HKMP.dll','HollowConnect.dll','HollowConnect.Core.dll'){
        if(@($taskRun.binaries | Where-Object {$_.name -eq $taskRequired}).Count -ne 1){throw 'Runtime DLL evidence is missing or duplicated; no release package created.'}
    }
    & (Join-Path $root 'tests/Smoke/Assert-ProbeLogs.ps1') -RunPath (Join-Path $root ('.test-results/'+$taskRun.name)) -RequireRuntimeFiles
    foreach($taskBinary in $taskRun.binaries){
        if((Get-FileHash -LiteralPath (Join-Path $root ('src/HollowConnect/bin/Release/net472/'+$taskBinary.name)) -Algorithm SHA256).Hash -ne $taskBinary.sha256){throw 'Production DLL differs from accepted runtime; no release package created.'}
    }
}
}
& dotnet build (Join-Path $root 'src\HollowConnect.Installer\HollowConnect.Installer.csproj') -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw 'Installer build failed.' }
$taskPackageName="HollowConnect-$version"+$(if($PlaytestBuild){'-playtest-'+$PlaytestBuild}else{''})
$package = Join-Path $root ('dist\'+$taskPackageName)
$payload = Join-Path $package 'payload\HKMP'
New-Item -ItemType Directory -Force -Path $payload,(Join-Path $package 'dependencies') | Out-Null
$output = Join-Path $root 'src\HollowConnect\bin\Release\net472'
$files = 'HKMP.dll','HKMP.pdb','BouncyCastle.Cryptography.dll','HollowConnect.dll','HollowConnect.pdb','HollowConnect.Core.dll','HollowConnect.Core.pdb'
foreach ($name in $files) { Copy-Item -LiteralPath (Join-Path $output $name) -Destination (Join-Path $payload $name) -Force }
Copy-Item -LiteralPath (Join-Path $root 'vendor\moddingapi.v77.windows.zip') -Destination (Join-Path $package 'dependencies') -Force
foreach ($name in 'Install.ps1','Uninstall.ps1','GamePath.ps1','安装说明.txt') { Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination $package -Force }
$installerOutput = Join-Path $root 'src\HollowConnect.Installer\bin\Release\net472'
foreach ($name in '一键安装.exe','一键安装.exe.config') { Copy-Item -LiteralPath (Join-Path $installerOutput $name) -Destination $package -Force }
foreach ($name in 'README.md','HKMP-LICENSE','LICENSE','THIRD-PARTY.md') { Copy-Item -LiteralPath (Join-Path $root $name) -Destination $package -Force }
foreach ($taskDoc in Get-ChildItem -LiteralPath (Join-Path $root 'docs') -File -Recurse | Where-Object { $_.Name -notlike '*.draft' }) {
    if ($taskDoc.Name -in 'AGENTS.md','AGENT.md','PROJECT-HANDOFF.md') { continue }
    $taskDocTarget = Join-Path $package $taskDoc.FullName.Substring($root.Length + 1)
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $taskDocTarget) | Out-Null
    Copy-Item -LiteralPath $taskDoc.FullName -Destination $taskDocTarget -Force
}
$manifest = [ordered]@{
    version = $version
    releaseChannel = if($Playtest){'playtest'}else{'release'}
    userRequestedRelease = [bool]$UserRequestedRelease
    releaseAuthorizationSource = if($UserRequestedRelease){$taskAuthorizationRelative}else{$null}
    releaseAuthorizationSha256 = if($UserRequestedRelease){(Get-FileHash -LiteralPath $taskAuthorizationPath).Hash}else{$null}
    releaseReady = if($Playtest){$false}else{$taskEvidence.releaseReady}
    retestOmitted = [bool]$taskSkipRetest
    buildLabel = $PlaytestBuild
    runtimeValidationSource = if($Playtest -and $taskAuthorization.runtimeValidation){$taskAuthorizationRelative}else{"docs/evidence/runtime-room-$version.json"}
    knownIssues = $taskUnresolved
    playtestScope = if($Playtest){$taskAuthorization.scope}else{$null}
    playtestAuthorizationSha256 = if($Playtest){(Get-FileHash -LiteralPath $taskAuthorizationPath -Algorithm SHA256).Hash}else{$null}
    installerRevision = 3
    buildInformation = @('HKMP.dll','HollowConnect.dll','HollowConnect.Core.dll') | ForEach-Object { @{name=$_;productVersion=[Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $output $_)).ProductVersion} }
    gameVersion = '1.5.78.11833 Windows / Unity 2020.2.2f1'
    vanillaAssemblySha256 = 'FCC01E0DF1B841A8FAF6B5E39F27030F63204AD02F430B3DEFA262134AE4E8A0'
    apiAssemblySha256 = '5944411BD93830369390A4B51766EE68C4AB26195B299E25A07B5E7D0E00086D'
    apiZipSha256 = 'bc9f0db3d0916b05cd5a2420bb602fb1b239ce3ff6c289fd84bffb682fb8f1d6'
    files = @($files | ForEach-Object { @{name=$_; sha256=(Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $payload $_)).Hash} })
}
$manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $package 'manifest.json') -Encoding UTF8
$taskWindowsZip=Join-Path $root ('dist\'+$taskPackageName+'-windows.zip')
Write-HollowArchive -Destination $taskWindowsZip -BasePath $package -Files @(Get-HollowSourceFiles -Path $package)
$sources = 'src','tests','scripts','docs','README.md','LICENSE','HKMP-LICENSE','THIRD-PARTY.md','Directory.Build.props','.gitignore'
# Exclude build output and the user's game assemblies from the corresponding source archive.
$sourceZip = Join-Path $root ('dist\'+$taskPackageName+'-source.zip')
$taskSourceFiles=@(
    foreach ($name in $sources) {
        $path = Join-Path $root $name
        Get-HollowSourceFiles -Path $path
    }
)
Write-HollowArchive -Destination $sourceZip -BasePath $root -Files $taskSourceFiles
Update-HollowChecksums -Directory (Join-Path $root 'dist') -ChangedArchives ([IO.Path]::GetFileName($taskWindowsZip)),([IO.Path]::GetFileName($sourceZip)) -Rebuild:$RebuildChecksums
Write-Output "Packaged $package"
