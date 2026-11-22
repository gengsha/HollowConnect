param(
    [Parameter(Mandatory)][string[]]$RunPath,
    [string]$Scene = '*',
    [string]$Phase = '*',
    [string]$CsvPath
)
$ErrorActionPreference = 'Stop'
$rows = @(foreach ($path in $RunPath) {
    $dir = Get-Item -LiteralPath $path
    $traces = @(Get-ChildItem -LiteralPath $dir.FullName -File -Filter '*.route-frames.json')
    $expected = @(Get-ChildItem -LiteralPath $dir.FullName -File -Filter '*.pid' | ForEach-Object { $_.BaseName })
    $manifest = Join-Path $dir.FullName 'binaries.json'
    $hashes = @{}
    if (Test-Path -LiteralPath $manifest) {
        foreach ($binary in (Get-Content -LiteralPath $manifest -Raw | ConvertFrom-Json)) {
            $hashes[$binary.name] = $binary.sha256
        }
    }
    foreach ($role in $expected) {
        if (!($traces.Name -contains "$role.route-frames.json")) { Write-Warning "$($dir.Name): missing frame trace for $role" }
    }
    if (!$traces.Count -and !$expected.Count) { Write-Warning "$($dir.Name): no frame traces" }
    foreach ($trace in $traces) {
        $role = $trace.Name -replace '\.route-frames\.json$', ''
        $data = Get-Content -LiteralPath $trace.FullName -Raw | ConvertFrom-Json
        $result = if (Test-Path -LiteralPath (Join-Path $dir.FullName "$role.failed")) { 'failed' }
            elseif (Test-Path -LiteralPath (Join-Path $dir.FullName "$role.done")) { 'done' } else { 'unmarked' }
        foreach ($group in ($data.windows | Where-Object { $_.scene -like $Scene -and $_.phase -like $Phase } | Group-Object scene, phase)) {
            # Pool the original samples across revisits. Percentiles and FPS
            # cannot be averaged across windows with different frame counts.
            $frames = @($group.Group | ForEach-Object { $_.frames } | Where-Object { $_.wallMs -gt 0 } | Sort-Object -Property wallMs)
            $count = $frames.Count
            if (!$count) { continue }
            $slowCount = [int][Math]::Ceiling($count * 0.01)
            $slow = $frames[($count - $slowCount)..($count - 1)] | Measure-Object -Property wallMs -Average
            $average = $frames | Measure-Object -Property wallMs -Average
            $worst = $frames[-1]
            $over33 = @($frames | Where-Object { $_.wallMs -gt (1000 / 30) })
            [pscustomobject][ordered]@{
                Run = $dir.Name; Role = $role; Scene = $group.Group[0].scene; Phase = $group.Group[0].phase
                Samples = $count; AvgFps = [Math]::Round(1000 / $average.Average, 2)
                Low1Fps = [Math]::Round(1000 / $slow.Average, 2)
                P99Ms = [Math]::Round($frames[[int][Math]::Floor(($count - 1) * 0.99)].wallMs, 2)
                MaxMs = [Math]::Round($worst.wallMs, 2); WorstFrame = $worst.index
                Over33Ms = $over33.Count; Over50Ms = @($over33 | Where-Object { $_.wallMs -gt 50 }).Count
                Gen0Collections = ($frames | Measure-Object -Property gc -Sum).Sum
                Over33WithGen0 = @($over33 | Where-Object { $_.gc -gt 0 }).Count
                Result = $result; Mode = $data.mode; Unity = $data.unity; Resolution = $data.resolution
                Rendered = $data.rendered; TargetFps = $data.targetFps; VSync = $data.vSync
                HKMPSha256 = $hashes['HKMP.dll']; ModSha256 = $hashes['HollowConnect.dll']; Trace = $trace.FullName
            }
        }
    }
})
if (!$rows.Count) { throw 'No frame samples match the selected runs, scene and phase.' }
if ($CsvPath) { $rows | Export-Csv -LiteralPath $CsvPath -NoTypeInformation -NoClobber -Encoding UTF8 }
$rows
