param([Parameter(Mandatory)][string]$Prefix,[ValidateSet('host','guest')][string[]]$Authority=@('host','guest'),[switch]$NoBuild)
$ErrorActionPreference='Stop'
$taskRoot=Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$taskKey='HOLLOWCONNECT_BOSS_TARGET_AUTHORITY'
$taskPrevious=[Environment]::GetEnvironmentVariable($taskKey)
try {
    if(!$NoBuild){& (Join-Path $taskRoot 'scripts/Build.ps1') -Target Smoke -NoRestore}
    foreach($taskAuthority in $Authority){
        $taskRun=$Prefix+'-'+$taskAuthority
        $taskPath=Join-Path $taskRoot ('.test-results/'+$taskRun)
        [Environment]::SetEnvironmentVariable($taskKey,$taskAuthority)
        & (Join-Path $PSScriptRoot 'Run-Two.ps1') -RunName $taskRun -Mode rescue -NoBuild
        (Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'bin/Release/net472/HollowConnect.Smoke.dll')).Hash |
            Set-Content -LiteralPath (Join-Path $taskPath 'probe-sha256.txt')
        try {
            $taskDeadline=[DateTime]::UtcNow.AddSeconds(360)
            while(!(Test-Path -LiteralPath (Join-Path $taskPath 'host.done')) -or !(Test-Path -LiteralPath (Join-Path $taskPath 'guest.done'))){
                if(@(Get-ChildItem -LiteralPath $taskPath -Filter '*.failed').Count){throw ('Probe failed '+$taskRun)}
                if([DateTime]::UtcNow -gt $taskDeadline){throw ('Probe timed out '+$taskRun)}
                Start-Sleep -Seconds 1
            }
        } finally {
            foreach($taskRole in 'guest','host'){
                $taskPid=[int](Get-Content -LiteralPath (Join-Path $taskPath ($taskRole+'.pid')))
                $taskProcess=Get-Process -Id $taskPid -ErrorAction SilentlyContinue
                $taskExe=[IO.Path]::GetFullPath((Join-Path $taskRoot ('.test-results/game-'+$taskRole+'/hollow_knight.exe')))
                if($taskProcess -and [string]::Equals($taskProcess.Path,$taskExe,[StringComparison]::OrdinalIgnoreCase)){
                    Stop-Process -Id $taskPid -Force; $taskProcess.WaitForExit()
                }
            }
        }
        & (Join-Path $PSScriptRoot 'Assert-ProbeLogs.ps1') -RunPath $taskPath -RequireRuntimeFiles
        'PASS: both endpoints and strict runtime logs.' | Set-Content -LiteralPath (Join-Path $taskPath 'strict-pass.txt')
        Write-Output ('PASS '+$taskRun)
    }
} finally {
    [Environment]::SetEnvironmentVariable($taskKey,$taskPrevious)
    foreach($taskRole in 'host','guest'){
        $taskPlugin=Join-Path $taskRoot ('.test-results/game-'+$taskRole+'/hollow_knight_Data/Managed/Mods/HKMP/HollowConnect.Smoke.dll')
        if(Test-Path -LiteralPath $taskPlugin){Move-Item -LiteralPath $taskPlugin -Destination ($taskPlugin+'.disabled') -Force}
    }
}
