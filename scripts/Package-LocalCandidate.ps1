param(
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+-rc[1-9][0-9]*$')][string]$Version,
    [ValidateSet('Both','Legacy','Unity6')][string]$GameVersion='Both',
    [switch]$SteamModule, [switch]$RebuildChecksums
)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
. "$PSScriptRoot/PackageTools.ps1"
. "$PSScriptRoot/PackageDocumentation.ps1"
$dist=Join-Path $root 'dist'
$profiles=if($GameVersion -eq 'Both'){@('Legacy','Unity6')}else{@($GameVersion)}
$referenceVersion='0.3.1'
# Read the pinned game compatibility metadata without assigning its tests to the candidate.
$reference=Get-Content "$root/docs/evidence/development-validation-$referenceVersion.json" -Raw | ConvertFrom-Json
$legacyApi='moddingapi.v77.windows.zip'
$unityApi='moddingapi.unity6.468f579.windows.zip'
$cache=Join-Path $root 'vendor/unity6-api/468f579f25e63da2188f39fe5ece15c25d2854d7'
foreach($profile in $profiles){
    $name="HollowConnect-$Version"+$(if($profile -eq 'Unity6'){'-unity6'})
    foreach($file in "$name-windows.zip","$name-source.zip"){
        if(Test-Path -LiteralPath "$dist/$file"){throw "Package already exists: $file"}
    }
    $output=if($profile -eq 'Unity6'){'bin/Unity6/Release/net472'}else{'bin/Release/net472'}
    foreach($module in @('HollowConnect')+@(if($SteamModule){'HollowConnect.Steam'})){
        $dll=Get-Item -LiteralPath "$root/src/$module/$output/$module.dll"
        if(($dll.VersionInfo.ProductVersion -split '\+',2)[0] -ne $Version){
            throw "$($dll.FullName) does not match $Version."
        }
    }
}
if($profiles -contains 'Legacy' -and (Get-FileHash "$root/vendor/$legacyApi").Hash -ne 'BC9F0DB3D0916B05CD5A2420BB602FB1B239CE3FF6C289FD84BFFB682FB8F1D6'){
    throw 'Legacy API archive checksum differs.'
}
if($profiles -contains 'Unity6'){
    $cacheRecord=Get-Content "$cache/build.json" -Raw | ConvertFrom-Json
    foreach($file in $cacheRecord.files){
        if((Get-FileHash "$cache/payload/$($file.name)").Hash -ne $file.sha256){throw "Unity6 API cache differs: $($file.name)"}
    }
}
& "$PSScriptRoot/Build.ps1" -Target Installer

function Get-ArchiveEntryHash($entry){
    $stream=$entry.Open()
    $hasher=[Security.Cryptography.SHA256]::Create()
    try { [BitConverter]::ToString($hasher.ComputeHash($stream)).Replace('-','') }
    finally { $stream.Dispose(); $hasher.Dispose() }
}
function Confirm-Archive($archive,$base,$files){
    $expected=@{}
    foreach($file in $files){
        $relative=$file.FullName.Substring($base.Length+1).Replace('\','/')
        $expected.Add($relative,$file)
    }
    $zip=[IO.Compression.ZipFile]::OpenRead($archive)
    try {
        if($zip.Entries.Count -ne $expected.Count){throw "File count differs: $archive"}
        foreach($entry in $zip.Entries){
            if($entry.FullName -match '(^|/)(vendor|bin|obj|\.analysis|\.test-results)/|HollowConnect\.(Smoke|SteamProbe)\.dll|user\d+.*\.dat$' -or
                !$expected.ContainsKey($entry.FullName) -or
                (Get-ArchiveEntryHash $entry) -ne (Get-FileHash -LiteralPath $expected[$entry.FullName].FullName).Hash){
                throw "Unexpected or changed file in $archive : $($entry.FullName)"
            }
            $expected.Remove($entry.FullName)
        }
    } finally { $zip.Dispose() }
}

$sources=@(
    foreach($path in 'src','tests','scripts','README.md','LICENSE','HKMP-LICENSE','THIRD-PARTY.md','Directory.Build.props','Directory.Build.targets','.gitignore'){
        Get-HollowSourceFiles "$root/$path"
    }
    Get-HollowDocumentationFiles -Root $root -Version $referenceVersion -Audience Repository
)
if($sources | Where-Object Extension -in '.dll','.exe','.pdb'){throw 'Build output in source selection'}
$stage=Join-Path $dist ('.candidate-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stage | Out-Null
$archives=@()
try {
    foreach($profile in $profiles){
        $metadata=$reference.profiles | Where-Object profile -eq $profile
        $name="HollowConnect-$Version"+$(if($profile -eq 'Unity6'){'-unity6'})
        $package=Join-Path $stage $name
        $payload=Join-Path $package 'payload/HKMP'
        New-Item -ItemType Directory -Path $payload,"$package/dependencies" -Force | Out-Null
        $output=if($profile -eq 'Unity6'){'bin/Unity6/Release/net472'}else{'bin/Release/net472'}
        $payloadFiles=@('HKMP.dll','HKMP.pdb','HollowConnect.dll','HollowConnect.pdb','HollowConnect.Core.dll','HollowConnect.Core.pdb','BouncyCastle.Cryptography.dll')
        foreach($file in $payloadFiles){Copy-Item -LiteralPath "$root/src/HollowConnect/$output/$file" -Destination $payload}
        if($SteamModule){
            foreach($file in 'HollowConnect.Steam.dll','HollowConnect.Steam.pdb'){
                Copy-Item -LiteralPath "$root/src/HollowConnect.Steam/$output/$file" -Destination $payload
            }
        }
        $apiName=if($profile -eq 'Unity6'){$unityApi}else{$legacyApi}
        $apiPath=Join-Path $package "dependencies/$apiName"
        if($profile -eq 'Unity6'){
            $apiFiles=@($cacheRecord.files | ForEach-Object { Get-Item -LiteralPath "$cache/payload/$($_.name)" })
            Write-HollowArchive -Destination $apiPath -BasePath "$cache/payload" -Files $apiFiles
            Copy-Item -LiteralPath "$cache/build.json" -Destination "$package/dependencies/api-build.json"
        }else{
            Copy-Item -LiteralPath "$root/vendor/$legacyApi" -Destination $apiPath
        }
        $apiZip=[IO.Compression.ZipFile]::OpenRead($apiPath)
        try {
            if((Get-ArchiveEntryHash $apiZip.GetEntry('Assembly-CSharp.dll')) -ne $metadata.apiAssemblySha256){
                throw "Wrong API version for $profile"
            }
        } finally { $apiZip.Dispose() }
        foreach($file in 'Install.ps1','Uninstall.ps1','GamePath.ps1','安装说明.txt'){
            Copy-Item -LiteralPath "$PSScriptRoot/$file" -Destination $package
        }
        foreach($file in '一键安装.exe','一键安装.exe.config'){
            Copy-Item -LiteralPath "$root/src/HollowConnect.Installer/bin/Release/net472/$file" -Destination $package
        }
        Copy-Item -LiteralPath "$package/一键安装.exe" -Destination "$package/HollowConnectInstaller.exe"
        Copy-Item -LiteralPath "$package/一键安装.exe.config" -Destination "$package/HollowConnectInstaller.exe.config"
        foreach($file in 'README.md','HKMP-LICENSE','LICENSE','THIRD-PARTY.md'){
            Copy-Item -LiteralPath "$root/$file" -Destination $package
        }
        Copy-HollowPackageDocumentation -Root $root -Destination $package -Version $referenceVersion
        $readme=[IO.File]::ReadAllText("$package/README.md") -replace '(?m)^\*\*当前版本：.*$',"**本地候选版 $Version** · $($metadata.gameVersion)"
        $readme=$readme -replace '(?m)^下载匹配游戏版本的完整包：.*$','请完整解压此 Windows 安装包，关闭游戏，再运行“一键安装.exe”。'
        Set-Content -LiteralPath "$package/README.md" -Value $readme -Encoding utf8
        $instructions=[IO.File]::ReadAllText("$package/安装说明.txt") -replace '(?m)^HollowConnect .*$',"HollowConnect $Version"
        $instructions=$instructions -replace '(?m)^网络标识：.*$',"网络标识：$Version。所有玩家更新后重新开房。"
        $instructions=$instructions -replace '(?m)^本包适用游戏 .*$',"适用游戏：$($metadata.gameVersion)"
        $instructions=$instructions -replace '(?m)^本版执行.*$','本地候选版已核对安装包内容和校验和。'
        $instructions=$instructions -replace '；本版范围见 docs\\UPDATE-0\.2\.9\.md',''
        Set-Content -LiteralPath "$package/安装说明.txt" -Value $instructions -Encoding utf8
        Update-HollowPackageMarkdownLinks -Root $root -Package $package
        [ordered]@{
            version=$Version; displayVersion="$Version ($profile)"; gameProfile=$profile; gameVersion=$metadata.gameVersion
            releaseChannel='prerelease'; validationScope='Archive and payload integrity only'; retestOmitted=$true; fullMatrixRerun=$false
            optionalModules=@(if($SteamModule){'Steam'})
            vanillaAssemblySha256=$metadata.vanillaAssemblySha256; apiAssemblySha256=$metadata.apiAssemblySha256
            apiArchive=$apiName; apiZipSha256=(Get-FileHash -LiteralPath $apiPath).Hash
            files=@(Get-ChildItem -LiteralPath $payload -File | ForEach-Object {
                @{name=$_.Name;sha256=(Get-FileHash -LiteralPath $_.FullName).Hash}
            })
        } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath "$package/manifest.json" -Encoding utf8
        $windowsFiles=@(Get-HollowSourceFiles $package)
        foreach($kind in 'windows','source'){
            $file="$name-$kind.zip"
            $base=if($kind -eq 'windows'){$package}else{$root}
            $selection=if($kind -eq 'windows'){$windowsFiles}else{$sources}
            Write-HollowArchive -Destination "$stage/$file" -BasePath $base -Files $selection
            Confirm-Archive "$stage/$file" $base $selection
            $archives+=$file
        }
        Write-Output "Verified $Version ($profile)"
    }
    foreach($file in $archives){
        Move-Item -LiteralPath "$stage/$file" -Destination "$dist/$file" -ErrorAction Stop
    }
    Update-HollowChecksums -Directory $dist -ChangedArchives $archives -Rebuild:$RebuildChecksums
    $sums=foreach($file in $archives){
        '{0}  {1}' -f (Get-FileHash -LiteralPath "$dist/$file").Hash.ToLowerInvariant(),$file
    }
    $sums | Set-Content -LiteralPath "$dist/HollowConnect-$Version-SHA256SUMS.txt" -Encoding ascii
} finally {
    $resolved=(Resolve-Path -LiteralPath $stage).Path
    if((Split-Path -Parent $resolved) -eq (Resolve-Path -LiteralPath $dist).Path -and (Split-Path -Leaf $resolved) -match '^\.candidate-[0-9a-f]{32}$'){
        Remove-Item -LiteralPath $resolved -Recurse
    }
}
