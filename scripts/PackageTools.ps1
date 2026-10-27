# File operations shared by packaging and its isolated workflow checks.
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
function Get-HollowSourceFiles {
    param([Parameter(Mandatory)][string]$Path)
    $taskEntry=Get-Item -LiteralPath $Path
    if (!$taskEntry.PSIsContainer) { $taskEntry; return }
    $taskFolders=[Collections.Generic.Stack[IO.DirectoryInfo]]::new()
    $taskFolders.Push($taskEntry)
    while ($taskFolders.Count) {
        $taskFolder=$taskFolders.Pop()
        foreach ($taskFile in $taskFolder.EnumerateFiles()) {
            if ($taskFile.Name -notlike '*.draft' -and $taskFile.Name -notin 'AGENTS.md','AGENT.md','PROJECT-HANDOFF.md') { $taskFile }
        }
        foreach ($taskChild in $taskFolder.EnumerateDirectories()) {
            if ($taskChild.Name -notin 'bin','obj' -and !($taskChild.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
                $taskFolders.Push($taskChild)
            }
        }
    }
}

function Write-HollowArchive {
    param([Parameter(Mandatory)][string]$Destination,
        [Parameter(Mandatory)][string]$BasePath,
        [Parameter(Mandatory)][IO.FileInfo[]]$Files,
        [IO.Compression.CompressionLevel]$CompressionLevel='Optimal')
    $taskArchiveRoot=[IO.Path]::GetFullPath($BasePath).TrimEnd('\','/')+[IO.Path]::DirectorySeparatorChar
    foreach ($taskFile in $Files) {
        if (!$taskFile.FullName.StartsWith($taskArchiveRoot,[StringComparison]::OrdinalIgnoreCase)) { throw 'Archive input escapes its base directory.' }
    }
    $taskTemporary=$Destination+'.'+[Guid]::NewGuid().ToString('N')+'.tmp'
    try {
        $taskZip=[IO.Compression.ZipFile]::Open($taskTemporary,[IO.Compression.ZipArchiveMode]::Create)
        try {
            foreach ($taskFile in $Files) {
                $taskRelative=$taskFile.FullName.Substring($taskArchiveRoot.Length).Replace('\','/')
                [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($taskZip,$taskFile.FullName,$taskRelative,$CompressionLevel) | Out-Null
            }
        } finally { $taskZip.Dispose() }
        Move-Item -LiteralPath $taskTemporary -Destination $Destination -Force
    } finally { if (Test-Path -LiteralPath $taskTemporary) { Remove-Item -LiteralPath $taskTemporary } }
}

function Update-HollowChecksums {
    param([Parameter(Mandatory)][string]$Directory,
        [Parameter(Mandatory)][string[]]$ChangedArchives, [switch]$Rebuild)
    $taskSumsPath=Join-Path $Directory 'SHA256SUMS.txt'
    $taskSums=@{}
    if (!$Rebuild -and (Test-Path -LiteralPath $taskSumsPath)) {
        foreach ($taskLine in [IO.File]::ReadAllLines($taskSumsPath)) {
            if ($taskLine -match '^([a-fA-F0-9]{64})  (HollowConnect-[a-zA-Z0-9.-]+\.zip)$') {
                $taskSums[$Matches[2]]=$Matches[1].ToLowerInvariant()
            }
        }
    }
    $taskLines=foreach ($taskZip in Get-ChildItem -LiteralPath $Directory -Filter 'HollowConnect-*.zip' -File | Sort-Object Name) {
        if ($Rebuild -or $taskZip.Name -in $ChangedArchives -or !$taskSums.ContainsKey($taskZip.Name)) {
            $taskSums[$taskZip.Name]=(Get-FileHash -LiteralPath $taskZip.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        }
        $taskSums[$taskZip.Name]+'  '+$taskZip.Name
    }
    [IO.File]::WriteAllLines($taskSumsPath,[string[]]$taskLines,[Text.Encoding]::ASCII)
}
