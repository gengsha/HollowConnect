param(
    [Parameter(Mandatory)][string]$Prefix,
    [Parameter(Mandatory)][string]$PayloadPath,
    [ValidateSet('Dream_01_False_Knight','Dream_02_Mage_Lord','Dream_03_Infected_Knight','Dream_04_White_Defender','Dream_Mighty_Zote')]
    [string[]]$Scenes=@('Dream_01_False_Knight','Dream_02_Mage_Lord','Dream_03_Infected_Knight','Dream_04_White_Defender','Dream_Mighty_Zote'),
    [switch]$DamageCheck,
    [switch]$LifecycleCheck
)
$ErrorActionPreference='Stop'
if(($DamageCheck -or $LifecycleCheck) -and ($Scenes.Count -ne 1 -or $Scenes[0] -ne 'Dream_01_False_Knight')) {
    throw 'Damage and lifecycle checks are scoped to Failed Champion.'
}
# Build Smoke once before calling this runner. Schema observations do not
# certify native entry, all boss phases, victory returns or personal rewards.
$taskKeys=@('HOLLOWCONNECT_DREAM_VARIANT_SCHEMA','HOLLOWCONNECT_DREAM_VARIANT_SCENE','HOLLOWCONNECT_DREAM_VARIANT_DAMAGE','HOLLOWCONNECT_CHAMPION_LIFECYCLE')
$taskOriginal=@{}
foreach($taskKey in $taskKeys){$taskOriginal[$taskKey]=[Environment]::GetEnvironmentVariable($taskKey)}
try {
    $env:HOLLOWCONNECT_DREAM_VARIANT_SCHEMA='1'
    $env:HOLLOWCONNECT_DREAM_VARIANT_DAMAGE=$(if($DamageCheck -or $LifecycleCheck){'1'}else{'0'})
    $env:HOLLOWCONNECT_CHAMPION_LIFECYCLE=$(if($LifecycleCheck){'1'}else{'0'})
    foreach($taskScene in $Scenes) {
        $env:HOLLOWCONNECT_DREAM_VARIANT_SCENE=$taskScene
        $taskSuffix=$taskScene.ToLowerInvariant().Replace('_','-')
        & (Join-Path $PSScriptRoot 'Run-Dream-Warriors.ps1') -Prefix ($Prefix+'-'+$taskSuffix) -PayloadPath $PayloadPath -Cases guest -Scenes $taskScene -Summoner host -GuestHistory 0
    }
} finally {
    foreach($taskKey in $taskKeys){[Environment]::SetEnvironmentVariable($taskKey,$taskOriginal[$taskKey])}
}
