# Scoped 0.3.1 delivery. Historical evidence and packagers remain unchanged.
param([ValidateSet('Legacy','Unity6')][string]$GameVersion,
    [switch]$VerifyOnly,[switch]$RebuildChecksums,[switch]$SteamModule,[switch]$UserRequestedRelease)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
. "$PSScriptRoot/PackageTools.ps1"
. "$PSScriptRoot/PackageDocumentation.ps1"
$version='0.3.1'
if(!$SteamModule -or !$UserRequestedRelease){throw '0.3.1 delivery requires the requested release mode and tested Steam module.'}
$taskName="HollowConnect-$version"+$(if($GameVersion -eq 'Unity6'){'-unity6'})
$package=Join-Path $root "dist/$taskName"
$taskEvidencePath="docs/evidence/development-validation-$version.json"
$taskAuthPath="docs/evidence/release-authorization-$version.json"
$taskEvidence=Get-Content "$root/$taskEvidencePath" -Raw | ConvertFrom-Json
$taskAuth=Get-Content "$root/$taskAuthPath" -Raw | ConvertFrom-Json
$taskProfile=$taskEvidence.profiles | Where-Object profile -eq $GameVersion
if($taskEvidence.version -ne $version -or $taskEvidence.core.passed -ne 15 -or $taskEvidence.core.failed -ne 0 -or
    @($taskProfile).Count -ne 1 -or @($taskProfile.runs).Count -ne 4 -or
    $taskEvidence.fullMatrixRerun -ne $false -or $taskAuth.version -ne $version -or !$taskAuth.userRequest){throw 'Incomplete scoped release evidence'}
$taskOutput=if($GameVersion -eq 'Unity6'){'bin/Unity6/Release/net472'}else{'bin/Release/net472'}
function Get-CurrentBinary([string]$Name){
    $taskProject=if($Name.StartsWith('HollowConnect.Steam.')){'HollowConnect.Steam'}else{'HollowConnect'}
    Join-Path $root "src/$taskProject/$taskOutput/$Name"
}
foreach($taskBinary in $taskProfile.production){
    if((Get-FileHash (Get-CurrentBinary $taskBinary.name)).Hash -ne $taskBinary.sha256){throw 'Production differs from final scoped tests'}
}
if(@(Compare-Object @('menus','blackwave-host','blackwave-guest','defender') @($taskProfile.runs.case)).Count){throw 'Wrong runtime case list'}
foreach($taskRun in $taskProfile.runs){
    $taskRunDir=Join-Path $root ".test-results/$($taskRun.run)"
    if($taskRun.status -ne 'passed'){throw 'Failed selected runtime case'}
    & "$root/tests/Smoke/Assert-ProbeLogs.ps1" -RunPath $taskRunDir -RequireRuntimeFiles
    $taskBinaries=Get-Content "$taskRunDir/binaries.json" -Raw | ConvertFrom-Json
    foreach($taskBinary in $taskBinaries){if(@($taskProfile.production | Where-Object {$_.name -eq $taskBinary.name -and $_.sha256 -eq $taskBinary.sha256}).Count -ne 1){throw 'Runtime hashes differ'}}
    foreach($taskRole in 'host','guest'){
        if(!(Test-Path "$taskRunDir/$taskRole.done") -or (Test-Path "$taskRunDir/$taskRole.failed") -or !(Select-String -LiteralPath "$taskRunDir/$taskRole.log" -SimpleMatch 'FINAL PASS' -Quiet)){throw 'Incomplete endpoints'}
    }
}
$taskFiles=@('HKMP.dll','HKMP.pdb','HollowConnect.dll','HollowConnect.pdb','HollowConnect.Core.dll','HollowConnect.Core.pdb','BouncyCastle.Cryptography.dll','HollowConnect.Steam.dll','HollowConnect.Steam.pdb')
$taskApiName=if($GameVersion -eq 'Unity6'){'moddingapi.unity6.468f579.windows.zip'}else{'moddingapi.v77.windows.zip'}
$taskCache=Join-Path $root 'vendor/unity6-api/468f579f25e63da2188f39fe5ece15c25d2854d7'
if(!$VerifyOnly){
    if(Test-Path $package){throw 'Preserve an existing delivery; verify it or archive the unpacked folder explicitly first'}
    New-Item -ItemType Directory -Path "$package/payload/HKMP","$package/dependencies" -Force | Out-Null
    foreach($taskFile in $taskFiles){Copy-Item (Get-CurrentBinary $taskFile) "$package/payload/HKMP/$taskFile"}
    if($GameVersion -eq 'Unity6'){
        $taskApiRecord=Get-Content "$taskCache/build.json" -Raw | ConvertFrom-Json
        foreach($taskFile in $taskApiRecord.files){if((Get-FileHash "$taskCache/payload/$($taskFile.name)").Hash -ne $taskFile.sha256){throw 'Unity6 API cache differs'}}
        Write-HollowArchive -Destination "$package/dependencies/$taskApiName" -BasePath "$taskCache/payload" -Files @(Get-ChildItem "$taskCache/payload" -File)
        Copy-Item "$taskCache/build.json" "$package/dependencies/api-build.json"
    }else{Copy-Item "$root/vendor/$taskApiName" "$package/dependencies/$taskApiName"}
    foreach($taskFile in 'Install.ps1','Uninstall.ps1','GamePath.ps1','安装说明.txt'){Copy-Item "$PSScriptRoot/$taskFile" $package}
    foreach($taskFile in '一键安装.exe','一键安装.exe.config'){Copy-Item "$root/src/HollowConnect.Installer/bin/Release/net472/$taskFile" $package}
    Copy-Item "$package/一键安装.exe" "$package/HollowConnectInstaller.exe"
    Copy-Item "$package/一键安装.exe.config" "$package/HollowConnectInstaller.exe.config"
    foreach($taskFile in 'README.md','HKMP-LICENSE','LICENSE','THIRD-PARTY.md'){Copy-Item "$root/$taskFile" $package}
    Copy-HollowPackageDocumentation -Root $root -Destination $package -Version $version -AuthorizationPath $taskAuthPath
    Update-HollowPackageMarkdownLinks -Root $root -Package $package
    $manifest=[ordered]@{
        version=$version;displayVersion="$version ($GameVersion)";gameProfile=$GameVersion;releaseChannel='release';userRequestedRelease=$true;
        validationScope='related core, installer, menus and two native blackwave routes per profile';fullMatrixRerun=$false;retestOmitted=$false;
        gameVersion=$taskProfile.gameVersion;optionalModules=@('Steam');
        vanillaAssemblySha256=$taskProfile.vanillaAssemblySha256;apiAssemblySha256=$taskProfile.apiAssemblySha256;
        apiArchive=$taskApiName;apiZipSha256=(Get-FileHash "$package/dependencies/$taskApiName").Hash;
        runtimeValidationSource=$taskEvidencePath;runtimeValidationSha256=(Get-FileHash "$root/$taskEvidencePath").Hash;
        releaseAuthorizationSource=$taskAuthPath;releaseAuthorizationSha256=(Get-FileHash "$root/$taskAuthPath").Hash;
        knownIssues=@($taskEvidence.knownIssues);files=@($taskFiles | ForEach-Object {@{name=$_;sha256=(Get-FileHash "$package/payload/HKMP/$_").Hash}})
    }
    $manifest | ConvertTo-Json -Depth 15 | Set-Content "$package/manifest.json" -Encoding utf8
    Write-HollowArchive -Destination "$root/dist/$taskName-windows.zip" -BasePath $package -Files @(Get-HollowSourceFiles $package)
    $taskSources=@(
        foreach($taskPath in 'src','tests','scripts','README.md','LICENSE','HKMP-LICENSE','THIRD-PARTY.md','Directory.Build.props','Directory.Build.targets','.gitignore'){Get-HollowSourceFiles "$root/$taskPath"}
        Get-HollowDocumentationFiles -Root $root -Version $version -Audience Repository -AuthorizationPath $taskAuthPath
    )
    Write-HollowArchive -Destination "$root/dist/$taskName-source.zip" -BasePath $root -Files $taskSources
    Update-HollowChecksums -Directory "$root/dist" -ChangedArchives "$taskName-windows.zip","$taskName-source.zip" -Rebuild:$RebuildChecksums
}
$manifest=Get-Content "$package/manifest.json" -Raw | ConvertFrom-Json
if($manifest.version -ne $version -or $manifest.gameProfile -ne $GameVersion -or $manifest.releaseChannel -ne 'release' -or
    $manifest.fullMatrixRerun -ne $false -or $manifest.runtimeValidationSha256 -ne (Get-FileHash "$root/$taskEvidencePath").Hash -or
    $manifest.releaseAuthorizationSha256 -ne (Get-FileHash "$root/$taskAuthPath").Hash){throw 'Wrong package identity/evidence'}
if(@(Compare-Object $taskFiles @($manifest.files.name)).Count -or @(Compare-Object $taskFiles @(Get-ChildItem "$package/payload/HKMP" -File | ForEach-Object Name)).Count){throw 'Wrong payload selection'}
foreach($taskFile in $manifest.files){if((Get-FileHash "$package/payload/HKMP/$($taskFile.name)").Hash -ne $taskFile.sha256 -or (Get-FileHash (Get-CurrentBinary $taskFile.name)).Hash -ne $taskFile.sha256){throw 'Stale production payload'}}
if((Get-FileHash "$package/dependencies/$taskApiName").Hash -ne $manifest.apiZipSha256){throw 'API ZIP differs'}
$taskApiZip=[IO.Compression.ZipFile]::OpenRead("$package/dependencies/$taskApiName")
try{
    $taskAssembly=$taskApiZip.Entries | Where-Object FullName -eq 'Assembly-CSharp.dll'
    $taskStream=$taskAssembly.Open();$taskHasher=[Security.Cryptography.SHA256]::Create()
    try{$taskApiHash=[BitConverter]::ToString($taskHasher.ComputeHash($taskStream)).Replace('-','')}finally{$taskStream.Dispose();$taskHasher.Dispose()}
    if($taskApiHash -ne $taskProfile.apiAssemblySha256){throw 'Wrong profile API'}
}finally{$taskApiZip.Dispose()}
foreach($taskKind in 'windows','source'){
    $taskZip=[IO.Compression.ZipFile]::OpenRead("$root/dist/$taskName-$taskKind.zip")
    try{
        $taskEntries=@($taskZip.Entries | Where-Object Name)
        foreach($taskEntry in $taskEntries){
            $taskRelative=$taskEntry.FullName
            if($taskRelative -match '(^|/)(vendor|bin|obj|\.analysis|\.test-results)/|HollowConnect\.(Smoke|SteamProbe)\.dll|user\d+.*\.dat$|\.draft$' -or ($taskKind -eq 'source' -and $taskRelative -match '\.(dll|exe|pdb)$')){throw "Private/test/build file: $taskRelative"}
            $taskBase=if($taskKind -eq 'windows'){$package}else{$root}
            $taskStream=$taskEntry.Open();$taskHasher=[Security.Cryptography.SHA256]::Create()
            try{$taskHash=[BitConverter]::ToString($taskHasher.ComputeHash($taskStream)).Replace('-','')}finally{$taskStream.Dispose();$taskHasher.Dispose()}
            if($taskHash -ne (Get-FileHash "$taskBase/$taskRelative").Hash){throw "Stale ZIP entry: $taskRelative"}
        }
        if($taskKind -eq 'windows'){
            if(@(Compare-Object @((Get-HollowSourceFiles $package).FullName | ForEach-Object {$_.Substring($package.Length+1).Replace('\','/')} ) @($taskEntries.FullName)).Count){throw 'ZIP file list differs from package'}
        }
    }finally{$taskZip.Dispose()}
    $taskHash=(Get-FileHash "$root/dist/$taskName-$taskKind.zip").Hash.ToLowerInvariant()
    if(!(Get-Content "$root/dist/SHA256SUMS.txt" | Where-Object {$_ -eq "$taskHash  $taskName-$taskKind.zip"})){throw 'Checksum index differs'}
}
Write-Output "PASS $GameVersion 0.3.1: scoped runtime evidence, payload/API, both ZIPs and checksums verified."
