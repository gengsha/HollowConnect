param([Parameter(Mandatory=$true)][string]$RunName, [switch]$Native, [string]$EnginePath='')
$ErrorActionPreference='Stop'
$taskRoot=Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if($RunName -notmatch '^[a-z0-9][a-z0-9-]{0,95}$'){throw 'Invalid run name'}
$runRoot=Join-Path $taskRoot ".test-results\$RunName"
try {
    $deadline=[DateTime]::UtcNow.AddSeconds(220)
    while(!(Test-Path -LiteralPath "$runRoot\host.done") -or !(Test-Path -LiteralPath "$runRoot\guest.done")) {
        if(@(Get-ChildItem -LiteralPath $runRoot -Filter '*.failed').Count){throw 'Probe rejected; preserve failure evidence'}
        if([DateTime]::UtcNow -gt $deadline){throw 'Probe timed out; preserve incomplete evidence'}
        Start-Sleep -Milliseconds 500
    }
    if(!$Native) {
        foreach($marker in 'host.shutdown-offline','guest.shutdown-offline','shutdown-server-stopped') {
            if(!(Test-Path -LiteralPath (Join-Path $runRoot $marker))){throw "Missing shutdown marker $marker"}
        }
    }
} finally {
    foreach($testRole in 'host','guest') {
        $gameExe=Join-Path $taskRoot ".test-results\game-$testRole\hollow_knight.exe"
        foreach($gameProcess in Get-Process -Name hollow_knight -ErrorAction SilentlyContinue) {
            if($gameProcess.Path -eq $gameExe) {
                Stop-Process -Id $gameProcess.Id -Force
                if(!$gameProcess.WaitForExit(10000)){throw 'Isolated process did not exit'}
            }
        }
    }
}
& (Join-Path $PSScriptRoot 'Assert-ProbeLogs.ps1') -RunPath $runRoot -RequireRuntimeFiles
foreach($binary in (Get-Content -LiteralPath "$runRoot\binaries.json" -Raw | ConvertFrom-Json)) {
    if($binary.name -eq 'HollowConnect.Smoke.dll'){continue}
    foreach($dllRoot in @('tests\Smoke\bin\Release\net472','src\HollowConnect\bin\Release\net472',
        '.test-results\game-host\hollow_knight_Data\Managed\Mods\HKMP','.test-results\game-guest\hollow_knight_Data\Managed\Mods\HKMP')) {
        $dllPath=if($binary.name -eq 'HKMP.dll' -and $EnginePath -and $dllRoot -notlike '.test-results*'){$EnginePath}else{Join-Path $taskRoot "$dllRoot\$($binary.name)"}
        if((Get-FileHash -LiteralPath $dllPath).Hash -ne $binary.sha256){throw "Production DLL mismatch $dllRoot $($binary.name)"}
    }
}
foreach($testRole in 'host','guest') {
    Select-String -LiteralPath "$runRoot\$testRole.log" -Pattern 'CHECKPOINT BEFORE DEATH|OBSERVED PERSONAL RETURN|FINAL PASS' | ForEach-Object {$_.Line}
}
Write-Output "OBSERVATION COMPLETE; stopped and strict logs/hashes verified: $RunName"
