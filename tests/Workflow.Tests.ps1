$ErrorActionPreference='Stop'
$taskRoot=Split-Path -Parent $PSScriptRoot
. (Join-Path $taskRoot 'scripts\PackageTools.ps1')
. (Join-Path $taskRoot 'scripts\PackageDocumentation.ps1')
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

# Exercise document selection using a small fake repository, never the delivered ZIPs.
$taskDocRoot=Join-Path $taskFixture 'documentation'
$taskDocSelection=Import-PowerShellDataFile -LiteralPath (Join-Path $taskRoot 'scripts/Documentation.psd1')
foreach ($taskName in @($taskDocSelection.Player)+@($taskDocSelection.Developer)+@($taskDocSelection.Evidence)) {
    $taskPath=Join-Path $taskDocRoot ('docs/'+$taskName.Replace('{version}','0.2.5'))
    [IO.Directory]::CreateDirectory((Split-Path -Parent $taskPath)) | Out-Null
    [IO.File]::WriteAllText($taskPath,'fixture')
}
[IO.File]::WriteAllText((Join-Path $taskDocRoot 'docs/debug.log'),'private debugging')
[IO.File]::WriteAllText((Join-Path $taskDocRoot 'docs/unpublished-report.md'),'unpublished')
$taskRepositoryDocs=@(Get-HollowDocumentationFiles -Root $taskDocRoot -Version '0.2.5')
$taskPlayerDocs=@(Get-HollowDocumentationFiles -Root $taskDocRoot -Version '0.2.5' -Audience Player)
Assert-Workflow ($taskRepositoryDocs.Count -eq @($taskDocSelection.Player+$taskDocSelection.Developer+$taskDocSelection.Evidence).Count -and
    'DEVELOPMENT.md' -in $taskRepositoryDocs.Name -and 'debug.log' -notin $taskRepositoryDocs.Name -and
    'unpublished-report.md' -notin $taskRepositoryDocs.Name -and 'DEVELOPMENT.md' -notin $taskPlayerDocs.Name -and
    'BOUNCYCASTLE-LICENSE.md' -in $taskPlayerDocs.Name -and 'runtime-room-0.2.5.json' -in $taskPlayerDocs.Name) 'source and player documents use explicit lists retaining licenses and required evidence'
$taskDocPackage=Join-Path $taskDocRoot 'dist/package'
[IO.Directory]::CreateDirectory((Join-Path $taskDocPackage 'docs')) | Out-Null
[IO.File]::WriteAllText((Join-Path $taskDocPackage 'docs/stale-report.md'),'historical evidence')
Copy-HollowPackageDocumentation -Root $taskDocRoot -Destination $taskDocPackage -Version '0.2.5'
$taskDocBackup=@(Get-ChildItem -LiteralPath (Join-Path $taskDocRoot 'dist') -Directory -Force -Filter '.docs-history-*')
Assert-Workflow (!(Test-Path -LiteralPath (Join-Path $taskDocPackage 'docs/stale-report.md')) -and $taskDocBackup.Count -eq 1 -and
    [IO.File]::ReadAllText((Join-Path $taskDocBackup[0].FullName 'stale-report.md')) -eq 'historical evidence' -and
    @(Get-ChildItem -LiteralPath (Join-Path $taskDocPackage 'docs') -File -Recurse).Count -eq $taskPlayerDocs.Count) 'repeat packaging removes stale documents from delivery and preserves their previous bytes outside the package'
[IO.File]::WriteAllText((Join-Path $taskDocPackage 'README.md'),'[Guide](docs/DEVELOPMENT.md#build) [Player](docs/LINKED-HEALTH.md)')
[IO.File]::WriteAllText((Join-Path $taskDocPackage 'docs/README.md'),'[Guide](DEVELOPMENT.md) [Home](../README.md)')
Update-HollowPackageMarkdownLinks -Root $taskDocRoot -Package $taskDocPackage
$taskDocReadme=[IO.File]::ReadAllText((Join-Path $taskDocPackage 'README.md'))
Assert-Workflow ($taskDocReadme.Contains('https://github.com/gengsha/HollowConnect/blob/master/docs/DEVELOPMENT.md#build') -and
    $taskDocReadme.Contains('[Player](docs/LINKED-HEALTH.md)') -and
    [IO.File]::ReadAllText((Join-Path $taskDocPackage 'docs/README.md')).Contains('[Home](../README.md)')) 'player package links to omitted developer guides resolve on GitHub while bundled links remain local'
foreach ($taskRequest in 'release-authorization-0.2.5.json','playtest-authorization-0.2.5-demo.json','release-authorization-0.2.4.json') {
    [IO.File]::WriteAllText((Join-Path $taskDocRoot ('docs/evidence/'+$taskRequest)),'{}')
}
$taskPlaytestDocs=@(Get-HollowDocumentationFiles -Root $taskDocRoot -Version '0.2.5' -Audience Player -AuthorizationPath 'docs/evidence/playtest-authorization-0.2.5-demo.json')
$taskSourceDocs=@(Get-HollowDocumentationFiles -Root $taskDocRoot -Version '0.2.5')
Assert-Workflow ('playtest-authorization-0.2.5-demo.json' -in $taskPlaytestDocs.Name -and
    'release-authorization-0.2.5.json' -notin $taskPlaytestDocs.Name -and
    'release-authorization-0.2.5.json' -in $taskSourceDocs.Name -and
    'playtest-authorization-0.2.5-demo.json' -in $taskSourceDocs.Name -and
    'release-authorization-0.2.4.json' -notin $taskSourceDocs.Name) 'release and labeled playtest modes retain their own required request records without older versions'
$taskRejected=$false
try { Copy-HollowPackageDocumentation -Root $taskDocRoot -Destination $taskDocRoot -Version '0.2.5' } catch { $taskRejected=$true }
Assert-Workflow ($taskRejected -and (Test-Path -LiteralPath (Join-Path $taskDocRoot 'docs/DEVELOPMENT.md'))) 'package documentation cannot replace repository source docs'
$taskRequiredLicense=Join-Path $taskDocRoot 'docs/BOUNCYCASTLE-LICENSE.md'
Remove-Item -LiteralPath $taskRequiredLicense
$taskRejected=$false
try { Copy-HollowPackageDocumentation -Root $taskDocRoot -Destination $taskDocPackage -Version '0.2.5' } catch { $taskRejected=$true }
Assert-Workflow ($taskRejected -and [IO.File]::ReadAllText((Join-Path $taskDocPackage 'README.md')) -eq $taskDocReadme -and
    (Test-Path -LiteralPath (Join-Path $taskDocPackage 'docs/BOUNCYCASTLE-LICENSE.md'))) 'missing required documentation fails before changing an existing package'
Write-Output ('FINAL PASS workflow file checks; evidence '+$taskFixture)
