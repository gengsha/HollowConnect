param([Parameter(Mandatory)][string]$Prefix,
    [ValidateSet('traveller','quota','off-spectate','spectator-wipe','no-spectate','native-control','voluntary','visibility-only')][string[]]$Only=@('traveller','quota','off-spectate','spectator-wipe','no-spectate','voluntary'),
    [switch]$NoBuild)
$ErrorActionPreference='Stop'
$taskRepo=Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$taskOriginal=$env:HOLLOWCONNECT_RESCUE_RULE_CHECK
try {
    if (!$NoBuild) { & (Join-Path $taskRepo 'scripts/Build.ps1') -Target Smoke -NoRestore }
    foreach ($taskCase in $Only) {
        $env:HOLLOWCONNECT_RESCUE_RULE_CHECK=$taskCase
        $taskRun=$Prefix+'-'+$taskCase
        $taskPath=Join-Path $taskRepo ('.test-results/'+$taskRun)
        & (Join-Path $PSScriptRoot 'Run-Two.ps1') -RunName $taskRun -Mode rescue -NoBuild
        $taskCase | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $taskPath 'case.json')
        (Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'bin/Release/net472/HollowConnect.Smoke.dll')).Hash |
            Set-Content -LiteralPath (Join-Path $taskPath 'probe-sha256.txt')
        try {
            $taskDeadline=[DateTime]::UtcNow.AddSeconds(240)
            while (!(Test-Path -LiteralPath (Join-Path $taskPath 'host.done')) -or !(Test-Path -LiteralPath (Join-Path $taskPath 'guest.done'))) {
                if (@(Get-ChildItem -LiteralPath $taskPath -Filter '*.failed').Count) { throw ('Probe failed: '+$taskRun) }
                if ([DateTime]::UtcNow -gt $taskDeadline) { throw ('Probe timed out: '+$taskRun) }
                Start-Sleep -Seconds 1
            }
        } finally {
            foreach ($taskRole in 'guest','host') {
                $taskPid=[int](Get-Content -LiteralPath (Join-Path $taskPath ($taskRole+'.pid')))
                $taskProcess=Get-Process -Id $taskPid -ErrorAction SilentlyContinue
                $taskExe=[IO.Path]::GetFullPath((Join-Path $taskRepo ('.test-results/game-'+$taskRole+'/hollow_knight.exe')))
                if ($taskProcess -and [string]::Equals($taskProcess.Path,$taskExe,[StringComparison]::OrdinalIgnoreCase)) {
                    Stop-Process -Id $taskPid -Force; $taskProcess.WaitForExit()
                }
            }
        }
        & (Join-Path $PSScriptRoot 'Assert-ProbeLogs.ps1') -RunPath $taskPath -RequireRuntimeFiles
        'PASS: both endpoints and strict runtime logs.' | Set-Content -LiteralPath (Join-Path $taskPath 'strict-pass.txt')
        Write-Output ('PASS '+$taskRun)
    }
} finally { $env:HOLLOWCONNECT_RESCUE_RULE_CHECK=$taskOriginal }
