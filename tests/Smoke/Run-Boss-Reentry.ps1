param([Parameter(Mandatory)][ValidatePattern('^[a-z0-9][a-z0-9-]{0,50}$')][string]$Prefix,
    [ValidateSet('host','guest','private','no-effects')][string[]]$Only=@('host','guest','private','no-effects'),
    [switch]$NoBuild)
$ErrorActionPreference='Stop'
$taskRoot=Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$taskOriginal=@{}
foreach($taskEnv in @(Get-ChildItem Env: | Where-Object Name -Like 'HOLLOWCONNECT_*')){$taskOriginal[$taskEnv.Name]=$taskEnv.Value}
try {
    if(!$NoBuild){& (Join-Path $taskRoot 'scripts/Build.ps1') -Target Smoke}
    foreach($taskCase in $Only){
        foreach($taskEnv in @(Get-ChildItem Env: | Where-Object Name -Like 'HOLLOWCONNECT_*')){Remove-Item -LiteralPath ('Env:'+$taskEnv.Name)}
        $env:HOLLOWCONNECT_MAWLEK_AUTHORITY=if($taskCase -in @('guest','no-effects')){'guest'}else{'host'}
        if($taskCase -eq 'private'){$env:HOLLOWCONNECT_REENTRY_PRIVATE='1'}
        if($taskCase -eq 'no-effects'){$env:HOLLOWCONNECT_REENTRY_NO_EFFECTS='1'}
        $taskRun=$Prefix+'-'+$taskCase
        $taskPath=Join-Path $taskRoot ('.test-results/'+$taskRun)
        & (Join-Path $PSScriptRoot 'Run-Two.ps1') -RunName $taskRun -Mode boss-reentry -NoBuild
        @{case=$taskCase;authority=$env:HOLLOWCONNECT_MAWLEK_AUTHORITY;private=($taskCase -eq 'private');withoutReplicaDeathEffects=($taskCase -eq 'no-effects')} |
            ConvertTo-Json | Set-Content -LiteralPath (Join-Path $taskPath 'case.json') -Encoding utf8
        (Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'bin/Release/net472/HollowConnect.Smoke.dll')).Hash |
            Set-Content -LiteralPath (Join-Path $taskPath 'probe-sha256.txt')
        try {
            $taskDeadline=[DateTime]::UtcNow.AddMinutes(4)
            while(!(Test-Path -LiteralPath (Join-Path $taskPath 'host.done')) -or !(Test-Path -LiteralPath (Join-Path $taskPath 'guest.done'))){
                if(@(Get-ChildItem -LiteralPath $taskPath -Filter '*.failed').Count){throw ('Native Boss reentry failed: '+$taskRun)}
                if([DateTime]::UtcNow -ge $taskDeadline){throw ('Native Boss reentry timed out: '+$taskRun)}
                Start-Sleep -Seconds 1
            }
            Start-Sleep -Seconds 2
            & (Join-Path $PSScriptRoot 'Assert-ProbeLogs.ps1') -RunPath $taskPath -RequireRuntimeFiles
            foreach($taskRole in 'host','guest'){
                if(!(Select-String -LiteralPath (Join-Path $taskPath ($taskRole+'.log')) -SimpleMatch 'FINAL PASS native boss reentry' -Quiet)){
                    throw ('Missing native reentry assertions: '+$taskRole)
                }
            }
            foreach($taskBinary in (Get-Content -LiteralPath (Join-Path $taskPath 'binaries.json') -Raw | ConvertFrom-Json)){
                if((Get-FileHash -LiteralPath (Join-Path $taskRoot ('src/HollowConnect/bin/Release/net472/'+$taskBinary.name))).Hash -ne $taskBinary.sha256){
                    throw ('Runtime production hash mismatch: '+$taskBinary.name)
                }
            }
            'PASS both endpoints, strict logs and production hashes' | Set-Content -LiteralPath (Join-Path $taskPath 'strict-pass.txt')
            Write-Output ('PASS '+$taskRun)
        } finally {
            foreach($taskRole in 'host','guest'){
                $taskProcess=Get-Process -Id ([int](Get-Content -LiteralPath (Join-Path $taskPath ($taskRole+'.pid')))) -ErrorAction SilentlyContinue
                $taskExe=[IO.Path]::GetFullPath((Join-Path $taskRoot ('.test-results/game-'+$taskRole+'/hollow_knight.exe')))
                if($taskProcess -and [string]::Equals($taskProcess.Path,$taskExe,[StringComparison]::OrdinalIgnoreCase)){
                    Stop-Process -Id $taskProcess.Id -Force;$taskProcess.WaitForExit()
                }
            }
        }
    }
} finally {
    foreach($taskEnv in @(Get-ChildItem Env: | Where-Object Name -Like 'HOLLOWCONNECT_*')){Remove-Item -LiteralPath ('Env:'+$taskEnv.Name)}
    foreach($taskKey in $taskOriginal.Keys){[Environment]::SetEnvironmentVariable($taskKey,$taskOriginal[$taskKey],'Process')}
}
