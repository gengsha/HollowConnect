param([Parameter(Mandatory)][string]$RunPath,[switch]$RequireRuntimeFiles,
    [ValidateSet('','System.Exception: Frozen camera without native hazard recovery injected-nonhazard','System.Exception: Timeout: native hazard camera recovery injected-persistent-hazard')]
    [string]$ExpectedFailureReason='')
$ErrorActionPreference='Stop'
if($RequireRuntimeFiles){foreach($taskRequired in 'binaries.json','host.log','guest.log','host-unity.log','guest-unity.log'){
    if(!(Test-Path -LiteralPath (Join-Path $RunPath $taskRequired))){throw ('Missing runtime log/artifact '+$taskRequired)}
}}
$taskMarkers=@(Get-ChildItem -LiteralPath $RunPath -Filter '*.failed' -File)
if($ExpectedFailureReason){
    # Only the two explicit camera fault injections may expect a failed probe.
    # Production acceptance never supplies this parameter. Still scan every
    # log and reject all errors except the exact injected FAIL line.
    if($taskMarkers.Count -ne 2 -or @(Get-ChildItem -LiteralPath $RunPath -Filter '*.done' -File).Count){throw 'Invalid expected-failure camera markers'}
    foreach($taskRole in 'host','guest'){
        $taskFailure=Get-Content -LiteralPath (Join-Path $RunPath ($taskRole+'.failed')) -Raw
        if($taskFailure.Split("`n")[0].TrimEnd("`r") -ne $ExpectedFailureReason){throw 'Unexpected camera failure marker'}
    }
}elseif($taskMarkers.Count){throw 'Probe has a failure marker (including failures after done).'}
$taskLogs=@(Get-ChildItem -LiteralPath $RunPath -Filter '*.log' -File)
if(!$taskLogs.Count){throw 'Probe logs are missing.'}
foreach($taskLog in $taskLogs){
    $taskText=Get-Content -LiteralPath $taskLog.FullName -Raw
    if(!$taskText){continue}
    if($ExpectedFailureReason){
        $taskText=[regex]::Replace($taskText,'(?m)^[0-9TZ:.+-]+ FAIL '+[regex]::Escape($ExpectedFailureReason)+'\r?$','')
    }
    if($taskText -match '(?im)^\s*(?:Crash!!!|A crash has been intercepted by the crash handler)|caused an Access Violation'){
        throw ('Native crash signature in '+$taskLog.Name)
    }
    if($taskText -match '(?im)^(?:[0-9TZ:.+-]+ )?(?:(?:ENGINE|UNITY|HKMP) )?(?:FAIL|ERROR) |Spawn expired|^\[ERROR\].*(?:HKMP|HollowConnect)'){
        throw ('Strict log gate rejected '+$taskLog.Name)
    }
    # Unity prints exception and stack on separate lines. Limit a stack block
    # at a blank line/new timestamp so unrelated native warnings stay distinct.
    # Scan candidate exception lines rather than retrying an unanchored
    # greedy word match at every character of multi-megabyte Unity logs.
    # Keep the same exception-block boundary and maximum of 35 next lines.
    $taskLines=$taskText -split '\r?\n'
    for($taskIndex=0;$taskIndex -lt $taskLines.Count;$taskIndex++){
        if($taskLines[$taskIndex].IndexOf('Exception',[StringComparison]::OrdinalIgnoreCase) -lt 0){continue}
        $taskException=[regex]::Match($taskLines[$taskIndex],'(?i)\b\w+Exception')
        if(!$taskException.Success){continue}
        if($taskLines[$taskIndex].Substring($taskException.Index) -match 'Hkmp\.|HollowConnect\.'){
            throw ('Mod exception stack in '+$taskLog.Name)
        }
        for($taskStack=$taskIndex+1;$taskStack -lt $taskLines.Count -and $taskStack -le $taskIndex+35;$taskStack++){
            if($taskLines[$taskStack] -match '^\s*$|^\d{4}-\d{2}-\d{2}T'){break}
            if($taskLines[$taskStack] -match 'Hkmp\.|HollowConnect\.'){
                throw ('Mod exception stack in '+$taskLog.Name)
            }
        }
    }
}
