param([Parameter(Mandatory)][string]$Prefix,
    [ValidateSet('story-host','story-guest','absolute-host','absolute-guest')][string[]]$Only=@('story-host','story-guest','absolute-host','absolute-guest'),
    [switch]$NoBuild)
$ErrorActionPreference='Stop'
$taskRepo=Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$taskOriginal=$env:HOLLOWCONNECT_RADIANCE_SPECTATOR
try {
    if (!$NoBuild) { & (Join-Path $taskRepo 'scripts/Build.ps1') -Target Smoke -NoRestore }
    foreach ($taskCase in $Only) {
        $env:HOLLOWCONNECT_RADIANCE_SPECTATOR=if ($taskCase.EndsWith('host')) {'host'} else {'guest'}
        $taskArenaCase=switch ($taskCase) {
            'story-host' {'story-f8-host'}
            'story-guest' {'story-native-guest'}
            'absolute-host' {'statue-native-guest'}
            'absolute-guest' {'statue-f8-host'}
        }
        & (Join-Path $PSScriptRoot 'Run-Radiance-Arena-Suite.ps1') -Prefix ($Prefix+'-'+$taskCase) -Only $taskArenaCase -NoBuild
    }
} finally { $env:HOLLOWCONNECT_RADIANCE_SPECTATOR=$taskOriginal }
