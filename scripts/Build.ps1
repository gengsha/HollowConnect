param([string]$GamePath)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    . (Join-Path $PSScriptRoot 'GamePath.ps1')
    $GamePath = Resolve-HollowKnightDirectory $GamePath
    $env:MSBuildEnableWorkloadResolver = 'false'
    $managed = Join-Path $GamePath 'hollow_knight_Data\Managed'
    if (!(Test-Path -LiteralPath (Join-Path $managed 'PlayMaker.dll'))) { throw 'Game Managed directory not found.' }
    $vendor = Join-Path $root 'vendor'
    $refs = Join-Path $vendor 'references'
    New-Item -ItemType Directory -Force -Path $refs | Out-Null
    Get-ChildItem -LiteralPath $managed -Filter '*.dll' | Copy-Item -Destination $refs -Force
    $apiZip = Join-Path $vendor 'moddingapi.v77.windows.zip'
    if (!(Test-Path -LiteralPath $apiZip)) {
        Invoke-WebRequest -Uri 'https://api.github.com/repos/hk-modding/api/releases/assets/325790225' -Headers @{Accept='application/octet-stream'} -UserAgent 'HollowConnect-build' -OutFile $apiZip
    }
    if ((Get-FileHash -LiteralPath $apiZip -Algorithm SHA256).Hash -ne 'bc9f0db3d0916b05cd5a2420bb602fb1b239ce3ff6c289fd84bffb682fb8f1d6') { throw 'Modding API checksum mismatch.' }
    $api = Join-Path $vendor 'ModdingAPI'
    Expand-Archive -LiteralPath $apiZip -DestinationPath $api -Force
    Get-ChildItem -LiteralPath $api -Filter '*.dll' | Copy-Item -Destination $refs -Force
    & dotnet build tests/HollowConnect.Tests.csproj -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    & (Join-Path $root 'tests\bin\Release\net472\HollowConnect.Tests.exe')
    if ($LASTEXITCODE -ne 0) { throw 'Regression tests failed.' }
    & (Join-Path $PSScriptRoot 'Package.ps1')
} finally { Pop-Location }
