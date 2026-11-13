param([string]$OutputRoot)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
if (!$OutputRoot) { $OutputRoot = Join-Path $taskRoot ('.test-results/build-references-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmssfff')) }
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
if (Test-Path -LiteralPath $OutputRoot) { throw 'Preserve existing evidence; choose a new output directory.' }
$taskFixture = Join-Path $OutputRoot 'workspace'
$taskGame = Join-Path $OutputRoot 'newer game'
$taskScripts = Join-Path $taskFixture 'scripts'
$taskRefs = Join-Path $taskFixture 'vendor/references'
$taskManaged = Join-Path $taskGame 'hollow_knight_Data/Managed'
New-Item -ItemType Directory -Path $taskScripts,$taskRefs,$taskManaged | Out-Null
foreach ($taskName in 'Build.ps1','GamePath.ps1','Prepare-Unity6.ps1') { Copy-Item -LiteralPath (Join-Path $taskRoot "scripts/$taskName") -Destination $taskScripts }
[IO.File]::WriteAllText((Join-Path $taskGame 'hollow_knight.exe'), 'fixture, never launched')
foreach ($taskName in 'Assembly-CSharp.dll','PlayMaker.dll','new-game-only.dll') {
    [IO.File]::WriteAllText((Join-Path $taskManaged $taskName), 'unsupported game fixture ' + $taskName)
}
$taskReferenceNames = 'Assembly-CSharp.dll','Assembly-CSharp-firstpass.dll','PlayMaker.dll',
    'MMHOOK_Assembly-CSharp.dll','MMHOOK_PlayMaker.dll','MonoMod.RuntimeDetour.dll',
    'UnityEngine.CoreModule.dll','UnityEngine.IMGUIModule.dll'
foreach ($taskName in $taskReferenceNames) { [IO.File]::WriteAllText((Join-Path $taskRefs $taskName), 'keep existing reference ' + $taskName) }
$taskBefore = @(Get-ChildItem -LiteralPath $taskRefs -File | Get-FileHash -Algorithm SHA256 | Select-Object Path,Hash) | ConvertTo-Json -Compress
$taskGameBefore = @(Get-ChildItem -LiteralPath $taskManaged -File | Get-FileHash -Algorithm SHA256 | Select-Object Path,Hash) | ConvertTo-Json -Compress
# Neither dependency acquisition nor the compiler may run on rejection.
function Invoke-WebRequest { throw 'Unexpected API download' }
function dotnet { throw 'Unexpected compiler invocation' }
$taskExpectedHash = (Get-FileHash -LiteralPath (Join-Path $taskManaged 'Assembly-CSharp.dll')).Hash
function Assert-RejectedBuild([bool]$Refresh) {
    $taskFailure = $null
    try { & (Join-Path $taskScripts 'Build.ps1') -GamePath $taskGame -RefreshReferences:$Refresh }
    catch { $taskFailure = $_.Exception.Message }
    if (!$taskFailure -or !$taskFailure.Contains('Unsupported game assembly') -or !$taskFailure.Contains($taskExpectedHash)) {
        throw ('Expected version-specific rejection before download/build, got: ' + $taskFailure)
    }
    if ((@(Get-ChildItem -LiteralPath $taskRefs -File | Get-FileHash -Algorithm SHA256 | Select-Object Path,Hash) | ConvertTo-Json -Compress) -ne $taskBefore) { throw 'Rejected refresh changed existing references.' }
    if (Test-Path -LiteralPath (Join-Path $taskFixture 'vendor/ModdingAPI')) { throw 'Rejected refresh extracted the old API.' }
    if ((@(Get-ChildItem -LiteralPath $taskManaged -File | Get-FileHash -Algorithm SHA256 | Select-Object Path,Hash) | ConvertTo-Json -Compress) -ne $taskGameBefore) { throw 'Rejected refresh changed the selected game.' }
}
Assert-RejectedBuild $true
Write-Output 'PASS explicit refresh rejects an unsupported game before changing references or acquiring the old API'
Remove-Item -LiteralPath (Join-Path $taskRefs 'MMHOOK_PlayMaker.dll')
$taskBefore = @(Get-ChildItem -LiteralPath $taskRefs -File | Get-FileHash -Algorithm SHA256 | Select-Object Path,Hash) | ConvertTo-Json -Compress
Assert-RejectedBuild $false
Write-Output 'PASS missing-reference setup also preserves existing references and the selected game'
foreach ($taskRefresh in @($true,$false)) {
    $taskFailure=$null
    try { & (Join-Path $taskScripts 'Build.ps1') -GameVersion Unity6 -GamePath $taskGame -RefreshReferences:$taskRefresh }
    catch { $taskFailure=$_.Exception.Message }
    if (!$taskFailure -or !$taskFailure.Contains('requires the original Windows 1.5.12620')) { throw 'Unity6 setup did not reject unknown game input.' }
    if (Test-Path -LiteralPath (Join-Path $taskFixture 'vendor/references-unity6')) { throw 'Rejected Unity6 setup created references.' }
    if ((@(Get-ChildItem -LiteralPath $taskRefs -File | Get-FileHash -Algorithm SHA256 | Select-Object Path,Hash) | ConvertTo-Json -Compress) -ne $taskBefore) { throw 'Rejected Unity6 setup changed legacy references.' }
    if ((@(Get-ChildItem -LiteralPath $taskManaged -File | Get-FileHash -Algorithm SHA256 | Select-Object Path,Hash) | ConvertTo-Json -Compress) -ne $taskGameBefore) { throw 'Rejected Unity6 setup changed the game.' }
    Write-Output ('PASS Unity6 rejects unknown input before preparation; explicit refresh='+$taskRefresh)
}
Write-Output 'RESULT 4 passed, 0 failed'
Write-Output "Evidence: $OutputRoot"
