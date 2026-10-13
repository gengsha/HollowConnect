param([switch]$List)

function Test-HollowKnightDirectory {
    param([string]$Path)
    if ([string]::IsNullOrWhiteSpace($Path)) { return $false }
    return (Test-Path -LiteralPath (Join-Path $Path 'hollow_knight.exe') -PathType Leaf) -and
        (Test-Path -LiteralPath (Join-Path $Path 'hollow_knight_Data\Managed\Assembly-CSharp.dll') -PathType Leaf)
}

function Get-HollowKnightInstallations {
    param([string[]]$SteamRoots)
    $roots = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    if ($PSBoundParameters.ContainsKey('SteamRoots')) {
        foreach ($candidate in $SteamRoots) { if ($candidate) { [void]$roots.Add($candidate) } }
    } else {
        foreach ($registry in @(
            @{Path='HKCU:\Software\Valve\Steam'; Name='SteamPath'},
            @{Path='HKLM:\SOFTWARE\WOW6432Node\Valve\Steam'; Name='InstallPath'},
            @{Path='HKLM:\SOFTWARE\Valve\Steam'; Name='InstallPath'})) {
            $value = Get-ItemProperty -LiteralPath $registry.Path -Name $registry.Name -ErrorAction SilentlyContinue
            if ($value -and $value.($registry.Name)) { [void]$roots.Add($value.($registry.Name)) }
        }
        foreach ($programs in @(${env:ProgramFiles(x86)}, $env:ProgramFiles)) {
            if ($programs) { [void]$roots.Add((Join-Path $programs 'Steam')) }
        }
        foreach ($process in @(Get-Process -Name steam -ErrorAction SilentlyContinue)) {
            try { if ($process.Path) { [void]$roots.Add((Split-Path -Parent $process.Path)) } } catch { }
        }
    }
    $libraries = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($steamRoot in $roots) {
        [void]$libraries.Add($steamRoot)
        foreach ($vdf in @((Join-Path $steamRoot 'steamapps\libraryfolders.vdf'), (Join-Path $steamRoot 'config\libraryfolders.vdf'))) {
            if (!(Test-Path -LiteralPath $vdf -PathType Leaf)) { continue }
            $text = Get-Content -LiteralPath $vdf -Raw -Encoding UTF8
            # Both current nested "path" entries and the legacy numbered paths.
            foreach ($match in [regex]::Matches($text, '"(?:path|\d+)"\s*"((?:\\.|[^"\\])*)"')) {
                $library = $match.Groups[1].Value.Replace('\\','\').Replace('\"','"')
                if ([IO.Path]::IsPathRooted($library) -and (Test-Path -LiteralPath $library -PathType Container)) { [void]$libraries.Add($library) }
            }
        }
    }
    $games = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($library in $libraries) {
        $common = Join-Path $library 'steamapps\common'
        $candidates = @((Join-Path $common 'Hollow Knight'))
        $appManifest = Join-Path $library 'steamapps\appmanifest_367520.acf'
        if (Test-Path -LiteralPath $appManifest -PathType Leaf) {
            $match = [regex]::Match((Get-Content -LiteralPath $appManifest -Raw -Encoding UTF8), '"installdir"\s*"([^"\r\n]+)"')
            if ($match.Success) {
                $directory = $match.Groups[1].Value
                if ($directory -ne '.' -and $directory -ne '..' -and $directory -notmatch '[/\\:]') { $candidates += Join-Path $common $directory }
            }
        }
        foreach ($candidate in $candidates) {
            if (Test-HollowKnightDirectory $candidate) { [void]$games.Add((Resolve-Path -LiteralPath $candidate).ProviderPath) }
        }
    }
    $games | Sort-Object
}

function Resolve-HollowKnightDirectory {
    param([string]$Path)
    if ([string]::IsNullOrWhiteSpace($Path)) {
        $games = @(Get-HollowKnightInstallations)
        if ($games.Count -ne 1) { throw 'Game location is ambiguous or unavailable. Use the installer to choose hollow_knight.exe, or supply -GamePath.' }
        $Path = $games[0]
    }
    if (Test-Path -LiteralPath $Path -PathType Leaf) {
        if ([IO.Path]::GetFileName($Path) -ine 'hollow_knight.exe') { throw 'Select hollow_knight.exe or its containing directory.' }
        $Path = Split-Path -Parent (Resolve-Path -LiteralPath $Path).ProviderPath
    }
    if (!(Test-HollowKnightDirectory $Path)) { throw 'This directory does not contain Hollow Knight and its Managed assemblies.' }
    (Resolve-Path -LiteralPath $Path).ProviderPath.TrimEnd('\')
}

function Assert-HollowKnightStopped {
    param([string]$Path)
    $executable = Join-Path $Path 'hollow_knight.exe'
    foreach ($process in @(Get-Process -Name hollow_knight -ErrorAction SilentlyContinue)) {
        $runningPath = $null
        try { $runningPath = $process.Path } catch { }
        if (!$runningPath -or $runningPath -ieq $executable) { throw 'Close Hollow Knight in the selected directory before installing or restoring.' }
    }
}

if ($List) { Get-HollowKnightInstallations }
