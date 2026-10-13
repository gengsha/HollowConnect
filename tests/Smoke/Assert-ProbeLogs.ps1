param([Parameter(Mandatory)][string]$RunPath)
$ErrorActionPreference='Stop'
if(@(Get-ChildItem -LiteralPath $RunPath -Filter '*.failed' -File).Count){throw 'Probe has a failure marker (including failures after done).'}
$taskLogs=@(Get-ChildItem -LiteralPath $RunPath -Filter '*.log' -File)
if(!$taskLogs.Count){throw 'Probe logs are missing.'}
foreach($taskLog in $taskLogs){
    $taskText=Get-Content -LiteralPath $taskLog.FullName -Raw
    if(!$taskText){continue}
    if($taskText -match '(?im)^(?:[0-9TZ:.+-]+ )?(?:FAIL|ERROR) |Spawn expired|^\[ERROR\].*(?:HKMP|HollowConnect)'){
        throw ('Strict log gate rejected '+$taskLog.Name)
    }
    # Unity prints exception and stack on separate lines. Limit a stack block
    # at a blank line/new timestamp so unrelated native warnings stay distinct.
    if($taskText -match '(?im)(?:NullReferenceException|MissingReferenceException|\w+Exception)[^\r\n]*(?:\r?\n(?!\s*$|\d{4}-\d{2}-\d{2}T)[^\r\n]*){0,35}(?:Hkmp\.|HollowConnect\.)'){
        throw ('Mod exception stack in '+$taskLog.Name)
    }
}
