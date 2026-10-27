param([Parameter(Mandatory)][string]$Prefix,[Parameter(Mandatory)][string]$PayloadPath,
    [ValidateSet('host','guest','both')][string[]]$Cases=@('guest'),
    [string]$Scenes='Fungus1_35',[ValidateSet('host','guest')][string]$Summoner='guest',[switch]$Inspect,
    [ValidateSet('0','1','2')][string]$GuestHistory='0',
    [ValidateSet('none','native','f8')][string]$ExitMode='none')
$ErrorActionPreference='Stop'
$taskRoot=Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$taskKeys=@('HOLLOWCONNECT_DREAM_WARRIORS','HOLLOWCONNECT_DREAM_SCENES','HOLLOWCONNECT_DREAM_LANTERN_OWNER','HOLLOWCONNECT_DREAM_SUMMONER','HOLLOWCONNECT_DREAM_GUEST_HISTORY','HOLLOWCONNECT_NETWORK_FAULTS','HOLLOWCONNECT_WARRIOR_EXIT')
$taskOriginal=@{}; foreach($taskKey in $taskKeys){$taskOriginal[$taskKey]=[Environment]::GetEnvironmentVariable($taskKey)}
try {
    $env:HOLLOWCONNECT_DREAM_WARRIORS=$(if($Inspect){'inspect'}else{'all'})
    $env:HOLLOWCONNECT_DREAM_SCENES=$Scenes; $env:HOLLOWCONNECT_DREAM_SUMMONER=$Summoner; $env:HOLLOWCONNECT_NETWORK_FAULTS='0'
    $env:HOLLOWCONNECT_DREAM_GUEST_HISTORY=$GuestHistory
    if($ExitMode -eq 'f8' -and $Scenes -ne 'Cliffs_02'){throw 'The F8 cleanup probe is scoped to Gorb.'}
    $env:HOLLOWCONNECT_WARRIOR_EXIT=$(if($ExitMode -eq 'none'){''}else{$ExitMode})
    foreach($taskCase in $Cases){
        $env:HOLLOWCONNECT_DREAM_LANTERN_OWNER=$taskCase
        $taskRun=$Prefix+'-'+$taskCase; $taskPath=Join-Path $taskRoot ('.test-results/'+$taskRun)
        $taskOutcome='incomplete'; $taskError=''; $taskStrict=$false
        try {
            & (Join-Path $PSScriptRoot 'Run-Two.ps1') -RunName $taskRun -Mode progress -NoBuild -PayloadPath $PayloadPath
            $taskDeadline=[DateTime]::UtcNow.AddSeconds(300)
            while(!(Test-Path -LiteralPath (Join-Path $taskPath 'host.done')) -or !(Test-Path -LiteralPath (Join-Path $taskPath 'guest.done'))){
                if(@(Get-ChildItem -LiteralPath $taskPath -File -Filter '*.failed').Count){throw 'Dream warrior check failed.'}
                if([DateTime]::UtcNow -gt $taskDeadline){throw 'Dream warrior check timed out.'}
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
        [ordered]@{scenario=$taskCase;scenes=$Scenes;summoner=$Summoner;inspect=[bool]$Inspect;outcome=$taskOutcome;strictLogs=$taskStrict;error=$taskError;probeSha256=(Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'bin/Release/net472/HollowConnect.Smoke.dll')).Hash}|
            ForEach-Object { $_.guestHistory=$GuestHistory; $_.exitMode=$ExitMode; $_ } | ConvertTo-Json|Set-Content -LiteralPath (Join-Path $taskPath 'result.json') -Encoding UTF8
        Write-Output ($taskRun+': '+$taskOutcome+'; strict='+$taskStrict)
    }
} finally {foreach($taskKey in $taskKeys){[Environment]::SetEnvironmentVariable($taskKey,$taskOriginal[$taskKey])}}
