param(
    [string]$Configuration = 'Release',
    [string]$AssemblyPath = '',
    [string]$GamePath = ''
)

$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
if (!$GamePath) { $GamePath = Join-Path $taskRoot '..\..\EFToffline - 4.1debug' }
$taskManaged = Join-Path $GamePath 'EscapeFromTarkov_Data\Managed'
[void][Reflection.Assembly]::LoadFrom((Join-Path $taskManaged 'UnityEngine.CoreModule.dll'))
if (!$AssemblyPath) { $AssemblyPath = Join-Path $taskRoot "bin\$Configuration\ConfigurationManager.zh-cn.dll" }
$taskAssembly = [Reflection.Assembly]::LoadFrom($AssemblyPath)
$taskLayout = $taskAssembly.GetType('ConfigurationManagerZhCn.TranslationDrawerLayout', $true)
$taskCalculate = $taskLayout.GetMethod('Calculate', [Reflection.BindingFlags]'Static,NonPublic')

function Get-DrawerRect([single]$X, [single]$Y, [single]$Width, [single]$Height, [single]$ScreenWidth, [single]$ScreenHeight) {
    $taskRect = [UnityEngine.Rect]::new($X, $Y, $Width, $Height)
    return $taskCalculate.Invoke($null, @($taskRect, $ScreenWidth, $ScreenHeight))
}

function Assert-Equal($Actual, $Expected, [string]$Message) {
    if ([Math]::Abs($Actual - $Expected) -gt 0.01) { throw "$Message : expected $Expected, got $Actual" }
}

$taskRight = Get-DrawerRect 635 50 650 980 1920 1080
Assert-Equal $taskRight.x 1293 'Dock right without moving the manager'
Assert-Equal $taskRight.y 50 'Align with the manager top'
Assert-Equal $taskRight.height 980 'Match manager height'
$taskLeft = Get-DrawerRect 1450 50 450 980 1920 1080
Assert-Equal $taskLeft.x 1022 'Dock left when only the left side has room'
$taskOverlay = Get-DrawerRect 358 50 650 668 1366 768
Assert-Equal $taskOverlay.x 588 'Overlay the right edge when neither side has room'
$taskSmall = Get-DrawerRect 0 0 640 480 640 480
Assert-Equal $taskSmall.height 464 'Fit short screens with a margin'
$taskNarrow = Get-DrawerRect 0 0 360 640 360 640
Assert-Equal $taskNarrow.width 344 'Fit narrow screens'

# Exercise screen edges, moved windows, small screens and ultrawide resolutions.
$taskCases = 0
foreach ($taskScreen in @(@(640,480), @(800,600), @(1280,720), @(1366,768), @(1920,1080), @(2560,1440), @(3840,2160))) {
    foreach ($taskX in @(-900, 0, 50, 350, 1000, 3500)) {
        foreach ($taskY in @(-500, 0, 50, 500, 2200)) {
            $taskResult = Get-DrawerRect $taskX $taskY 650 980 $taskScreen[0] $taskScreen[1]
            if ($taskResult.x -lt 8 -or $taskResult.y -lt 8 -or
                $taskResult.xMax -gt ($taskScreen[0] - 8) -or $taskResult.yMax -gt ($taskScreen[1] - 8)) {
                throw "Drawer outside screen at $taskX, $taskY on $taskScreen"
            }
            if ($taskResult.width -le 0 -or $taskResult.height -le 0) { throw 'Non-positive drawer size' }
            $taskCases++
        }
    }
}
Write-Output "PASS: 7 placement assertions and $taskCases screen-boundary cases (compiled assembly)."
