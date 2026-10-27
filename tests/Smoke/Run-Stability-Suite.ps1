param([Parameter(Mandatory)][ValidatePattern('^[a-z0-9][a-z0-9-]{0,50}$')][string]$RunPrefix,
    [ValidateSet('resources','full','defender','arena-host','arena-guest','combat-profile')][string[]]$Only)
$ErrorActionPreference='Stop'
$stabilityRoot=Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
function Stop-StabilityGames {
    foreach($stabilityRole in 'host','guest') {
        $stabilityExe=Join-Path $stabilityRoot ".test-results\game-$stabilityRole\hollow_knight.exe"
        foreach($stabilityProcess in Get-Process hollow_knight -ErrorAction SilentlyContinue | Where-Object {$_.Path -eq $stabilityExe}) {
            Stop-Process -Id $stabilityProcess.Id -Force -ErrorAction SilentlyContinue
            $stabilityProcess.WaitForExit()
        }
    }
}
$stabilityCases=@(
    @{Name='resources';Mode='progress';Env=@{HOLLOWCONNECT_RESOURCE_CHECK='1'}},
    @{Name='full';Mode='full';Env=@{}},
    @{Name='defender';Mode='progress';Env=@{HOLLOWCONNECT_DEFENDER_CHECK='1'}},
    @{Name='arena-host';Arena='host';Env=@{}},
    @{Name='arena-guest';Arena='guest';Env=@{}},
    @{Name='combat-profile';Mode='coverage';Env=@{HOLLOWCONNECT_COVERAGE_SCENES='GG_Vengefly,GG_White_Defender,GG_Grimm,GG_Nailmasters';HOLLOWCONNECT_COVERAGE_SECONDS='30';HOLLOWCONNECT_COVERAGE_PROFILE='1';HOLLOWCONNECT_COVERAGE_PERFORMANCE='1';HOLLOWCONNECT_PERFORMANCE_ASSERTIONS='1'}}
)
$stabilityResults=@()
try {
    & (Join-Path $stabilityRoot 'scripts\Build.ps1') -Target Smoke
    foreach($stabilityCase in $stabilityCases) {
        if($Only -and $stabilityCase.Name -notin $Only){continue}
        Stop-StabilityGames
        foreach($stabilityEnv in @(Get-ChildItem Env: | Where-Object {$_.Name -like 'HOLLOWCONNECT_*'})){
            [Environment]::SetEnvironmentVariable($stabilityEnv.Name,$null,'Process')
        }
        foreach($stabilityKey in $stabilityCase.Env.Keys){[Environment]::SetEnvironmentVariable($stabilityKey,$stabilityCase.Env[$stabilityKey],'Process')}
        $stabilityRun=$RunPrefix+'-'+$stabilityCase.Name
        $stabilityDir=Join-Path $stabilityRoot ('.test-results/'+$stabilityRun)
        if(Test-Path -LiteralPath $stabilityDir){throw 'Preserve previous evidence; choose another run prefix.'}
        if($stabilityCase.Arena){
            & (Join-Path $PSScriptRoot 'Run-Colosseum.ps1') -RunName $stabilityRun -First $stabilityCase.Arena -Trial Bronze -Case reward-continuation
        }else{
            & (Join-Path $PSScriptRoot 'Run-Two.ps1') -RunName $stabilityRun -Mode $stabilityCase.Mode -NoBuild
        }
        $stabilityDeadline=[DateTime]::UtcNow.AddMinutes(10)
        while(!(Test-Path -LiteralPath (Join-Path $stabilityDir 'host.done')) -or !(Test-Path -LiteralPath (Join-Path $stabilityDir 'guest.done'))){
            if(@(Get-ChildItem -LiteralPath $stabilityDir -Filter '*.failed').Count){throw ('Failed '+$stabilityRun)}
            if([DateTime]::UtcNow -gt $stabilityDeadline){throw ('Timed out '+$stabilityRun)}
            Start-Sleep -Milliseconds 500
        }
        Start-Sleep -Seconds 1
        & (Join-Path $PSScriptRoot 'Assert-ProbeLogs.ps1') -RunPath $stabilityDir -RequireRuntimeFiles
        $stabilityBinaries=Get-Content -LiteralPath (Join-Path $stabilityDir 'binaries.json') -Raw | ConvertFrom-Json
        foreach($stabilityBinary in $stabilityBinaries | Where-Object {$_.name -ne 'HollowConnect.Smoke.dll'}){
            $stabilityProduction=Join-Path $stabilityRoot ('src/HollowConnect/bin/Release/net472/'+$stabilityBinary.name)
            if((Get-FileHash -LiteralPath $stabilityProduction).Hash -ne $stabilityBinary.sha256){throw ('Runtime production hash mismatch '+$stabilityRun)}
        }
        foreach($stabilityRole in 'host','guest'){
            if(!(Select-String -LiteralPath (Join-Path $stabilityDir ($stabilityRole+'.log')) -SimpleMatch 'FINAL PASS' -Quiet)){throw ('Missing final pass '+$stabilityRun)}
        }
        $stabilityResults += [pscustomobject]@{name=$stabilityRun;bothPassed=$true;binaries=$stabilityBinaries}
        $stabilityResults | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $stabilityRoot ('.test-results/'+$RunPrefix+'-results.json'))
        Write-Output ('PASS BOTH '+$stabilityRun)
    }
}finally{Stop-StabilityGames}
