param([Parameter(Mandatory)][string]$RunName,
    [ValidateSet('world','different-host','different-guest')][string]$Case='world',
    [ValidateSet('host','guest')][string]$First='host',[switch]$NoBuild)
$ErrorActionPreference='Stop'
$taskRoot=Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$taskPath=Join-Path $taskRoot ('.test-results/'+$RunName)
$taskOriginal=@{};foreach($taskEnv in @(Get-ChildItem Env: | Where-Object {$_.Name -like 'HOLLOWCONNECT_*'})){
    $taskOriginal[$taskEnv.Name]=$taskEnv.Value;[Environment]::SetEnvironmentVariable($taskEnv.Name,$null,'Process')
}
try {
    if(!$NoBuild){& (Join-Path $taskRoot 'scripts/Build.ps1') -Target Smoke -NoRestore}
    $env:HOLLOWCONNECT_LOCAL_WORLD=$Case;$env:HOLLOWCONNECT_LOCAL_FIRST=$First;$env:HOLLOWCONNECT_NETWORK_FAULTS='0'
    & (Join-Path $PSScriptRoot 'Run-Two.ps1') -RunName $RunName -Mode progress -NoBuild
    @{case=$Case;first=$First}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $taskPath 'case.json') -Encoding UTF8
    (Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'bin/Release/net472/HollowConnect.Smoke.dll')).Hash|
        Set-Content -LiteralPath (Join-Path $taskPath 'smoke.sha256')
    try {
        $taskDeadline=[DateTime]::UtcNow.AddSeconds(300)
        while(!(Test-Path -LiteralPath (Join-Path $taskPath 'host.done')) -or !(Test-Path -LiteralPath (Join-Path $taskPath 'guest.done'))){
            if(@(Get-ChildItem -LiteralPath $taskPath -Filter '*.failed').Count){throw ('Local world failed '+$RunName)}
            if([DateTime]::UtcNow -gt $taskDeadline){throw ('Local world timed out '+$RunName)}
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
    foreach($taskBinary in (Get-Content -LiteralPath (Join-Path $taskPath 'binaries.json') -Raw | ConvertFrom-Json)){
        $taskProduction=Join-Path $taskRoot ('src/HollowConnect/bin/Release/net472/'+$taskBinary.name)
        if((Get-FileHash -LiteralPath $taskProduction).Hash -ne $taskBinary.sha256){throw 'Runtime DLL differs from production'}
    }
    'PASS both endpoints, strict logs and production hashes'|Set-Content -LiteralPath (Join-Path $taskPath 'strict-pass.txt')
    Write-Output ('PASS '+$RunName)
} finally {
    foreach($taskEnv in @(Get-ChildItem Env: | Where-Object {$_.Name -like 'HOLLOWCONNECT_*'})){[Environment]::SetEnvironmentVariable($taskEnv.Name,$null,'Process')}
    foreach($taskKey in $taskOriginal.Keys){[Environment]::SetEnvironmentVariable($taskKey,$taskOriginal[$taskKey],'Process')}
}
