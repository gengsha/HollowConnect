param([switch]$SteamModule, [switch]$VerifyOnly, [switch]$RebuildChecksums,
    [ValidateSet('','hit-evasion')][string]$Revision='')
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'PackageTools.ps1')
. (Join-Path $PSScriptRoot 'PackageDocumentation.ps1')
$version=if($Revision){'0.2.8'}else{'0.2.9'}
$taskName="HollowConnect-$version-unity6"+$(if($Revision){'-'+$Revision}else{''})
$package=Join-Path $root "dist/$taskName"
$taskValidationSource="docs/evidence/development-validation-$version-unity6.json"
$taskRequestSource="docs/evidence/package-request-$version-unity6.json"
$taskNotes="docs/UPDATE-$version.md"
if($Revision -eq 'hit-evasion'){
    $taskValidationSource='docs/evidence/development-validation-hit-evasion-20261113.json'
    $taskRequestSource='docs/evidence/package-request-0.2.8-unity6-hit-evasion.json'
    $taskNotes='docs/HIT-EVASION-0.2.8.md'
}
$taskLicense='docs/UNITY6-API-LICENSE.txt'
$taskBlockerSource="docs/evidence/runtime-blockers-$version.json"
$taskValidation=Get-Content (Join-Path $root $taskValidationSource) -Raw | ConvertFrom-Json
$taskRequest=Get-Content (Join-Path $root $taskRequestSource) -Raw | ConvertFrom-Json
$taskBlockers=Get-Content (Join-Path $root $taskBlockerSource) -Raw | ConvertFrom-Json
$taskCommit='468f579f25e63da2188f39fe5ece15c25d2854d7'
$taskApiHash='DF301E643028BD5C96FE5034138B8694C08FBFA685947B93D6401EE3FCC0CD46'
$taskVanillaHash='E9048EF6A633970F735E01EC166D3959F610EAEA7A88D827D48D67B1E5FB87BD'
$taskApiName='moddingapi.unity6.468f579.windows.zip'
$taskPayloadNames=@('HKMP.dll','HKMP.pdb','BouncyCastle.Cryptography.dll','HollowConnect.dll','HollowConnect.pdb','HollowConnect.Core.dll','HollowConnect.Core.pdb')
if($SteamModule){$taskPayloadNames+=@('HollowConnect.Steam.dll','HollowConnect.Steam.pdb')}
function Get-Unity6BinaryPath([string]$Name){
    $taskProject=if($Name.StartsWith('HollowConnect.Steam.')){'HollowConnect.Steam'}else{'HollowConnect'}
    Join-Path $root "src/$taskProject/bin/Unity6/Release/net472/$Name"
}
function Assert-Unity6Record {
    if($taskRequest.version -ne $version -or $taskRequest.gameProfile -ne 'Unity6' -or
        $taskRequest.action -ne 'package-local' -or !$taskRequest.userRequest -or
        $taskRequest.validationSha256 -ne (Get-FileHash (Join-Path $root $taskValidationSource)).Hash -or
        $taskRequest.blockerLedgerSha256 -ne (Get-FileHash (Join-Path $root $taskBlockerSource)).Hash){throw 'Unity6 package request/evidence differs.'}
    if($taskValidation.profile -ne 'Unity6' -or $taskValidation.api.commit -ne $taskCommit -or
        $taskValidation.api.assemblySha256 -ne $taskApiHash){throw 'Wrong Unity6 validation scope.'}
    $taskRoomNames=@('native-host','native-guest','lifecycle-host','lifecycle-guest','boss','boss-late','grimm-host','grimm-guest','rescue','death-story','full','join' | ForEach-Object {"final-$_-013"})
    $taskDirectNames='hitstop-host','hitstop-guest','feedback','balance','resource-fonts','ranking','notification-fonts','steam-sockets'
    $taskExpectedCases=@($taskRoomNames+$taskDirectNames)
    if($version -eq '0.2.9'){
        $taskExpectedCases=@('crystal-host','crystal-guest','evasion-host')
        if($taskValidation.version -ne $version -or $taskValidation.corePassed -ne 17 -or
            $taskValidation.coreFailed -ne 0 -or $taskValidation.fullMatrixRerun -ne $false){throw 'Wrong 0.2.9 scoped validation record.'}
    }
    if($Revision -eq 'hit-evasion'){
        $taskExpectedCases=@('evasion-host','evasion-guest','balance','nailarts')
        if($taskValidation.revision -ne $Revision -or $taskRequest.revision -ne $Revision -or
            $taskValidation.corePassed -ne 17 -or $taskValidation.legacyRuntimeTested -ne $false -or
            $taskValidation.baselineValidationSource -ne 'docs/evidence/development-validation-unity6-20261111.json' -or
            $taskValidation.baselineValidationSha256 -ne (Get-FileHash (Join-Path $root $taskValidation.baselineValidationSource)).Hash){throw 'Wrong hit-evasion revision/baseline record.'}
    }
    if(@($taskValidation.checks).Count -ne $taskExpectedCases.Count -or
        @(Compare-Object $taskExpectedCases @($taskValidation.checks.case)).Count){throw 'Unity6 acceptance case list differs.'}
    if(@(Compare-Object @('HKMP.dll','HollowConnect.dll','HollowConnect.Core.dll','HollowConnect.Steam.dll') @($taskValidation.production.name)).Count){throw 'Wrong production binary selection.'}
    foreach($taskBinary in $taskValidation.production){
        if((Get-FileHash (Get-Unity6BinaryPath $taskBinary.name)).Hash -ne $taskBinary.sha256 -or
            @($taskRequest.binaries | Where-Object {$_.name -eq $taskBinary.name -and $_.sha256 -eq $taskBinary.sha256}).Count -ne 1){throw 'Unity6 production differs from tested/requested build.'}
    }
    foreach($taskCheck in $taskValidation.checks){
        if($taskCheck.state -ne 'passed' -or !$taskCheck.bothEndpoints -or !$taskCheck.strictLogs -or !$taskCheck.productionHashes){throw 'Incomplete Unity6 runtime case.'}
        if($taskCheck.run -notmatch '^\.test-results/[a-z0-9-]+$'){throw 'Invalid runtime evidence path.'}
        $taskRun=Join-Path $root $taskCheck.run
        & (Join-Path $root 'tests/Smoke/Assert-ProbeLogs.ps1') -RunPath $taskRun -RequireRuntimeFiles
        foreach($taskRole in 'host','guest'){
            if(!(Test-Path (Join-Path $taskRun "$taskRole.done")) -or !(Select-String -LiteralPath (Join-Path $taskRun "$taskRole.log") -SimpleMatch 'FINAL PASS' -Quiet)){throw 'Runtime endpoint incomplete.'}
        }
        foreach($taskBinary in (Get-Content (Join-Path $taskRun 'binaries.json') -Raw | ConvertFrom-Json)){
            if(@($taskValidation.production | Where-Object {$_.name -eq $taskBinary.name -and $_.sha256 -eq $taskBinary.sha256}).Count -ne 1){throw 'Runtime production hash differs.'}
        }
    }
}
Assert-Unity6Record
if(!$VerifyOnly){
    if(Test-Path $package){throw 'Preserve existing Unity6 package directory before generating another copy.'}
    foreach($taskKind in 'windows','source'){if(Test-Path (Join-Path $root "dist/$taskName-$taskKind.zip")){throw 'Preserve existing Unity6 ZIP before regeneration.'}}
    $taskCache=Join-Path $root "vendor/unity6-api/$taskCommit"
    $taskApiRecord=Get-Content (Join-Path $taskCache 'build.json') -Raw | ConvertFrom-Json
    if($taskApiRecord.commit -ne $taskCommit -or $taskApiRecord.sourceSha256 -ne 'FDFF2B2E13E8C8C679D3E19FD0731A79883BB11DEB0F7E4E8E615A5D6FE10E07'){throw 'Wrong API source record.'}
    if(@($taskApiRecord.files | Where-Object {$_.name -eq 'Assembly-CSharp.dll' -and $_.sha256 -eq $taskApiHash}).Count -ne 1){throw 'Wrong API assembly.'}
    foreach($taskFile in $taskApiRecord.files){
        if($taskFile.name -notmatch '^[\w.-]+$' -or (Get-FileHash (Join-Path $taskCache "payload/$($taskFile.name)")).Hash -ne $taskFile.sha256){throw 'Cached API payload differs.'}
    }
    & (Join-Path $PSScriptRoot 'Build.ps1') -Target Installer
    $taskPayload=Join-Path $package 'payload/HKMP'
    New-Item -ItemType Directory -Path $taskPayload,(Join-Path $package 'dependencies') -Force | Out-Null
    foreach($taskFile in $taskPayloadNames){Copy-Item -LiteralPath (Get-Unity6BinaryPath $taskFile) -Destination $taskPayload}
    $taskApiFiles=@($taskApiRecord.files | ForEach-Object {Get-Item -LiteralPath (Join-Path $taskCache "payload/$($_.name)")})
    Write-HollowArchive -Destination (Join-Path $package "dependencies/$taskApiName") -BasePath (Join-Path $taskCache 'payload') -Files $taskApiFiles
    Copy-Item -LiteralPath (Join-Path $taskCache 'build.json') -Destination (Join-Path $package 'dependencies/api-build.json')
    foreach($taskFile in 'Install.ps1','Uninstall.ps1','GamePath.ps1'){Copy-Item -LiteralPath (Join-Path $PSScriptRoot $taskFile) -Destination $package}
    foreach($taskFile in '一键安装.exe','一键安装.exe.config'){Copy-Item -LiteralPath (Join-Path $root "src/HollowConnect.Installer/bin/Release/net472/$taskFile") -Destination $package}
    foreach($taskFile in 'HKMP-LICENSE','LICENSE','THIRD-PARTY.md'){Copy-Item -LiteralPath (Join-Path $root $taskFile) -Destination $package}
    Copy-HollowPackageDocumentation -Root $root -Destination $package -Version $version -AuthorizationPath $taskRequestSource
    # Notes live in docs/; their README copy lives one directory higher.
    $taskReadme=[regex]::Replace([IO.File]::ReadAllText((Join-Path $root $taskNotes)),
        '\]\((?![a-zA-Z][a-zA-Z0-9+.-]*:|#|/)([^)]+)\)', '](docs/$1)')
    [IO.File]::WriteAllText((Join-Path $package 'README.md'),$taskReadme,[Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText((Join-Path $package '安装说明.txt'),[IO.File]::ReadAllText((Join-Path $root $taskNotes)),[Text.UTF8Encoding]::new($true))
    Update-HollowPackageMarkdownLinks -Root $root -Package $package
    $taskKnownIssues=@($taskBlockers.findings | Where-Object resolved -ne $true)
    $manifest=[ordered]@{
        version=$version;displayVersion="$version (Unity6)";gameProfile='Unity6';releaseChannel='local';releaseReady=$false;retestOmitted=$false;
        revision=$Revision;validationScope=$taskValidation.scope;
        gameVersion='1.5.12620 Windows / Unity 6000.0.61f1';optionalModules=@(if($SteamModule){'Steam'});
        vanillaAssemblySha256=$taskVanillaHash;apiAssemblySha256=$taskApiHash;apiCommit=$taskCommit;apiArchive=$taskApiName;
        apiZipSha256=(Get-FileHash (Join-Path $package "dependencies/$taskApiName")).Hash;
        apiBuildSha256=(Get-FileHash (Join-Path $package 'dependencies/api-build.json')).Hash;
        runtimeValidationSource=$taskValidationSource;runtimeValidationSha256=(Get-FileHash (Join-Path $root $taskValidationSource)).Hash;
        packageRequestSource=$taskRequestSource;packageRequestSha256=(Get-FileHash (Join-Path $root $taskRequestSource)).Hash;
        blockerLedgerSource=$taskBlockerSource;blockerLedgerSha256=(Get-FileHash (Join-Path $root $taskBlockerSource)).Hash;
        knownIssues=$taskKnownIssues;apiStartupLimitation=$taskValidation.apiOnlyTitleBaseline;installerRevision=4;
        files=@($taskPayloadNames | ForEach-Object {@{name=$_;sha256=(Get-FileHash (Join-Path $taskPayload $_)).Hash}})
    }
    $manifest | ConvertTo-Json -Depth 10 | Set-Content (Join-Path $package 'manifest.json') -Encoding utf8
    Write-HollowArchive -Destination (Join-Path $root "dist/$taskName-windows.zip") -BasePath $package -Files @(Get-HollowSourceFiles -Path $package)
    $taskSourceFiles=@(
        foreach($taskPath in 'src','tests','scripts','README.md','LICENSE','HKMP-LICENSE','THIRD-PARTY.md','Directory.Build.props','Directory.Build.targets','.gitignore'){
            Get-HollowSourceFiles -Path (Join-Path $root $taskPath)
        }
        Get-HollowDocumentationFiles -Root $root -Version $version -Audience Repository -AuthorizationPath $taskRequestSource
    )
    Write-HollowArchive -Destination (Join-Path $root "dist/$taskName-source.zip") -BasePath $root -Files $taskSourceFiles
    Update-HollowChecksums -Directory (Join-Path $root 'dist') -ChangedArchives "$taskName-windows.zip","$taskName-source.zip" -Rebuild:$RebuildChecksums
}
$manifest=Get-Content (Join-Path $package 'manifest.json') -Raw | ConvertFrom-Json
if($manifest.version -ne $version -or ([string]$manifest.revision) -ne $Revision -or $manifest.gameProfile -ne 'Unity6' -or $manifest.releaseChannel -ne 'local' -or $manifest.releaseReady -ne $false -or $manifest.retestOmitted -ne $false -or
    $manifest.apiCommit -ne $taskCommit -or $manifest.apiAssemblySha256 -ne $taskApiHash -or $manifest.vanillaAssemblySha256 -ne $taskVanillaHash -or
    $manifest.apiArchive -ne $taskApiName -or [bool]('Steam' -in @($manifest.optionalModules)) -ne [bool]$SteamModule){throw 'Wrong Unity6 package identity.'}
foreach($taskPair in @(@('runtimeValidationSource','runtimeValidationSha256',$taskValidationSource),@('packageRequestSource','packageRequestSha256',$taskRequestSource),@('blockerLedgerSource','blockerLedgerSha256',$taskBlockerSource))){
    if($manifest.($taskPair[0]) -ne $taskPair[2]){throw 'Wrong package evidence path.'}
    foreach($taskBase in $root,$package){if((Get-FileHash (Join-Path $taskBase $taskPair[2])).Hash -ne $manifest.($taskPair[1])){throw 'Package evidence hash differs.'}}
}
if(@(Compare-Object $taskPayloadNames @($manifest.files.name)).Count -or @(Compare-Object $taskPayloadNames @(Get-ChildItem (Join-Path $package 'payload/HKMP') -File | ForEach-Object Name)).Count){throw 'Wrong package payload selection.'}
foreach($taskFile in $manifest.files){if((Get-FileHash (Join-Path $package "payload/HKMP/$($taskFile.name)")).Hash -ne $taskFile.sha256 -or (Get-FileHash (Get-Unity6BinaryPath $taskFile.name)).Hash -ne $taskFile.sha256){throw 'Package binary differs from production.'}}
if((Get-FileHash (Join-Path $package "dependencies/$taskApiName")).Hash -ne $manifest.apiZipSha256 -or (Get-FileHash (Join-Path $package 'dependencies/api-build.json')).Hash -ne $manifest.apiBuildSha256){throw 'Packaged API checksum differs.'}
$taskApiRecord=Get-Content (Join-Path $package 'dependencies/api-build.json') -Raw | ConvertFrom-Json
if($taskApiRecord.commit -ne $taskCommit -or @($taskApiRecord.files | Where-Object {$_.name -eq 'Assembly-CSharp.dll' -and $_.sha256 -eq $taskApiHash}).Count -ne 1){throw 'Packaged API record does not match the validated API.'}
if(@(Compare-Object @($taskBlockers.findings | Where-Object resolved -ne $true) @($manifest.knownIssues) -Property id,resolved).Count){throw 'Packaged known issues differ from the retained ledger.'}
$taskApiZip=[IO.Compression.ZipFile]::OpenRead((Join-Path $package "dependencies/$taskApiName"))
function Hash-Unity6Stream($Stream){$taskHasher=[Security.Cryptography.SHA256]::Create();try{[BitConverter]::ToString($taskHasher.ComputeHash($Stream)).Replace('-','')}finally{$taskHasher.Dispose()}}
try{
    if(@(Compare-Object @($taskApiRecord.files.name) @($taskApiZip.Entries.FullName)).Count){throw 'Wrong API ZIP entries.'}
    foreach($taskFile in $taskApiRecord.files){$taskStream=$taskApiZip.GetEntry($taskFile.name).Open();try{if((Hash-Unity6Stream $taskStream) -ne $taskFile.sha256){throw 'API ZIP entry differs.'}}finally{$taskStream.Dispose()}}
}finally{$taskApiZip.Dispose()}
$taskCounts=@{}
foreach($taskKind in 'windows','source'){
    $taskZipPath=Join-Path $root "dist/$taskName-$taskKind.zip"
    $taskZip=[IO.Compression.ZipFile]::OpenRead($taskZipPath)
    try{
        $taskEntries=@($taskZip.Entries | Where-Object Name)
        $taskCounts[$taskKind]=$taskEntries.Count
        foreach($taskEntry in $taskEntries){
            $taskRelative=$taskEntry.FullName
            if($taskRelative -match '(^|/)(\.test-results|\.analysis|vendor|bin|obj)/|HollowConnect\.(Smoke|SteamProbe)\.(dll|pdb)$|user\d+.*\.dat$|\.draft$' -or $taskRelative -match '(^|/)\.\./'){throw 'Private/test file in package.'}
            if($taskKind -eq 'source' -and $taskRelative -match '\.(dll|exe|pdb)$'){throw 'Binary in source ZIP.'}
            $taskBase=if($taskKind -eq 'windows'){$package}else{$root}
            $taskStream=$taskEntry.Open()
            try{if((Hash-Unity6Stream $taskStream) -ne (Get-FileHash (Join-Path $taskBase $taskRelative)).Hash){throw "Stale $taskKind ZIP entry: $taskRelative"}}finally{$taskStream.Dispose()}
        }
        $taskRequired=if($taskKind -eq 'windows'){@('manifest.json','Install.ps1','GamePath.ps1','一键安装.exe',"dependencies/$taskApiName",$taskNotes,$taskLicense)}else{@('Directory.Build.targets','scripts/Prepare-Unity6.ps1','scripts/Package-Unity6.ps1',$taskValidationSource,$taskRequestSource,$taskNotes,$taskLicense)}
        foreach($taskPath in $taskRequired){if($taskPath -notin $taskEntries.FullName){throw "Missing $taskKind ZIP entry: $taskPath"}}
    }finally{$taskZip.Dispose()}
    $taskHash=(Get-FileHash $taskZipPath).Hash.ToLowerInvariant()
    if(!(Get-Content (Join-Path $root 'dist/SHA256SUMS.txt') | Where-Object {$_ -eq "$taskHash  $taskName-$taskKind.zip"})){throw 'ZIP checksum index differs.'}
}
Write-Output "PASS Unity6 $version package: $(@($taskValidation.checks).Count) accepted dual-client cases; $($manifest.files.Count) payload files; $(@($taskApiRecord.files).Count) matched API files; $($taskCounts.windows) Windows / $($taskCounts.source) source ZIP entries; all content/checksums verified."
Write-Output (Join-Path $root "dist/$taskName-windows.zip")
