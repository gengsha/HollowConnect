param([string]$GamePath)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'GamePath.ps1')
$GamePath = Resolve-HollowKnightDirectory $GamePath
$taskManaged = Join-Path $GamePath 'hollow_knight_Data/Managed'
$taskGameHash = (Get-FileHash -LiteralPath (Join-Path $taskManaged 'Assembly-CSharp.dll')).Hash
if ($taskGameHash -ne 'E9048EF6A633970F735E01EC166D3959F610EAEA7A88D827D48D67B1E5FB87BD') {
    throw 'Unity6 preparation requires the original Windows 1.5.12620 Assembly-CSharp.dll; references were not changed.'
}
$taskCommit = '468f579f25e63da2188f39fe5ece15c25d2854d7'
$taskArchiveHash = 'FDFF2B2E13E8C8C679D3E19FD0731A79883BB11DEB0F7E4E8E615A5D6FE10E07'
$taskCache = Join-Path $taskRoot ('vendor/unity6-api/' + $taskCommit)
# Keep .NET Framework patcher paths below Windows' legacy path limit.
$taskSource = Join-Path $taskCache 'src'
$taskPayload = Join-Path $taskCache 'payload'
$taskRecordPath = Join-Path $taskCache 'build.json'
New-Item -ItemType Directory -Force -Path $taskCache | Out-Null
# Steam updates can leave old loader files beside the new vanilla game. Never
# let those replace the new patcher's dependencies during its post-build copy.
$taskInputs = @(Get-ChildItem -LiteralPath $taskManaged -Filter '*.dll' -File | Where-Object { $_.Name -notmatch '^(MonoMod\.|Mono\.Cecil|MMHOOK_)' })
$taskInputHashes = @($taskInputs | Sort-Object Name | ForEach-Object { [ordered]@{name=$_.Name;sha256=(Get-FileHash -LiteralPath $_.FullName).Hash} })
if (Test-Path -LiteralPath $taskRecordPath) {
    $taskRecord = Get-Content -LiteralPath $taskRecordPath -Raw | ConvertFrom-Json
    if ($taskRecord.commit -ne $taskCommit -or ($taskRecord.inputs | ConvertTo-Json -Compress) -ne ($taskInputHashes | ConvertTo-Json -Compress)) {
        throw 'Cached Unity6 API inputs differ. Preserve this cache and prepare a matching API before refreshing.'
    }
    foreach ($taskFile in $taskRecord.files) {
        if ((Get-FileHash -LiteralPath (Join-Path $taskPayload $taskFile.name)).Hash -ne $taskFile.sha256) { throw 'Cached Unity6 API checksum mismatch.' }
    }
} else {
    $taskZip = Join-Path $taskCache 'source.zip'
    if (!(Test-Path -LiteralPath $taskZip)) { Invoke-WebRequest "https://codeload.github.com/SFGrenade/api/zip/$taskCommit" -OutFile $taskZip -UserAgent 'HollowConnect-build' }
    if ((Get-FileHash -LiteralPath $taskZip).Hash -ne $taskArchiveHash) { throw 'Unity6 API source checksum mismatch.' }
    if (Test-Path -LiteralPath $taskSource) { throw 'Incomplete Unity6 source cache; preserve it before preparing a fresh cache.' }
    Expand-Archive -LiteralPath $taskZip -DestinationPath (Join-Path $taskCache 'source')
    Move-Item -LiteralPath (Join-Path $taskCache ('source/api-' + $taskCommit)) -Destination $taskSource
    $taskVanilla = Join-Path $taskSource 'Vanilla'
    New-Item -ItemType Directory -Path $taskVanilla | Out-Null
    $taskInputs | Copy-Item -Destination $taskVanilla
    Push-Location $taskSource
    try {
        $taskOptions = @('-c','Release','--runtime','win-x64','-p:RuntimeIdentifiers=win-x64%3Bwin-x86','-p:Mono=','-p:CopyDir=','--nologo')
        foreach ($taskTool in 'PrePatcher','PostPatcher') {
            & dotnet build "$taskTool/$taskTool.csproj" @taskOptions
            if ($LASTEXITCODE -ne 0) { throw ('Unity6 API tool build failed: ' + $taskTool) }
            New-Item -ItemType Directory -Force -Path "$taskTool/Output" | Out-Null
            Get-ChildItem -LiteralPath "$taskTool/bin/Release/net472/win-x64" -File | Copy-Item -Destination "$taskTool/Output" -Force
        }
        & dotnet build 'Assembly-CSharp/Assembly-CSharp.csproj' @taskOptions
        if ($LASTEXITCODE -ne 0) { throw 'Unity6 API build failed.' }
    } finally { Pop-Location }
    New-Item -ItemType Directory -Path $taskPayload | Out-Null
    Get-ChildItem -LiteralPath (Join-Path $taskSource 'OutputFinal') -File | Copy-Item -Destination $taskPayload
    $taskRecord = [ordered]@{commit=$taskCommit;sourceSha256=$taskArchiveHash;inputs=$taskInputHashes;files=@(
        Get-ChildItem -LiteralPath $taskPayload -File | Sort-Object Name | ForEach-Object { @{name=$_.Name;sha256=(Get-FileHash -LiteralPath $_.FullName).Hash} }
    )}
    $taskRecord | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $taskRecordPath -Encoding UTF8
}
$taskReferences = Join-Path $taskRoot 'vendor/references-unity6'
if (Test-Path -LiteralPath $taskReferences) {
    $taskBackup = $taskReferences + '.previous-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmssfff')
    Move-Item -LiteralPath $taskReferences -Destination $taskBackup
}
New-Item -ItemType Directory -Path $taskReferences | Out-Null
$taskInputs | Copy-Item -Destination $taskReferences
Get-ChildItem -LiteralPath $taskPayload -File | Copy-Item -Destination $taskReferences -Force
Copy-Item -LiteralPath $taskRecordPath -Destination (Join-Path $taskReferences 'api-build.json')
Write-Output ('Prepared Windows 1.5.12620 / API #164 ' + $taskCommit + ': ' + $taskReferences)
