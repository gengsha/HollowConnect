param([switch]$Playtest)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$version = '0.1.13'
$env:MSBuildEnableWorkloadResolver = 'false'
$taskEvidencePath=Join-Path $root 'docs/evidence/runtime-room-0.1.13.json'
$taskEvidence=Get-Content -LiteralPath $taskEvidencePath -Raw | ConvertFrom-Json
$taskBlockers=Get-Content -LiteralPath (Join-Path $root 'docs/evidence/runtime-blockers-0.1.13.json') -Raw | ConvertFrom-Json
if($taskBlockers.version -ne $version){throw 'Wrong blocker ledger version.'}
$taskUnresolved=@($taskBlockers.findings | Where-Object {$_.resolved -ne $true})
if($taskUnresolved.Count -and !$Playtest){throw 'Unresolved runtime blocker; no release package created.'}
$taskAuthorizationPath=Join-Path $root 'docs/evidence/playtest-authorization-0.1.13.json'
if($Playtest){
    $taskAuthorization=Get-Content -LiteralPath $taskAuthorizationPath -Raw | ConvertFrom-Json
    if($taskAuthorization.version -ne $version -or $taskAuthorization.action -ne 'package-for-user-playtest' -or @($taskUnresolved | Where-Object {$_.id -notin $taskAuthorization.acceptedUnresolvedFindings}).Count){throw 'Playtest request does not cover current unresolved findings.'}
    foreach($taskRequired in 'HKMP.dll','HollowConnect.dll','HollowConnect.Core.dll'){
        $taskHash=(Get-FileHash -LiteralPath (Join-Path $root ('src/HollowConnect/bin/Release/net472/'+$taskRequired)) -Algorithm SHA256).Hash
        if(@($taskAuthorization.binaries | Where-Object {$_.name -eq $taskRequired -and $_.sha256 -eq $taskHash}).Count -ne 1){throw 'Playtest production DLL differs from the authorized tested build.'}
    }
}
if((!$Playtest -and $taskEvidence.releaseReady -ne $true) -or $taskEvidence.version -ne $version -or $taskEvidence.engineVersion -ne '3.0.0-hc10' -or @($taskEvidence.runs).Count -ne 12){throw 'Final runtime acceptance is incomplete or blocked; no release package created.'}
foreach($taskRun in $taskEvidence.runs){
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
& dotnet build (Join-Path $root 'src\HollowConnect.Installer\HollowConnect.Installer.csproj') -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw 'Installer build failed.' }
$taskPackageName="HollowConnect-$version"+$(if($Playtest){'-playtest'}else{''})
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
Copy-Item -LiteralPath (Join-Path $root 'docs') -Destination $package -Recurse -Force
$manifest = [ordered]@{
    version = $version
    releaseChannel = if($Playtest){'playtest'}else{'release'}
    releaseReady = $taskEvidence.releaseReady
    knownIssues = $taskUnresolved
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
Compress-Archive -Path (Join-Path $package '*') -DestinationPath (Join-Path $root ('dist\'+$taskPackageName+'-windows.zip')) -Force
$sources = 'src','tests','scripts','docs','README.md','LICENSE','HKMP-LICENSE','THIRD-PARTY.md','Directory.Build.props','.gitignore'
# Exclude build output and the user's game assemblies from the corresponding source archive.
Add-Type -AssemblyName System.IO.Compression.FileSystem
$sourceZip = Join-Path $root ('dist\'+$taskPackageName+'-source.zip')
if (Test-Path -LiteralPath $sourceZip) { Remove-Item -LiteralPath $sourceZip }
$archive = [IO.Compression.ZipFile]::Open($sourceZip, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($name in $sources) {
        $path = Join-Path $root $name
        $entries = if (Test-Path -LiteralPath $path -PathType Container) { Get-ChildItem -LiteralPath $path -File -Recurse | Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } } else { Get-Item -LiteralPath $path }
        foreach ($file in $entries) {
            $relative = $file.FullName.Substring($root.Length + 1).Replace('\','/')
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $relative, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    }
} finally { $archive.Dispose() }
Get-ChildItem -LiteralPath (Join-Path $root 'dist') -Filter 'HollowConnect-*.zip' -File |
    Sort-Object Name | ForEach-Object { ((Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName).Hash.ToLowerInvariant()) + '  ' + $_.Name } |
    Set-Content -LiteralPath (Join-Path $root 'dist\SHA256SUMS.txt') -Encoding ASCII
Write-Output "Packaged $package"
