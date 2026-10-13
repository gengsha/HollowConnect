param([string]$GamePath, [string]$BackupPath)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'GamePath.ps1')
$game = Resolve-HollowKnightDirectory $GamePath
Assert-HollowKnightStopped $game
if (!$BackupPath) { $BackupPath = (Get-Content -LiteralPath (Join-Path $game '.HollowConnectBackups\latest.txt') -Raw).Trim() }
$record = Get-Content -LiteralPath (Join-Path $BackupPath 'install-record.json') -Raw | ConvertFrom-Json
if ($record.game -ine $game) { throw 'Backup belongs to another game directory.' }
$managed = Join-Path $game 'hollow_knight_Data\Managed'
$modDir = Join-Path $managed 'Mods\HKMP'
if (Test-Path -LiteralPath $modDir) {
    $resolved = (Resolve-Path -LiteralPath $modDir).Path
    if (!$resolved.StartsWith($managed + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid mod directory.' }
    # Keep post-install edits recoverable as well.
    Move-Item -LiteralPath $modDir -Destination (Join-Path $BackupPath ('removed-HKMP-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff')))
}
foreach ($file in $record.files) {
    if ($file.name -notmatch '^[\w.-]+$') { throw 'Invalid backup filename.' }
    $destination = Join-Path $managed $file.name
    $original = Join-Path $BackupPath "original-managed\$($file.name)"
    if (Test-Path -LiteralPath $destination) {
        if ((Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -ne $file.installedSha256) {
            Copy-Item -LiteralPath $destination -Destination (Join-Path $BackupPath ('post-install-' + $file.name)) -Force
        }
    }
    if ($file.existed) { Copy-Item -LiteralPath $original -Destination $destination -Force }
    elseif (Test-Path -LiteralPath $destination) { Remove-Item -LiteralPath $destination }
}
if (Test-Path -LiteralPath (Join-Path $BackupPath 'original-HKMP')) { Copy-Item -LiteralPath (Join-Path $BackupPath 'original-HKMP') -Destination $modDir -Recurse }
Write-Output "Restored pre-install game assemblies. Save backups remain in $BackupPath\saves; saves were not rolled back."
