param([Parameter(Mandatory)][string]$Prefix,[Parameter(Mandatory)][string]$PayloadPath,
    [ValidateSet('new','stale')][string[]]$Cases=@('new','stale'))
$ErrorActionPreference='Stop'
$taskRoot=Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$taskKeys=@('HOLLOWCONNECT_PROGRESS_AUDIT','HOLLOWCONNECT_RESOURCE_MENU','HOLLOWCONNECT_NETWORK_FAULTS','HOLLOWCONNECT_RESOURCE_CHECK')
$taskOriginal=@{}; foreach($taskKey in $taskKeys){$taskOriginal[$taskKey]=[Environment]::GetEnvironmentVariable($taskKey)}
try {
    $env:HOLLOWCONNECT_PROGRESS_AUDIT='legacy-full'; $env:HOLLOWCONNECT_NETWORK_FAULTS='0'; $env:HOLLOWCONNECT_RESOURCE_CHECK='0'
    foreach($taskCase in $Cases){
        $env:HOLLOWCONNECT_RESOURCE_MENU=$taskCase
        $taskRun=$Prefix+'-'+$taskCase; $taskPath=Join-Path $taskRoot ('.test-results/'+$taskRun)
        $taskOutcome='incomplete'; $taskError=''; $taskStrict=$false
        try {
            & (Join-Path $PSScriptRoot 'Run-Two.ps1') -RunName $taskRun -Mode progress -NoBuild -PayloadPath $PayloadPath
            $taskDeadline=[DateTime]::UtcNow.AddSeconds(240)
            while(!(Test-Path -LiteralPath (Join-Path $taskPath 'host.done')) -or !(Test-Path -LiteralPath (Join-Path $taskPath 'guest.done'))){
                if(@(Get-ChildItem -LiteralPath $taskPath -File -Filter '*.failed').Count){throw 'Menu resource probe failed.'}
                if([DateTime]::UtcNow -gt $taskDeadline){throw 'Menu resource probe timed out.'}
                Start-Sleep -Seconds 1
            }
            $taskOutcome='passed'
        } catch {$taskError=$_.Exception.Message; $taskOutcome='needs-inspection'}
        finally {
            foreach($taskRole in 'host','guest'){
                $taskPidPath=Join-Path $taskPath ($taskRole+'.pid')
                if(!(Test-Path -LiteralPath $taskPidPath)){continue}
                $taskProcessId=[int](Get-Content -LiteralPath $taskPidPath)
                $taskProcess=Get-Process -Id $taskProcessId -ErrorAction SilentlyContinue
                $taskExe=[IO.Path]::GetFullPath((Join-Path $taskRoot ('.test-results/game-'+$taskRole+'/hollow_knight.exe')))
                if($taskProcess -and [string]::Equals($taskProcess.Path,$taskExe,[StringComparison]::OrdinalIgnoreCase)){
                    Stop-Process -Id $taskProcessId -Force; $taskProcess.WaitForExit()
                }
            }
        }
        try {& (Join-Path $PSScriptRoot 'Assert-ProbeLogs.ps1') -RunPath $taskPath -RequireRuntimeFiles; $taskStrict=$true}
        catch {$taskError+='; '+$_.Exception.Message}
        [ordered]@{scenario=$taskCase;outcome=$taskOutcome;strictLogs=$taskStrict;error=$taskError;probeSha256=(Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'bin/Release/net472/HollowConnect.Smoke.dll')).Hash}|
            ConvertTo-Json|Set-Content -LiteralPath (Join-Path $taskPath 'result.json') -Encoding UTF8
        Write-Output ($taskRun+': '+$taskOutcome+'; strict='+$taskStrict)
    }
} finally {foreach($taskKey in $taskKeys){[Environment]::SetEnvironmentVariable($taskKey,$taskOriginal[$taskKey])}}
