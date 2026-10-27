param([Parameter(Mandatory)][string]$Prefix,[string[]]$Only=@(),[switch]$NoBuild)
$ErrorActionPreference='Stop'
$taskRoot=Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$taskCases=foreach($taskMode in 'together','partial','late','outside','reentry','disconnect','boundary') {
    foreach($taskAuthority in 'host','guest') {
        @{Name=$taskMode+'-'+$taskAuthority;Mode=$taskMode;Authority=$taskAuthority}
    }
}
foreach($taskName in $Only){if($taskName -notin $taskCases.Name){throw ('Unknown case '+$taskName)}}
if($Only.Count){$taskCases=@($taskCases | Where-Object {$_.Name -in $Only})}
else{$taskCases=@($taskCases | Where-Object {$_.Mode -in 'together','partial','late','outside'})}
$taskKeys=@('HOLLOWCONNECT_LOCAL_BATTLE_CHECK','HOLLOWCONNECT_LOCAL_BATTLE_AUTHORITY','HOLLOWCONNECT_NETWORK_FAULTS')
$taskOriginal=@{};foreach($taskKey in $taskKeys){$taskOriginal[$taskKey]=[Environment]::GetEnvironmentVariable($taskKey)}
try {
    if(!$NoBuild){& (Join-Path $taskRoot 'scripts/Build.ps1') -Target Smoke -NoRestore}
    foreach($taskCase in $taskCases){
        $taskRun=$Prefix+'-'+$taskCase.Name
        $taskPath=Join-Path $taskRoot ('.test-results/'+$taskRun)
        $env:HOLLOWCONNECT_LOCAL_BATTLE_CHECK=$taskCase.Mode
        $env:HOLLOWCONNECT_LOCAL_BATTLE_AUTHORITY=$taskCase.Authority
        $env:HOLLOWCONNECT_NETWORK_FAULTS='0'
        & (Join-Path $PSScriptRoot 'Run-Two.ps1') -RunName $taskRun -Mode rescue -NoBuild
        $taskCase | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $taskPath 'case.json') -Encoding UTF8
        (Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'bin/Release/net472/HollowConnect.Smoke.dll')).Hash | Set-Content -LiteralPath (Join-Path $taskPath 'probe-sha256.txt')
        try {
            $taskDeadline=[DateTime]::UtcNow.AddSeconds(150)
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
        'PASS: both endpoints, native rewards and walking through both gates; strict runtime logs.' | Set-Content -LiteralPath (Join-Path $taskPath 'strict-pass.txt')
        Write-Output ('PASS '+$taskRun)
    }
}finally{foreach($taskKey in $taskKeys){[Environment]::SetEnvironmentVariable($taskKey,$taskOriginal[$taskKey])}}
