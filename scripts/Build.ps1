param([string]$GamePath,
    [ValidateSet('Mod','Core','Smoke','Installer')][string]$Target='Mod',
    [switch]$RefreshReferences, [switch]$RunTests, [string]$TestSelection,
    [switch]$NoRestore)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$taskSelectionPath = if ($TestSelection) {
    if (!(Test-Path -LiteralPath $TestSelection -PathType Leaf)) { throw 'Test selection file not found.' }
    (Resolve-Path -LiteralPath $TestSelection).Path
} else { $null }
Push-Location $root
try {
    $env:MSBuildEnableWorkloadResolver = 'false'
    $taskReferences = Join-Path $root 'vendor/references'
    $taskReferenceNames = 'Assembly-CSharp.dll','Assembly-CSharp-firstpass.dll','PlayMaker.dll',
        'MMHOOK_Assembly-CSharp.dll','MMHOOK_PlayMaker.dll','MonoMod.RuntimeDetour.dll',
        'UnityEngine.CoreModule.dll','UnityEngine.IMGUIModule.dll'
    $taskMissing = @($taskReferenceNames | Where-Object { !(Test-Path -LiteralPath (Join-Path $taskReferences $_)) })
    # Setup is only needed on the first build or an explicit reference refresh.
    if ($RefreshReferences -or ($Target -ne 'Installer' -and $taskMissing.Count)) {
        . (Join-Path $PSScriptRoot 'GamePath.ps1')
        $GamePath = Resolve-HollowKnightDirectory $GamePath
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
    }
    $taskProjects = @{
        Mod='src/HollowConnect/HollowConnect.csproj'
        Core='tests/HollowConnect.Tests.csproj'
        Smoke='tests/Smoke/HollowConnect.Smoke.csproj'
        Installer='src/HollowConnect.Installer/HollowConnect.Installer.csproj'
    }
    $taskExecuteTests = $RunTests -or $TestSelection
    $taskBuildTargets = @($Target)
    if ($taskExecuteTests) {
        if ($Target -eq 'Mod') { $taskBuildTargets = @('Core') }
        elseif ($Target -ne 'Core') { $taskBuildTargets += 'Core' }
    }
    foreach ($taskBuildTarget in $taskBuildTargets) {
        $taskArguments = @('build', $taskProjects[$taskBuildTarget], '-c', 'Release', '--nologo')
        if ($NoRestore) { $taskArguments += '--no-restore' }
        & dotnet @taskArguments
        if ($LASTEXITCODE -ne 0) { throw ('Build failed: '+$taskBuildTarget) }
    }
    if ($taskExecuteTests) {
        $taskOldSelection = $env:HOLLOWCONNECT_TEST_SELECTION
        try {
            $env:HOLLOWCONNECT_TEST_SELECTION = $taskSelectionPath
            & (Join-Path $root 'tests/bin/Release/net472/HollowConnect.Tests.exe')
            if ($LASTEXITCODE -ne 0) { throw 'Regression tests failed.' }
        } finally { $env:HOLLOWCONNECT_TEST_SELECTION = $taskOldSelection }
    }
} finally { Pop-Location }
