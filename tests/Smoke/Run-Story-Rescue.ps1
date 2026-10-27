param([Parameter(Mandatory)][string]$Prefix,
    [string[]]$Only=@('mother','knight','tyrant','kin','defender','ogres','moving','vanishing'),
    [ValidateSet('host','guest')][string[]]$Authority=@('host','guest'),[switch]$NoBuild,[switch]$ContinueOnFailure)
$ErrorActionPreference='Stop'
$taskRepo=Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$taskCases=@{mother='Crossroads_04';knight='Crossroads_10';tyrant='Dream_02_Mage_Lord';kin='Dream_03_Infected_Knight';defender='Dream_04_White_Defender';ogres='Fungus2_05';moving='moving';vanishing='vanishing';retry='retry';rejected='rejected';timeout='timeout';blocked='blocked';acid='acid';'revive-loss'='revive-loss'}
foreach($taskCase in $Only){if(!$taskCases.ContainsKey($taskCase)){throw ('Unknown story rescue case '+$taskCase)}}
$taskKeys=@('HOLLOWCONNECT_STORY_RESCUE_CASE','HOLLOWCONNECT_STORY_RESCUE_AUTHORITY','HOLLOWCONNECT_NETWORK_FAULTS')
$taskPrevious=@{};foreach($taskKey in $taskKeys){$taskPrevious[$taskKey]=[Environment]::GetEnvironmentVariable($taskKey)}
try {
    $taskFailures=@()
    if(!$NoBuild){& (Join-Path $taskRepo 'scripts/Build.ps1') -Target Smoke -NoRestore}
    foreach($taskCase in $Only){foreach($taskAuthority in $Authority){
      try {
        $env:HOLLOWCONNECT_STORY_RESCUE_CASE=$taskCases[$taskCase]
        $env:HOLLOWCONNECT_STORY_RESCUE_AUTHORITY=$taskAuthority
        $env:HOLLOWCONNECT_NETWORK_FAULTS='0'
        $taskRun=$Prefix+'-'+$taskCase+'-'+$taskAuthority
        $taskPath=Join-Path $taskRepo ('.test-results/'+$taskRun)
        & (Join-Path $PSScriptRoot 'Run-Two.ps1') -RunName $taskRun -Mode rescue -NoBuild
        @{case=$taskCases[$taskCase];authority=$taskAuthority;route='native walking after Down; native nail collision'} | ConvertTo-Json |
            Set-Content -LiteralPath (Join-Path $taskPath 'case.json')
        (Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'bin/Release/net472/HollowConnect.Smoke.dll')).Hash |
            Set-Content -LiteralPath (Join-Path $taskPath 'probe-sha256.txt')
        try {
            $taskDeadline=[DateTime]::UtcNow.AddSeconds(240)
            while(!(Test-Path -LiteralPath (Join-Path $taskPath 'host.done')) -or !(Test-Path -LiteralPath (Join-Path $taskPath 'guest.done'))){
                if(@(Get-ChildItem -LiteralPath $taskPath -Filter '*.failed').Count){throw ('Probe failed: '+$taskRun)}
                if([DateTime]::UtcNow -gt $taskDeadline){throw ('Probe timed out: '+$taskRun)}
                Start-Sleep -Seconds 1
            }
        }finally{
            foreach($taskRole in 'guest','host'){
                $taskPid=[int](Get-Content -LiteralPath (Join-Path $taskPath ($taskRole+'.pid')))
                $taskProcess=Get-Process -Id $taskPid -ErrorAction SilentlyContinue
                $taskExe=[IO.Path]::GetFullPath((Join-Path $taskRepo ('.test-results/game-'+$taskRole+'/hollow_knight.exe')))
                if($taskProcess -and [string]::Equals($taskProcess.Path,$taskExe,[StringComparison]::OrdinalIgnoreCase)){
                    Stop-Process -Id $taskPid -Force; $taskProcess.WaitForExit()
                }
            }
        }
        & (Join-Path $PSScriptRoot 'Assert-ProbeLogs.ps1') -RunPath $taskPath -RequireRuntimeFiles
        'PASS: both endpoints, actual walking rescue, strict runtime logs.' | Set-Content -LiteralPath (Join-Path $taskPath 'strict-pass.txt')
        Write-Output ('PASS '+$taskRun)
      }catch{
        $taskFailures+= $taskRun
        Write-Output ('FAILED '+$taskRun+': '+$_.Exception.Message)
        if(!$ContinueOnFailure){throw}
      }
    }}
    if($taskFailures.Count){throw ('Failed story rescue cases: '+($taskFailures -join ', '))}
}finally{foreach($taskKey in $taskKeys){[Environment]::SetEnvironmentVariable($taskKey,$taskPrevious[$taskKey])}}
