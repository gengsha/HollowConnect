param([string]$OutputRoot)
$ErrorActionPreference='Stop'
$taskRoot=Split-Path -Parent $PSScriptRoot
if(!$OutputRoot){$OutputRoot=Join-Path $taskRoot ('.test-results/installer-process-'+[DateTime]::UtcNow.ToString('yyyyMMdd-HHmmssfff'))}
[IO.Directory]::CreateDirectory($OutputRoot) | Out-Null
$OutputRoot=[IO.Path]::GetFullPath($OutputRoot)
. (Join-Path $taskRoot 'scripts/GamePath.ps1')
$taskSelected=Join-Path $OutputRoot 'selected'
$taskOther=Join-Path $OutputRoot 'other'
$taskExecutable=Join-Path $taskSelected 'hollow_knight.exe'
$taskOtherExecutable=Join-Path $taskOther 'hollow_knight.exe'
$taskPassed=0
function Assert-ProcessCheck($Condition,[string]$Message){if(!$Condition){throw $Message};$script:taskPassed++;Write-Output ('PASS '+$Message)}
function Invoke-ProcessFixture($CimRecords,$ProcessRecords,[bool]$CimUnavailable=$false){
    & {
        function Get-CimInstance { if($CimUnavailable){throw 'CIM unavailable'}; $CimRecords }
        function Get-Process { param($Name,$Id,$ErrorAction); $ProcessRecords }
        try { Assert-HollowKnightStopped $taskSelected; [pscustomobject]@{blocked=$false;message=''} }
        catch { [pscustomobject]@{blocked=$true;message=$_.Exception.Message} }
    }
}
$taskResult=Invoke-ProcessFixture @([pscustomobject]@{ProcessId=101;ThreadCount=0;ExecutablePath=$null}) @()
Assert-ProcessCheck (!$taskResult.blocked) 'exited zero-thread record with unreadable path does not block installation'
$taskResult=Invoke-ProcessFixture @([pscustomobject]@{ProcessId=102;ThreadCount=1;ExecutablePath=$taskExecutable}) @()
Assert-ProcessCheck ($taskResult.blocked -and $taskResult.message.Contains('102') -and $taskResult.message.Contains($taskExecutable)) 'running selected game is blocked with its PID and executable'
$taskResult=Invoke-ProcessFixture @([pscustomobject]@{ProcessId=101;ThreadCount=0;ExecutablePath=$null},[pscustomobject]@{ProcessId=102;ThreadCount=1;ExecutablePath=$taskExecutable}) @()
Assert-ProcessCheck ($taskResult.blocked -and $taskResult.message.Contains('102')) 'stale record does not hide another running game process'
$taskResult=Invoke-ProcessFixture @([pscustomobject]@{ProcessId=103;ThreadCount=2;ExecutablePath=$taskOtherExecutable}) @()
Assert-ProcessCheck (!$taskResult.blocked) 'running game in another directory does not block the selected copy'
$taskResult=Invoke-ProcessFixture @([pscustomobject]@{ProcessId=104;ThreadCount=2;ExecutablePath=$null}) @()
Assert-ProcessCheck ($taskResult.blocked -and $taskResult.message.Contains('Cannot verify') -and $taskResult.message.Contains('104')) 'live process with unreadable path is reported as unverified and remains blocked'
$taskResult=Invoke-ProcessFixture @([pscustomobject]@{ProcessId=105;ThreadCount=$null;ExecutablePath=$null}) @()
Assert-ProcessCheck $taskResult.blocked 'missing thread count is not treated as proof that the game exited'
$taskResult=Invoke-ProcessFixture @([pscustomobject]@{ProcessId=106;ThreadCount=1;ExecutablePath=$null}) @([pscustomobject]@{Id=106;Path=$taskExecutable})
Assert-ProcessCheck ($taskResult.blocked -and $taskResult.message.Contains('Close Hollow Knight')) 'process-path fallback still blocks the selected live game'
$taskResult=Invoke-ProcessFixture @() @([pscustomobject]@{Id=107;HasExited=$true;Path=$null}) $true
Assert-ProcessCheck (!$taskResult.blocked) 'CIM-unavailable fallback ignores a positively exited process'
$taskResult=Invoke-ProcessFixture @() @([pscustomobject]@{Id=108;HasExited=$false;Path=$taskExecutable}) $true
Assert-ProcessCheck $taskResult.blocked 'CIM-unavailable fallback still blocks the selected live process'
$taskResult=Invoke-ProcessFixture @() @([pscustomobject]@{Id=109;HasExited=$false;Path=$null}) $true
Assert-ProcessCheck ($taskResult.blocked -and $taskResult.message.Contains('Cannot verify')) 'CIM failure and denied path do not disable the running-game guard'

# A small sleeping .NET helper is used instead of launching the user's game.
# Keep its process handle open after exit to exercise a retained Windows record.
[IO.Directory]::CreateDirectory($taskSelected) | Out-Null
[IO.Directory]::CreateDirectory($taskOther) | Out-Null
$taskHelperSource=Join-Path $OutputRoot 'ProcessFixture.cs'
[IO.File]::WriteAllText($taskHelperSource,'class ProcessFixture { static void Main() { System.Threading.Thread.Sleep(60000); } }')
$taskCompiler=Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
& $taskCompiler /nologo /target:exe "/out:$taskExecutable" $taskHelperSource
if($LASTEXITCODE -ne 0){throw 'Process helper compilation failed'}
$taskHelper=$null
try {
    $taskHelper=Start-Process -FilePath $taskExecutable -WindowStyle Hidden -PassThru
    $taskRetainedHandle=$taskHelper.Handle
    $taskBlocked=$false
    try { Assert-HollowKnightStopped $taskSelected } catch { $taskBlocked=$_.Exception.Message.Contains([string]$taskHelper.Id) }
    Assert-ProcessCheck $taskBlocked 'actual live helper with the game executable name is blocked'
    Assert-HollowKnightStopped $taskOther
    Assert-ProcessCheck $true 'actual live helper permits a different installation directory'
    $taskWinPs=Join-Path $env:WINDIR 'System32/WindowsPowerShell/v1.0/powershell.exe'
    $taskGuardPath=(Join-Path $taskRoot 'scripts/GamePath.ps1').Replace("'","''")
    $taskSelectedLiteral=$taskSelected.Replace("'","''")
    $taskCommand="`$ErrorActionPreference='Stop'; . '$taskGuardPath'; try { Assert-HollowKnightStopped '$taskSelectedLiteral'; exit 2 } catch { [Console]::WriteLine(`$_.Exception.Message); if(`$_.Exception.Message.Contains('$($taskHelper.Id)')){exit 0}; exit 3 }"
    & $taskWinPs -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand ([Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($taskCommand)))
    Assert-ProcessCheck ($LASTEXITCODE -eq 0) 'installer Windows PowerShell runtime blocks the actual selected live process'
    $taskHelper.Kill(); $taskHelper.WaitForExit()
    Assert-HollowKnightStopped $taskSelected
    Assert-ProcessCheck $true 'exited helper with a retained process handle no longer blocks installation'
} finally {
    if($taskHelper){
        if(!$taskHelper.HasExited){
            if([IO.Path]::GetFullPath($taskHelper.Path) -ne $taskExecutable){throw 'Unexpected helper executable; refusing cleanup'}
            $taskHelper.Kill(); $taskHelper.WaitForExit()
        }
        $taskHelper.Dispose()
    }
}
Write-Output "RESULT $taskPassed passed, 0 failed"
Write-Output "Evidence: $OutputRoot"
