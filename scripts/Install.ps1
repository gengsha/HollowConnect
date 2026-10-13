param([string]$GamePath)
$ErrorActionPreference = 'Stop'
$package = $PSScriptRoot
. (Join-Path $PSScriptRoot 'GamePath.ps1')
$game = Resolve-HollowKnightDirectory $GamePath
$managed = Join-Path $game 'hollow_knight_Data\Managed'
if (!(Test-Path -LiteralPath (Join-Path $game 'hollow_knight.exe'))) { throw 'Hollow Knight executable not found.' }
Assert-HollowKnightStopped $game
$manifest = Get-Content -LiteralPath (Join-Path $package 'manifest.json') -Raw | ConvertFrom-Json
$assemblyHash = (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $managed 'Assembly-CSharp.dll')).Hash
if ($assemblyHash -ne $manifest.vanillaAssemblySha256 -and $assemblyHash -ne $manifest.apiAssemblySha256) {
    throw "Unsupported game/API assembly ($assemblyHash). This package targets the documented 1.5.78 Windows build."
}
$zip = Join-Path $package 'dependencies\moddingapi.v77.windows.zip'
if ((Get-FileHash -Algorithm SHA256 -LiteralPath $zip).Hash -ne $manifest.apiZipSha256) { throw 'API archive checksum mismatch.' }
foreach ($entry in $manifest.files) {
    if ($entry.name -notmatch '^[\w.-]+$') { throw 'Invalid payload filename.' }
    if ((Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $package "payload\HKMP\$($entry.name)")).Hash -ne $entry.sha256) { throw "Payload checksum mismatch: $($entry.name)" }
}
$backupRoot = Join-Path $game '.HollowConnectBackups'
$pointerPath = Join-Path $backupRoot 'latest.txt'
$originalBackup = $null
if (Test-Path -LiteralPath $pointerPath) {
    $originalBackup = (Get-Content -LiteralPath $pointerPath -Raw).Trim()
    if (!(Test-Path -LiteralPath $originalBackup -PathType Container)) { throw 'The original installation backup is missing. Restore it before updating.' }
    $originalBackup = (Resolve-Path -LiteralPath $originalBackup).ProviderPath
    if (!$originalBackup.StartsWith($backupRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'The backup pointer is outside this game directory.' }
    $originalRecord = Get-Content -LiteralPath (Join-Path $originalBackup 'install-record.json') -Raw | ConvertFrom-Json
    if ($originalRecord.game -ine $game) { throw 'The original backup belongs to another game directory.' }
}
$backup = Join-Path $game ('.HollowConnectBackups\' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
New-Item -ItemType Directory -Path $backup | Out-Null
$api = Join-Path $backup 'api-payload'
Expand-Archive -LiteralPath $zip -DestinationPath $api
$original = Join-Path $backup 'original-managed'
New-Item -ItemType Directory -Path $original | Out-Null
$records = @()
foreach ($file in Get-ChildItem -LiteralPath $api -File) {
    $destination = Join-Path $managed $file.Name
    $existed = Test-Path -LiteralPath $destination
    if ($existed) { Copy-Item -LiteralPath $destination -Destination (Join-Path $original $file.Name) }
    $records += @{name=$file.Name; existed=$existed; installedSha256=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash}
}
$savePath = Join-Path ([Environment]::GetFolderPath('UserProfile')) 'AppData\LocalLow\Team Cherry\Hollow Knight'
if (Test-Path -LiteralPath $savePath) {
    New-Item -ItemType Directory -Path (Join-Path $backup 'saves') | Out-Null
    Get-ChildItem -LiteralPath $savePath -File | Copy-Item -Destination (Join-Path $backup 'saves')
}
$modDir = Join-Path $managed 'Mods\HKMP'
if (Test-Path -LiteralPath $modDir) {
    $resolved = (Resolve-Path -LiteralPath $modDir).Path
    if (!$resolved.StartsWith($managed + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid mod directory.' }
}
$oldModMoved = $false
$newModStarted = $false
try {
    if (Test-Path -LiteralPath $modDir) {
        Move-Item -LiteralPath $modDir -Destination (Join-Path $backup 'original-HKMP')
        $oldModMoved = $true
    }
    Get-ChildItem -LiteralPath $api -File | Copy-Item -Destination $managed -Force
    New-Item -ItemType Directory -Path (Join-Path $managed 'Mods') -Force | Out-Null
    $newModStarted = $true
    if ($originalBackup -and $oldModMoved) {
        # Updates keep the player's skins, server lists and other local files.
        Copy-Item -LiteralPath (Join-Path $backup 'original-HKMP') -Destination $modDir -Recurse
    }
    Copy-Item -LiteralPath (Join-Path $package 'payload\HKMP') -Destination (Join-Path $managed 'Mods') -Recurse -Force
    foreach ($entry in $manifest.files) {
        if ((Get-FileHash -LiteralPath (Join-Path $modDir $entry.name) -Algorithm SHA256).Hash -ne $entry.sha256) { throw "Installed checksum mismatch: $($entry.name)" }
    }
    @{game=$game; version=$manifest.version; files=$records; modFiles=$manifest.files; originalBackup=$originalBackup} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $backup 'install-record.json') -Encoding UTF8
    if (!$originalBackup) { Set-Content -LiteralPath $pointerPath -Value $backup -Encoding UTF8 }
    Write-Output "Installed HollowConnect $($manifest.version). Backup: $backup"
    if ($originalBackup) { Write-Output "Original uninstall backup retained: $originalBackup" }
} catch {
    $installError = $_
    $rollbackErrors = [Collections.Generic.List[string]]::new()
    # Try every recovery step even when one file remains locked. Keep both
    # the original mod and any partial payload recoverable in the backup.
    if ($newModStarted -and (Test-Path -LiteralPath $modDir)) {
        try { Move-Item -LiteralPath $modDir -Destination (Join-Path $backup 'failed-HKMP') }
        catch { $rollbackErrors.Add('Keep partial HKMP: ' + $_.Exception.Message) }
    }
    foreach ($record in $records) {
        try {
            $destination = Join-Path $managed $record.name
            if ($record.existed) { Copy-Item -LiteralPath (Join-Path $original $record.name) -Destination $destination -Force }
            elseif (Test-Path -LiteralPath $destination) { Remove-Item -LiteralPath $destination }
        } catch { $rollbackErrors.Add('Restore ' + $record.name + ': ' + $_.Exception.Message) }
    }
    if ($oldModMoved) {
        try {
            if (Test-Path -LiteralPath $modDir) { throw 'The partial mod could not be moved aside; original-HKMP remains in the backup.' }
            Move-Item -LiteralPath (Join-Path $backup 'original-HKMP') -Destination $modDir
        } catch { $rollbackErrors.Add('Restore original HKMP: ' + $_.Exception.Message) }
    }
    $recoveryMessage = "Installation failed: $($installError.Exception.Message)`r`nBackup: $backup"
    if ($rollbackErrors.Count -gt 0) {
        $recoveryMessage += "`r`nRecovery incomplete; close programs holding game files before restoring from this backup:`r`n" + ($rollbackErrors -join "`r`n")
    } else {
        $recoveryMessage += "`r`nPre-install files restored."
    }
    try { [IO.File]::WriteAllText((Join-Path $backup 'install-failure.txt'), $recoveryMessage, [Text.UTF8Encoding]::new($true)) }
    catch { $recoveryMessage += "`r`nCould not save recovery report: $($_.Exception.Message)" }
    # The EXE displays this complete report and still returns failure.
    throw $recoveryMessage
}
