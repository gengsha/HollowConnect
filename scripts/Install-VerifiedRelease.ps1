param([Parameter(Mandatory=$true)][string]$GamePath)
$ErrorActionPreference='Stop'
$taskRoot=Split-Path -Parent $PSScriptRoot
$taskBlockers=Get-Content -LiteralPath (Join-Path $taskRoot 'docs/evidence/runtime-blockers-0.1.13.json') -Raw | ConvertFrom-Json
if($taskBlockers.version -ne '0.1.13' -or @($taskBlockers.findings | Where-Object {$_.resolved -ne $true}).Count){throw 'Unresolved runtime blocker; installation refused'}
$taskPackage=Join-Path $taskRoot 'dist\HollowConnect-0.1.13'
$taskManifest=Get-Content -LiteralPath (Join-Path $taskPackage 'manifest.json') -Raw | ConvertFrom-Json
$taskRuntime=Get-Content -LiteralPath (Join-Path $taskRoot 'docs\evidence\runtime-room-0.1.13.json') -Raw | ConvertFrom-Json
if($taskRuntime.releaseReady -ne $true -or $taskManifest.version -ne '0.1.13' -or @($taskRuntime.runs).Count -ne 12){throw 'Release or runtime evidence incomplete or blocked'}
foreach($taskRun in $taskRuntime.runs){
    & (Join-Path $taskRoot 'tests/Smoke/Assert-ProbeLogs.ps1') -RunPath (Join-Path $taskRoot ('.test-results/'+$taskRun.name)) -RequireRuntimeFiles
    if(!$taskRun.bothPassed){throw 'Runtime probe did not pass'}
    foreach($taskBinary in $taskRun.binaries){
        if(@($taskManifest.files | Where-Object {$_.name -eq $taskBinary.name -and $_.sha256 -eq $taskBinary.sha256}).Count -ne 1){throw 'Runtime binary differs from manifest'}
    }
}
if(@(Get-Process -Name hollow_knight -ErrorAction SilentlyContinue).Count){throw 'Game processes must be stopped before install'}
$taskSaveRoot=Join-Path ([Environment]::GetFolderPath('UserProfile')) 'AppData\LocalLow\Team Cherry\Hollow Knight'
function Get-TaskSaveHashes {
    @(Get-ChildItem -LiteralPath $taskSaveRoot -File | Where-Object { $_.Name -match '^user\d+' } | ForEach-Object {
        [pscustomobject]@{name=$_.Name;sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}
    })
}
# Capture a fresh baseline immediately before this install, never a historical
# baseline from an older release or a count treated as a checksum comparison.
$taskBefore=Get-TaskSaveHashes
$taskPrivateOutput=Join-Path $taskRoot '.test-results'
$taskBefore | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $taskPrivateOutput 'save-before-upgrade-013.json') -Encoding UTF8
$taskManaged=Join-Path $GamePath 'hollow_knight_Data\Managed'
$taskPointer=Join-Path $GamePath '.HollowConnectBackups\latest.txt'
$taskPointerBefore=(Get-Content -LiteralPath $taskPointer -Raw).Trim()
$taskNativeBefore=@{}
foreach($taskName in 'Assembly-CSharp.dll','PlayMaker.dll'){$taskNativeBefore[$taskName]=(Get-FileHash -LiteralPath (Join-Path $taskManaged $taskName) -Algorithm SHA256).Hash}
$taskInstaller=Join-Path $taskPackage '一键安装.exe'
$taskStdout=Join-Path $taskPrivateOutput 'upgrade-013.stdout.log'
$taskStderr=Join-Path $taskPrivateOutput 'upgrade-013.stderr.log'
$taskProcess=Start-Process -FilePath $taskInstaller -ArgumentList @('--install',('"'+$GamePath+'"')) -WindowStyle Hidden -Wait -PassThru -RedirectStandardOutput $taskStdout -RedirectStandardError $taskStderr
Get-Content -LiteralPath $taskStdout
if($taskProcess.ExitCode -ne 0){Get-Content -LiteralPath $taskStderr;throw 'Installer failed'}
foreach($taskEntry in $taskManifest.files){
    if((Get-FileHash -LiteralPath (Join-Path $taskManaged ('Mods\HKMP\'+$taskEntry.name)) -Algorithm SHA256).Hash -ne $taskEntry.sha256){throw ('Installed file mismatch '+$taskEntry.name)}
}
foreach($taskName in $taskNativeBefore.Keys){if((Get-FileHash -LiteralPath (Join-Path $taskManaged $taskName) -Algorithm SHA256).Hash -ne $taskNativeBefore[$taskName]){throw ('Native assembly changed '+$taskName)}}
if((Get-Content -LiteralPath $taskPointer -Raw).Trim() -ne $taskPointerBefore){throw 'Original backup pointer changed'}
if(@(Get-ChildItem -LiteralPath (Join-Path $taskManaged 'Mods') -Recurse -Filter 'HollowConnect.Smoke.dll' -File).Count){throw 'Test plugin in normal game'}
$taskAfter=Get-TaskSaveHashes
if(@(Compare-Object -ReferenceObject $taskBefore -DifferenceObject $taskAfter -Property name,sha256).Count){throw 'Save names or hashes changed'}
[ordered]@{version='0.1.13';checkedAtUtc=[DateTime]::UtcNow.ToString('O');packagedInstallerUsed=$true;installedFilesChecked=@($taskManifest.files).Count;installedHashesMatch=$true;runtimeRunsChecked=@($taskRuntime.runs).Count;runtimeHashesMatchManifest=$true;nativeAssemblyHashesUnchanged=$true;saveFilesChecked=@($taskAfter).Count;saveHashesUnchanged=$true;saveBaseline='Captured immediately before this installation';testPluginAbsent=$true;originalBackupRetained=$true;twoMachineFrpValidation=$false} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $taskRoot 'docs\evidence\upgrade-0.1.13.json') -Encoding UTF8
Write-Output ('PASS 0.1.13 install: '+@($taskManifest.files).Count+' payload files; '+@($taskAfter).Count+' fresh save hashes unchanged; runtime hashes; backup; no test plugin.')
