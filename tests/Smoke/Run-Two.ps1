param([string]$RunName, [string]$Mode='full', [string]$EnginePath='')
$ErrorActionPreference='Stop'
$root=Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if ($RunName -notmatch '^[a-z0-9][a-z0-9-]{0,95}$') { throw 'Invalid isolated run directory name.' }
foreach ($p in Get-Process -Name hollow_knight -ErrorAction SilentlyContinue) {
    if ($p.Path -and $p.Path.StartsWith($root+'\.test-results\game-')) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue; $p.WaitForExit() }
}
# Language initialization writes Config.ini. Test copies must not share its
# hardlink, or two clients can lock the same file during simultaneous startup.
foreach ($testRole in 'host','guest') {
    $config=Join-Path $root ".test-results\game-$testRole\hollow_knight_Data\Config.ini"
    Copy-Item -LiteralPath $config -Destination ($config+'.isolated') -Force
    Remove-Item -LiteralPath $config
    Move-Item -LiteralPath ($config+'.isolated') -Destination $config
}
$env:MSBuildEnableWorkloadResolver='false'
dotnet build "$root\tests\Smoke\HollowConnect.Smoke.csproj" -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
$testRoot=Join-Path "$root\.test-results" $RunName
if ([IO.Path]::GetFullPath($testRoot) -notlike ($root+'\.test-results\*')) { throw 'Run directory escapes the isolated workspace.' }
if (Test-Path -LiteralPath $testRoot) { Remove-Item -LiteralPath $testRoot -Recurse -Force }
New-Item -ItemType Directory -Path $testRoot | Out-Null
$taskBinaries=@('HKMP.dll','HollowConnect.dll','HollowConnect.Core.dll') | ForEach-Object {
    $taskDllPath=if($_ -eq 'HKMP.dll' -and $EnginePath){$EnginePath}else{Join-Path $root ('tests/Smoke/bin/Release/net472/'+$_)}
    [pscustomobject]@{name=$_;sha256=(Get-FileHash -LiteralPath $taskDllPath -Algorithm SHA256).Hash}
}
$taskBinaries | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $testRoot 'binaries.json') -Encoding UTF8
$env:SteamAppId='367520'
$env:SteamGameId='367520'
$env:HOLLOWCONNECT_SMOKE_ROOT=$testRoot
$env:HOLLOWCONNECT_SMOKE_MODE=$Mode
foreach ($testRole in 'host','guest') {
    $gameDir=Join-Path "$root\.test-results" "game-$testRole"
    foreach ($dll in 'HKMP.dll','HollowConnect.dll','HollowConnect.Core.dll','BouncyCastle.Cryptography.dll','HollowConnect.Smoke.dll') {
        $taskDllPath=if($dll -eq 'HKMP.dll' -and $EnginePath){$EnginePath}else{"$root\tests\Smoke\bin\Release\net472\$dll"}
        Copy-Item -LiteralPath $taskDllPath -Destination "$gameDir\hollow_knight_Data\Managed\Mods\HKMP\$dll" -Force
    }
    $env:HOLLOWCONNECT_SMOKE_ROLE=$testRole
    $taskArguments=@('-screen-fullscreen','0','-screen-width','800','-screen-height','600','-logFile',"$testRoot\$testRole-unity.log")
    if($env:HOLLOWCONNECT_RENDERED_TEST -ne '1'){$taskArguments=@('-batchmode')+$taskArguments}
    $testProcess=Start-Process -FilePath "$gameDir\hollow_knight.exe" -ArgumentList $taskArguments -WorkingDirectory $gameDir -WindowStyle Hidden -PassThru
    Set-Content -LiteralPath "$testRoot\$testRole.pid" -Value $testProcess.Id
    Write-Output "$testRole started: $($testProcess.Id)"
}

