# Document selection shared by packaging, verification and isolated workflow checks.
function Get-HollowDocumentationFiles {
    param([Parameter(Mandatory)][string]$Root,
        [Parameter(Mandatory)][string]$Version,
        [ValidateSet('Repository','Player')][string]$Audience='Repository',
        [string]$AuthorizationPath)
    $taskSelection=Import-PowerShellDataFile -LiteralPath (Join-Path $PSScriptRoot 'Documentation.psd1')
    $taskNames=@($taskSelection.Player)+@($taskSelection.Evidence)
    if ($Audience -eq 'Repository') { $taskNames+=@($taskSelection.Developer) }
    $taskDocs=[IO.Path]::GetFullPath((Join-Path $Root 'docs')).TrimEnd('\','/')+[IO.Path]::DirectorySeparatorChar
    if ($AuthorizationPath) {
        $taskAuthorization=[IO.Path]::GetFullPath((Join-Path $Root $AuthorizationPath))
        if (!$taskAuthorization.StartsWith($taskDocs,[StringComparison]::OrdinalIgnoreCase)) { throw 'Release request path escapes docs/.' }
        $taskNames+=$taskAuthorization.Substring($taskDocs.Length)
    } else {
        # The source archive retains this version's release/playtest request records.
        # Player packages pass the selected mode explicitly when a request is required.
        $taskNames+=@(Get-ChildItem -LiteralPath (Join-Path $taskDocs 'evidence') -Filter "*authorization-$Version*.json" -File |
            Where-Object { $_.Name -match ('^(?:release|playtest)-authorization-'+[regex]::Escape($Version)+'(?:-[a-z0-9-]+)?\.json$') } |
            ForEach-Object { 'evidence/'+$_.Name })
    }
    foreach ($taskName in $taskNames) {
        $taskPath=[IO.Path]::GetFullPath((Join-Path $taskDocs $taskName.Replace('{version}',$Version)))
        if (!$taskPath.StartsWith($taskDocs,[StringComparison]::OrdinalIgnoreCase)) { throw 'Documentation path escapes docs/.' }
        if (!(Test-Path -LiteralPath $taskPath -PathType Leaf)) { throw ('Required documentation missing: '+$taskPath) }
        Get-Item -LiteralPath $taskPath
    }
}

function Copy-HollowPackageDocumentation {
    param([Parameter(Mandatory)][string]$Root,
        [Parameter(Mandatory)][string]$Destination,
        [Parameter(Mandatory)][string]$Version,
        [string]$AuthorizationPath)
    # Resolve and validate the complete selection before changing an existing package.
    $taskFiles=@(Get-HollowDocumentationFiles -Root $Root -Version $Version -Audience Player -AuthorizationPath $AuthorizationPath)
    $taskBase=[IO.Path]::GetFullPath($Root).TrimEnd('\','/')+[IO.Path]::DirectorySeparatorChar
    $taskPackage=[IO.Path]::GetFullPath($Destination).TrimEnd('\','/')
    $taskDocs=Join-Path $taskPackage 'docs'
    $taskSourceDocs=Join-Path $taskBase 'docs'
    if (!$taskPackage.StartsWith($taskBase,[StringComparison]::OrdinalIgnoreCase) -or
        $taskPackage.Equals($taskSourceDocs,[StringComparison]::OrdinalIgnoreCase) -or
        $taskPackage.StartsWith($taskSourceDocs+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) {
        throw 'Package documentation destination must be inside the workspace and outside source docs/.'
    }
    foreach ($taskPath in @($taskPackage,$taskDocs)) {
        $taskCurrent=$taskPath
        while ($taskCurrent -and $taskCurrent.StartsWith($taskBase,[StringComparison]::OrdinalIgnoreCase)) {
            if ((Test-Path -LiteralPath $taskCurrent) -and
                ((Get-Item -LiteralPath $taskCurrent -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
                throw 'Package documentation destination cannot contain a directory link.'
            }
            $taskCurrent=Split-Path -Parent $taskCurrent
        }
    }
    $taskParent=Split-Path -Parent $taskPackage
    $taskId=[Guid]::NewGuid().ToString('N')
    $taskStaging=Join-Path $taskParent ('.docs-staging-'+$taskId)
    $taskBackup=Join-Path $taskParent ('.docs-history-'+(Split-Path -Leaf $taskPackage)+'-'+$taskId)
    [IO.Directory]::CreateDirectory($taskStaging) | Out-Null
    foreach ($taskFile in $taskFiles) {
        $taskRelative=$taskFile.FullName.Substring($taskSourceDocs.Length+1)
        $taskTarget=Join-Path $taskStaging $taskRelative
        [IO.Directory]::CreateDirectory((Split-Path -Parent $taskTarget)) | Out-Null
        Copy-Item -LiteralPath $taskFile.FullName -Destination $taskTarget
    }
    # All move targets are resolved children of the workspace, outside source docs/.
    # Preserve previous package documents rather than deleting historical evidence.
    [IO.Directory]::CreateDirectory($taskPackage) | Out-Null
    if (Test-Path -LiteralPath $taskDocs) { Move-Item -LiteralPath $taskDocs -Destination $taskBackup }
    try { Move-Item -LiteralPath $taskStaging -Destination $taskDocs }
    catch {
        if ((Test-Path -LiteralPath $taskBackup) -and !(Test-Path -LiteralPath $taskDocs)) {
            Move-Item -LiteralPath $taskBackup -Destination $taskDocs
        }
        throw
    }
}

function Update-HollowPackageMarkdownLinks {
    param([Parameter(Mandatory)][string]$Root,
        [Parameter(Mandatory)][string]$Package)
    $taskPackageBase=[IO.Path]::GetFullPath($Package).TrimEnd('\','/')+[IO.Path]::DirectorySeparatorChar
    $taskMarkdown=@(Get-Item -LiteralPath (Join-Path $Package 'README.md'))+
        @(Get-ChildItem -LiteralPath (Join-Path $Package 'docs') -Filter '*.md' -File -Recurse)
    foreach ($taskFile in $taskMarkdown) {
        $taskContent=[IO.File]::ReadAllText($taskFile.FullName)
        $taskUpdated=[regex]::Replace($taskContent,'\]\(([^)]+)\)',[Text.RegularExpressions.MatchEvaluator]{
            param($taskMatch)
            $taskTarget=$taskMatch.Groups[1].Value
            if ($taskTarget -match '^(?:[A-Za-z][A-Za-z0-9+.-]*:|#|/)' -or $taskTarget -match '\s') { return $taskMatch.Value }
            $taskParts=$taskTarget -split '#',2
            $taskResolved=[IO.Path]::GetFullPath((Join-Path $taskFile.DirectoryName $taskParts[0]))
            if (!$taskResolved.StartsWith($taskPackageBase,[StringComparison]::OrdinalIgnoreCase)) { throw 'Markdown link escapes package.' }
            if (Test-Path -LiteralPath $taskResolved) { return $taskMatch.Value }
            $taskRelative=$taskResolved.Substring($taskPackageBase.Length).Replace('\','/')
            if (!(Test-Path -LiteralPath (Join-Path $Root $taskRelative))) { throw ('Broken documentation link: '+$taskTarget) }
            $taskEncoded=($taskRelative.Split('/') | ForEach-Object { [Uri]::EscapeDataString($_) }) -join '/'
            $taskAnchor=if ($taskParts.Count -gt 1) { '#'+$taskParts[1] } else { '' }
            return ']('+'https://github.com/gengsha/HollowConnect/blob/master/'+$taskEncoded+$taskAnchor+')'
        })
        if ($taskUpdated -ne $taskContent) { [IO.File]::WriteAllText($taskFile.FullName,$taskUpdated,[Text.UTF8Encoding]::new($false)) }
    }
}
