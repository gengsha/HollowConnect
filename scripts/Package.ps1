$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$version = '0.1.13'
$env:MSBuildEnableWorkloadResolver = 'false'
& dotnet build (Join-Path $root 'src\HollowConnect.Installer\HollowConnect.Installer.csproj') -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw 'Installer build failed.' }
$package = Join-Path $root "dist\HollowConnect-$version"
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
    installerRevision = 3
    gameVersion = '1.5.78.11833 Windows / Unity 2020.2.2f1'
    vanillaAssemblySha256 = 'FCC01E0DF1B841A8FAF6B5E39F27030F63204AD02F430B3DEFA262134AE4E8A0'
    apiAssemblySha256 = '5944411BD93830369390A4B51766EE68C4AB26195B299E25A07B5E7D0E00086D'
    apiZipSha256 = 'bc9f0db3d0916b05cd5a2420bb602fb1b239ce3ff6c289fd84bffb682fb8f1d6'
    files = @($files | ForEach-Object { @{name=$_; sha256=(Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $payload $_)).Hash} })
}
$manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $package 'manifest.json') -Encoding UTF8
Compress-Archive -Path (Join-Path $package '*') -DestinationPath (Join-Path $root "dist\HollowConnect-$version-windows.zip") -Force
$sources = 'src','tests','scripts','docs','README.md','LICENSE','HKMP-LICENSE','THIRD-PARTY.md','Directory.Build.props','.gitignore'
# Exclude build output and the user's game assemblies from the corresponding source archive.
Add-Type -AssemblyName System.IO.Compression.FileSystem
$sourceZip = Join-Path $root "dist\HollowConnect-$version-source.zip"
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
