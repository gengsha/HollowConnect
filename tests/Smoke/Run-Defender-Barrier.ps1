param([Parameter(Mandatory)][string]$RunName,
    [ValidateSet('inspect','outside','late')][string]$Case='outside',
    [ValidateSet('host','guest')][string]$First='host',[switch]$NoBuild)
$ErrorActionPreference='Stop'
$taskRoot=Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$taskPath=Join-Path $taskRoot ('.test-results/'+$RunName)
$taskKeys=@('HOLLOWCONNECT_DEFENDER_CHECK','HOLLOWCONNECT_DEFENDER_BARRIER_CHECK','HOLLOWCONNECT_DEFENDER_FIRST','HOLLOWCONNECT_NETWORK_FAULTS')
$taskOriginal=@{};foreach($taskKey in $taskKeys){$taskOriginal[$taskKey]=[Environment]::GetEnvironmentVariable($taskKey)}
try {
    if(!$NoBuild){& (Join-Path $taskRoot 'scripts/Build.ps1') -Target Smoke -NoRestore}
    $env:HOLLOWCONNECT_DEFENDER_CHECK='1';$env:HOLLOWCONNECT_DEFENDER_BARRIER_CHECK=$Case
    $env:HOLLOWCONNECT_DEFENDER_FIRST=$First;$env:HOLLOWCONNECT_NETWORK_FAULTS='0'
    & (Join-Path $PSScriptRoot 'Run-Two.ps1') -RunName $RunName -Mode progress -NoBuild
    @{case=$Case;first=$First}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $taskPath 'case.json') -Encoding UTF8
    (Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'bin/Release/net472/HollowConnect.Smoke.dll')).Hash|
        Set-Content -LiteralPath (Join-Path $taskPath 'smoke.sha256')
    try {
        $taskDeadline=[DateTime]::UtcNow.AddSeconds(180)
        while(!(Test-Path -LiteralPath (Join-Path $taskPath 'host.done')) -or !(Test-Path -LiteralPath (Join-Path $taskPath 'guest.done'))){
            if(@(Get-ChildItem -LiteralPath $taskPath -Filter '*.failed').Count){throw ('Defender barrier failed '+$RunName)}
            if([DateTime]::UtcNow -gt $taskDeadline){throw ('Defender barrier timed out '+$RunName)}
            Start-Sleep -Seconds 1
        }
    } finally {
        foreach($taskRole in 'host','guest'){
            $taskPid=[int](Get-Content -LiteralPath (Join-Path $taskPath ($taskRole+'.pid')))
            $taskProcess=Get-Process -Id $taskPid -ErrorAction SilentlyContinue
            $taskExe=Join-Path $taskRoot ('.test-results/game-'+$taskRole+'/hollow_knight.exe')
            if($taskProcess -and [string]::Equals($taskProcess.Path,$taskExe,[StringComparison]::OrdinalIgnoreCase)){
                Stop-Process -Id $taskPid -Force;$taskProcess.WaitForExit()
            }
        }
    }
    & (Join-Path $PSScriptRoot 'Assert-ProbeLogs.ps1') -RunPath $taskPath -RequireRuntimeFiles
    ('PASS: '+$Case+'; both endpoints and strict runtime logs.')|Set-Content -LiteralPath (Join-Path $taskPath 'strict-pass.txt')
    Write-Output ('PASS '+$RunName)
} finally {foreach($taskKey in $taskKeys){[Environment]::SetEnvironmentVariable($taskKey,$taskOriginal[$taskKey])}}
