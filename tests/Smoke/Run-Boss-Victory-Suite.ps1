param([Parameter(Mandatory)][string]$Prefix,[string[]]$Only=@(),[switch]$NoBuild)
$ErrorActionPreference='Stop'
$taskRoot=Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$taskCases=@(
    @{Name='health-host';Case='health';Authority='host';Disabled='0'},
    @{Name='health-guest';Case='health';Authority='guest';Disabled='0'},
    @{Name='armour-host';Case='armour';Authority='host';Disabled='0'},
    @{Name='armour-guest';Case='armour';Authority='guest';Disabled='0'},
    @{Name='disabled-host';Case='health';Authority='host';Disabled='1'},
    @{Name='godhome-host';Case='godhome';Authority='host';Disabled='0'},
    @{Name='godhome-guest';Case='godhome';Authority='guest';Disabled='0'},
    @{Name='dream-champion';Case='dream-champion';Authority='host';Disabled='0'},
    @{Name='p5-failed-host';Case='p5-failed';Authority='host';Disabled='0'},
    @{Name='p5-failed-guest';Case='p5-failed';Authority='guest';Disabled='0'},
    @{Name='p5-pure-host';Case='p5-pure';Authority='host';Disabled='0';Spectator='1'},
    @{Name='p5-pure-guest';Case='p5-pure';Authority='guest';Disabled='0';Spectator='1'},
    @{Name='final-continuation';Case='final';Authority='host';Disabled='0'},
    @{Name='final-native';Case='final';Authority='host';Disabled='0';Native='1'},
    @{Name='final-spectator';Case='final';Authority='host';Disabled='0';Native='1';Spectator='1'}
)
foreach($taskName in $Only){if($taskName -notin $taskCases.Name){throw ('Unknown case '+$taskName)}}
if($Only.Count){$taskCases=@($taskCases | Where-Object {$_.Name -in $Only})}
$taskKeys=@('HOLLOWCONNECT_BOSS_VICTORY_CHECK','HOLLOWCONNECT_BOSS_VICTORY_CASE','HOLLOWCONNECT_BOSS_VICTORY_AUTHORITY','HOLLOWCONNECT_BOSS_VICTORY_DISABLED','HOLLOWCONNECT_FINAL_CASE','HOLLOWCONNECT_FINAL_BOSS_RESCUE','HOLLOWCONNECT_NETWORK_FAULTS','HOLLOWCONNECT_SETTLEMENT_SPECTATOR','HOLLOWCONNECT_FINAL_SPECTATOR')
$taskOriginal=@{};foreach($taskKey in $taskKeys){$taskOriginal[$taskKey]=[Environment]::GetEnvironmentVariable($taskKey)}
try {
    if(!$NoBuild){& (Join-Path $taskRoot 'scripts/Build.ps1') -Target Smoke -NoRestore}
    foreach($taskCase in $taskCases){
        $taskRun=$Prefix+'-'+$taskCase.Name
        $taskPath=Join-Path $taskRoot ('.test-results/'+$taskRun)
        $env:HOLLOWCONNECT_BOSS_VICTORY_CHECK='1';$env:HOLLOWCONNECT_BOSS_VICTORY_CASE=$taskCase.Case
        $env:HOLLOWCONNECT_BOSS_VICTORY_AUTHORITY=$taskCase.Authority;$env:HOLLOWCONNECT_BOSS_VICTORY_DISABLED=$taskCase.Disabled
        $env:HOLLOWCONNECT_FINAL_CASE=if($taskCase.Native -eq '1'){'native'}else{'continuation'}
        $env:HOLLOWCONNECT_SETTLEMENT_SPECTATOR=$taskCase.Spectator;$env:HOLLOWCONNECT_FINAL_SPECTATOR=$taskCase.Spectator
        $env:HOLLOWCONNECT_FINAL_BOSS_RESCUE='1';$env:HOLLOWCONNECT_NETWORK_FAULTS='0'
        $taskMode=if($taskCase.Case -eq 'final'){'final-ending'}else{'rescue'}
        & (Join-Path $PSScriptRoot 'Run-Two.ps1') -RunName $taskRun -Mode $taskMode -NoBuild
        $taskCase | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $taskPath 'case.json') -Encoding UTF8
        (Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'bin/Release/net472/HollowConnect.Smoke.dll')).Hash | Set-Content -LiteralPath (Join-Path $taskPath 'probe-sha256.txt')
        try {
            $taskDeadline=[DateTime]::UtcNow.AddSeconds(210)
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
                if($taskProcess -and [string]::Equals($taskProcess.Path,$taskExe,[StringComparison]::OrdinalIgnoreCase)){Stop-Process -Id $taskPid -Force;$taskProcess.WaitForExit()}
            }
        }
        & (Join-Path $PSScriptRoot 'Assert-ProbeLogs.ps1') -RunPath $taskPath -RequireRuntimeFiles
        'PASS: both endpoints and strict runtime logs.' | Set-Content -LiteralPath (Join-Path $taskPath 'strict-pass.txt')
        Write-Output ('PASS '+$taskRun)
    }
}finally{foreach($taskKey in $taskKeys){[Environment]::SetEnvironmentVariable($taskKey,$taskOriginal[$taskKey])}}
