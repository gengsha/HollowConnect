param([Parameter(Mandatory=$true)][string]$RunName, [ValidateSet('host','guest')][string]$First='host',
    [ValidateSet('Bronze','Silver','Gold')][string]$Trial='Bronze', [bool]$Share=$true,
    [ValidateSet('inspect','entry','return','late','bench','native','bounds','bounds-baseline','lifecycle')][string]$Case='inspect', [switch]$Wait,
    [string]$EnginePath='')
$ErrorActionPreference='Stop'
$taskRoot=Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if ($RunName -notmatch '^[a-z0-9][a-z0-9-]{0,95}$') { throw 'Invalid run name' }
$runRoot=Join-Path $taskRoot ".test-results\$RunName"
if(Test-Path -LiteralPath $runRoot){throw 'Preserve existing evidence; choose a new run name'}
foreach($testRole in 'host','guest') {
    $gameExe=Join-Path $taskRoot ".test-results\game-$testRole\hollow_knight.exe"
    foreach($gameProcess in Get-Process -Name hollow_knight -ErrorAction SilentlyContinue) {
        if($gameProcess.Path -eq $gameExe){throw "Isolated process still running: $($gameProcess.Id)"}
    }
}
New-Item -ItemType Directory -Path $runRoot | Out-Null
$dllRoot=Join-Path $PSScriptRoot 'bin\Release\net472'
$binaries=foreach($dllName in 'HKMP.dll','HollowConnect.dll','HollowConnect.Core.dll','HollowConnect.Smoke.dll') {
    $dllPath=if($dllName -eq 'HKMP.dll' -and $EnginePath){$EnginePath}else{Join-Path $dllRoot $dllName}
    [pscustomobject]@{name=$dllName;sha256=(Get-FileHash -LiteralPath $dllPath).Hash}
}
$binaries | ConvertTo-Json | Set-Content -LiteralPath "$runRoot\binaries.json"
[pscustomobject]@{first=$First;trial=$Trial;share=$Share;case=$Case} | ConvertTo-Json | Set-Content -LiteralPath "$runRoot\parameters.json"
foreach($testRole in 'host','guest') {
    $gameDir=Join-Path $taskRoot ".test-results\game-$testRole"
    foreach($dllName in 'HKMP.dll','HollowConnect.dll','HollowConnect.Core.dll','HollowConnect.Smoke.dll') {
        $dllPath=if($dllName -eq 'HKMP.dll' -and $EnginePath){$EnginePath}else{Join-Path $dllRoot $dllName}
        Copy-Item -LiteralPath $dllPath -Destination "$gameDir\hollow_knight_Data\Managed\Mods\HKMP\$dllName" -Force
    }
    $startInfo=[Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName=Join-Path $gameDir 'hollow_knight.exe'; $startInfo.WorkingDirectory=$gameDir
    $startInfo.UseShellExecute=$false; $startInfo.CreateNoWindow=$true; $startInfo.WindowStyle='Hidden'
    $startInfo.Arguments="-batchmode -screen-fullscreen 0 -screen-width 800 -screen-height 600 -logFile `"$runRoot\$testRole-unity.log`""
    $startInfo.EnvironmentVariables['SteamAppId']='367520';$startInfo.EnvironmentVariables['SteamGameId']='367520'
    $startInfo.EnvironmentVariables['HOLLOWCONNECT_SMOKE_ROOT']=$runRoot
    $startInfo.EnvironmentVariables['HOLLOWCONNECT_SMOKE_ROLE']=$testRole
    $startInfo.EnvironmentVariables['HOLLOWCONNECT_SMOKE_MODE']='colosseum'
    $startInfo.EnvironmentVariables['HOLLOWCONNECT_COLOSSEUM_SHARE']=if($Share){'1'}else{'0'}
    $startInfo.EnvironmentVariables['HOLLOWCONNECT_COLOSSEUM_FIRST']=$First
    $startInfo.EnvironmentVariables['HOLLOWCONNECT_COLOSSEUM_TRIAL']=$Trial
    $startInfo.EnvironmentVariables['HOLLOWCONNECT_COLOSSEUM_CASE']=$Case
    $testProcess=[Diagnostics.Process]::Start($startInfo)
    $testProcess.Id | Set-Content -LiteralPath "$runRoot\$testRole.pid"
    Write-Output "$testRole started: $($testProcess.Id)"
}
if($Wait){& (Join-Path $PSScriptRoot 'Complete-Colosseum.ps1') -RunName $RunName -Native:($Case -eq 'native') -EnginePath $EnginePath}
