$ErrorActionPreference='Stop'
$taskRoot=Split-Path -Parent $PSScriptRoot
. (Join-Path $taskRoot 'scripts\PackageTools.ps1')
$taskFixture=Join-Path $taskRoot ('.test-results\workflow-'+[Guid]::NewGuid().ToString('N'))
$taskInput=Join-Path $taskFixture 'input [中文]'
$taskOutput=Join-Path $taskFixture 'dist'
New-Item -ItemType Directory -Path $taskInput,$taskOutput,(Join-Path $taskInput 'bin\nested'),(Join-Path $taskInput 'obj'),(Join-Path $taskInput 'nested') | Out-Null
$taskFiles=@{'main.cs'='source';'nested\data.json'='{}';'bin\nested\private.dll'='private';'obj\generated.cs'='generated';'pending.cs.draft'='draft'}
foreach ($taskName in $taskFiles.Keys) { [IO.File]::WriteAllText((Join-Path $taskInput $taskName),$taskFiles[$taskName]) }
function Assert-Workflow($Condition,[string]$Message) {
    if (!$Condition) { throw $Message }
    Write-Output ('PASS '+$Message)
}
$taskSources=@(Get-HollowSourceFiles -Path $taskInput)
Assert-Workflow ($taskSources.Count -eq 2) 'source walk prunes bin, obj and drafts before archive creation'
$taskZip=Join-Path $taskOutput 'HollowConnect-current-source.zip'
Write-HollowArchive -Destination $taskZip -BasePath $taskInput -Files $taskSources
$taskArchive=[IO.Compression.ZipFile]::OpenRead($taskZip)
try {
    Assert-Workflow (@($taskArchive.Entries).Count -eq 2 -and 'nested/data.json' -in $taskArchive.Entries.FullName) 'ZIP preserves relative paths for nested and Unicode input folders'
    foreach ($taskEntry in $taskArchive.Entries) {
        $taskStream=$taskEntry.Open()
        $taskSha=[Security.Cryptography.SHA256]::Create()
        try { $taskHash=[BitConverter]::ToString($taskSha.ComputeHash($taskStream)).Replace('-','') }
        finally { $taskSha.Dispose(); $taskStream.Dispose() }
        if ($taskHash -ne (Get-FileHash -LiteralPath (Join-Path $taskInput $taskEntry.FullName)).Hash) { throw 'Archive bytes differ from source.' }
    }
    Assert-Workflow $true 'every archived file retains its exact source bytes'
} finally { $taskArchive.Dispose() }
$taskOriginalHash=(Get-FileHash -LiteralPath $taskZip).Hash
$taskOutside=Join-Path $taskFixture 'outside.cs'
[IO.File]::WriteAllText($taskOutside,'outside')
$taskRejected=$false
try { Write-HollowArchive -Destination $taskZip -BasePath $taskInput -Files @(Get-Item -LiteralPath $taskOutside) } catch { $taskRejected=$true }
Assert-Workflow ($taskRejected -and (Get-FileHash -LiteralPath $taskZip).Hash -eq $taskOriginalHash) 'out-of-root input is rejected without damaging an existing archive'
$taskMissing=Join-Path $taskInput 'vanishing.cs'
[IO.File]::WriteAllText($taskMissing,'temporary')
$taskMissingFile=Get-Item -LiteralPath $taskMissing
Remove-Item -LiteralPath $taskMissing
$taskRejected=$false
try { Write-HollowArchive -Destination $taskZip -BasePath $taskInput -Files @($taskMissingFile) } catch { $taskRejected=$true }
Assert-Workflow ($taskRejected -and (Get-FileHash -LiteralPath $taskZip).Hash -eq $taskOriginalHash -and !(Get-ChildItem -LiteralPath $taskOutput -Filter '*.tmp')) 'mid-write failure preserves the old ZIP and removes only its temporary file'
Update-HollowChecksums -Directory $taskOutput -ChangedArchives ([IO.Path]::GetFileName($taskZip))
$taskSums=Get-Content -LiteralPath (Join-Path $taskOutput 'SHA256SUMS.txt')
Assert-Workflow ($taskSums -eq ($taskOriginalHash.ToLowerInvariant()+'  '+[IO.Path]::GetFileName($taskZip))) 'checksum index records the newly generated archive'
$taskOld=Join-Path $taskOutput 'HollowConnect-old-windows.zip'
[IO.File]::WriteAllText($taskOld,'old archive bytes')
Update-HollowChecksums -Directory $taskOutput -ChangedArchives ([IO.Path]::GetFileName($taskZip))
$taskOldHash=(Get-FileHash -LiteralPath $taskOld).Hash.ToLowerInvariant()
Assert-Workflow ((Get-Content -LiteralPath (Join-Path $taskOutput 'SHA256SUMS.txt')) -contains ($taskOldHash+'  '+[IO.Path]::GetFileName($taskOld))) 'missing historical checksum is filled once'
$taskLocked=[IO.File]::Open($taskOld,[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
try {
    Update-HollowChecksums -Directory $taskOutput -ChangedArchives ([IO.Path]::GetFileName($taskZip))
    Assert-Workflow $true 'ordinary packaging reuses the historical hash without reading a locked old archive'
} finally { $taskLocked.Dispose() }
[IO.File]::WriteAllText($taskOld,'changed historical bytes')
Update-HollowChecksums -Directory $taskOutput -ChangedArchives ([IO.Path]::GetFileName($taskZip)) -Rebuild
Assert-Workflow ((Get-Content -LiteralPath (Join-Path $taskOutput 'SHA256SUMS.txt')) -contains ((Get-FileHash -LiteralPath $taskOld).Hash.ToLowerInvariant()+'  '+[IO.Path]::GetFileName($taskOld))) 'explicit checksum rebuild detects changes in historical archives'
Write-Output ('FINAL PASS workflow file checks; evidence '+$taskFixture)
