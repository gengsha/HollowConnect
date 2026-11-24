param([string]$GamePath)
$ErrorActionPreference = 'Stop'
$package = $PSScriptRoot
. (Join-Path $PSScriptRoot 'GamePath.ps1')
$game = Resolve-HollowKnightDirectory $GamePath
$managed = Join-Path $game 'hollow_knight_Data\Managed'
if (!(Test-Path -LiteralPath (Join-Path $game 'hollow_knight.exe'))) { throw 'Hollow Knight executable not found.' }
Assert-HollowKnightStopped $game
$manifest = Get-Content -LiteralPath (Join-Path $package 'manifest.json') -Raw | ConvertFrom-Json
$taskDisplayVersion=if($manifest.displayVersion){$manifest.displayVersion}else{$manifest.version}
$assemblyHash = (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $managed 'Assembly-CSharp.dll')).Hash
if ($assemblyHash -ne $manifest.vanillaAssemblySha256 -and $assemblyHash -ne $manifest.apiAssemblySha256) {
    $taskGameVersion=if($manifest.gameVersion){$manifest.gameVersion}else{'1.5.78 Windows'}
    throw "Unsupported game/API assembly ($assemblyHash). This package targets $taskGameVersion."
}
$taskApiArchive=if($manifest.apiArchive){[string]$manifest.apiArchive}else{'moddingapi.v77.windows.zip'}
if ($taskApiArchive -notmatch '^[a-zA-Z0-9][a-zA-Z0-9.-]*\.zip$') { throw 'Invalid API archive filename.' }
$zip = Join-Path $package ('dependencies\'+$taskApiArchive)
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
    if ($manifest.gameProfile -eq 'Unity6' -and @($originalRecord.files | Where-Object {
        $_.name -eq 'Assembly-CSharp.dll' -and $_.installedSha256 -eq $manifest.apiAssemblySha256
    }).Count -ne 1) { throw 'The original backup targets another game/API version. Preserve and archive the previous backup chain before installing this game version.' }
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
$backupMod = Join-Path $backup 'original-HKMP'
$updateInPlace = $originalBackup -and (Test-Path -LiteralPath $modDir -PathType Container)
$oldModMoved = $false
$newModStarted = $false
$apiStarted = $false
$updatedModFiles = [Collections.Generic.List[string]]::new()
$phase = 'Back up existing HKMP'
try {
    if ($updateInPlace) {
        # Existing skins or other local files can be open while their directory
        # is being browsed. Keep HKMP in place and change only package files.
        foreach ($directory in (Split-Path -Parent $modDir),$modDir) {
            if ((Get-Item -LiteralPath $directory -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "Cannot update through a linked mod directory: $directory"
            }
        }
        foreach ($entry in $manifest.files) {
            $destination = Join-Path $modDir $entry.name
            $existing = Get-Item -LiteralPath $destination -Force -ErrorAction SilentlyContinue
            if ($existing -and ($existing.PSIsContainer -or ($existing.Attributes -band [IO.FileAttributes]::ReparsePoint))) {
                throw "Package file conflicts with an existing directory or link: $destination"
            }
        }
        Copy-Item -LiteralPath $modDir -Destination $backupMod -Recurse -Force
    } elseif (Test-Path -LiteralPath $modDir) {
        Move-Item -LiteralPath $modDir -Destination $backupMod
        $oldModMoved = $true
    }
    $phase = 'Install Modding API'
    $apiStarted = $true
    Get-ChildItem -LiteralPath $api -File | Copy-Item -Destination $managed -Force
    $phase = 'Install HKMP payload'
    if ($updateInPlace) {
        foreach ($entry in $manifest.files) {
            $destination = Join-Path $modDir $entry.name
            $updatedModFiles.Add($entry.name)
            Copy-Item -LiteralPath (Join-Path $package "payload\HKMP\$($entry.name)") -Destination $destination -Force
        }
    } else {
        New-Item -ItemType Directory -Path (Join-Path $managed 'Mods') -Force | Out-Null
        $newModStarted = $true
        Copy-Item -LiteralPath (Join-Path $package 'payload\HKMP') -Destination (Join-Path $managed 'Mods') -Recurse -Force
    }
    $phase = 'Verify payload and record installation'
    foreach ($entry in $manifest.files) {
        if ((Get-FileHash -LiteralPath (Join-Path $modDir $entry.name) -Algorithm SHA256).Hash -ne $entry.sha256) { throw "Installed checksum mismatch: $($entry.name)" }
    }
    @{game=$game; version=$manifest.version; displayVersion=$taskDisplayVersion; files=$records; modFiles=$manifest.files; originalBackup=$originalBackup} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $backup 'install-record.json') -Encoding UTF8
    if (!$originalBackup) { Set-Content -LiteralPath $pointerPath -Value $backup -Encoding UTF8 }
    Write-Output "Installed HollowConnect $taskDisplayVersion. Backup: $backup"
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
    foreach ($name in $updatedModFiles) {
        try {
            $destination = Join-Path $modDir $name
            $previous = Join-Path $backupMod $name
            if (Test-Path -LiteralPath $previous -PathType Leaf) {
                if (!(Test-Path -LiteralPath $destination -PathType Leaf) -or
                    (Get-FileHash -LiteralPath $destination).Hash -ne (Get-FileHash -LiteralPath $previous).Hash) {
                    Copy-Item -LiteralPath $previous -Destination $destination -Force
                }
            } elseif (Test-Path -LiteralPath $destination -PathType Leaf) {
                Remove-Item -LiteralPath $destination -Force
            }
        } catch { $rollbackErrors.Add('Restore HKMP/' + $name + ': ' + $_.Exception.Message) }
    }
    if ($apiStarted) {
        foreach ($record in $records) {
            try {
                $destination = Join-Path $managed $record.name
                if ($record.existed) { Copy-Item -LiteralPath (Join-Path $original $record.name) -Destination $destination -Force }
                elseif (Test-Path -LiteralPath $destination) { Remove-Item -LiteralPath $destination }
            } catch { $rollbackErrors.Add('Restore ' + $record.name + ': ' + $_.Exception.Message) }
        }
    }
    if ($oldModMoved) {
        try {
            if (Test-Path -LiteralPath $modDir) { throw 'The partial mod could not be moved aside; original-HKMP remains in the backup.' }
            Move-Item -LiteralPath (Join-Path $backup 'original-HKMP') -Destination $modDir
        } catch { $rollbackErrors.Add('Restore original HKMP: ' + $_.Exception.Message) }
    }
    $recoveryMessage = "Installation failed: [$phase] $($installError.Exception.Message)`r`nBackup: $backup"
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
