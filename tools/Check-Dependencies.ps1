param(
    [string]$GameRoot = '',
    [string]$ManifestPath = (Join-Path $PSScriptRoot 'DEPENDENCIES.txt'),
    [switch]$CheckOnly
)
$ErrorActionPreference = 'Stop'
$taskManifest = Get-Content -LiteralPath $ManifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
if ([string]::IsNullOrWhiteSpace($GameRoot)) {
    $GameRoot = if ($taskManifest.packageLayoutVersion -in 2, 3, 4, 5, 6, 7) { [IO.Path]::GetDirectoryName($PSScriptRoot) } else { $PSScriptRoot }
}
$taskRoot = [IO.Path]::GetFullPath($GameRoot)
$taskExpected = $taskManifest.newtonsoft
function Test-JsonIdentity([string]$Path) {
    $taskName = [Reflection.AssemblyName]::GetAssemblyName($Path)
    $taskToken = ([BitConverter]::ToString($taskName.GetPublicKeyToken())).Replace('-', '').ToLowerInvariant()
    if ($taskName.Name -ne 'Newtonsoft.Json' -or $taskName.Version.ToString() -ne $taskExpected.version -or $taskToken -ne $taskExpected.token) {
        throw "Incompatible Newtonsoft.Json at $Path. Expected CLR $($taskExpected.version), token $($taskExpected.token). Interop proxies are not CLR dependencies."
    }
}
$taskCandidates = @()
if ($taskManifest.runtime -eq 'Mono-SPT4x') {
    $taskManaged = Join-Path $taskRoot 'EscapeFromTarkov_Data\Managed\Newtonsoft.Json.dll'
    if (Test-Path -LiteralPath $taskManaged -PathType Leaf) { $taskCandidates += $taskManaged }
}
foreach ($taskFolder in @('BepInEx\core', 'BepInEx\plugins')) {
    $taskLocation = Join-Path $taskRoot $taskFolder
    if (Test-Path -LiteralPath $taskLocation -PathType Container) {
        $taskCandidates += @(Get-ChildItem -LiteralPath $taskLocation -Filter Newtonsoft.Json.dll -File -Recurse | ForEach-Object { $_.FullName })
    }
}
foreach ($taskCandidate in $taskCandidates) { Test-JsonIdentity $taskCandidate }
if ($taskCandidates.Count -gt 0) {
    Write-Output ('Newtonsoft.Json is available; existing game/plugin assemblies retained: ' + ($taskCandidates -join ', '))
    exit 0
}
if ($CheckOnly) { throw 'CLR Newtonsoft.Json is missing. Run this script without -CheckOnly to install the bundled fallback.' }
$taskPayloadPath = if ($taskManifest.payloadPath) { $taskManifest.payloadPath } else { 'Dependencies\Newtonsoft.Json\Newtonsoft.Json.dll' }
$taskPayload = [IO.Path]::GetFullPath((Join-Path $taskRoot $taskPayloadPath))
if (!$taskPayload.StartsWith($taskRoot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Fallback path must stay inside GameRoot.' }
if (!(Test-Path -LiteralPath $taskPayload -PathType Leaf)) { throw 'Bundled fallback missing. Extract the complete release ZIP again.' }
Test-JsonIdentity $taskPayload
$taskHasher = [Security.Cryptography.SHA256]::Create()
try { $taskHash = ([BitConverter]::ToString($taskHasher.ComputeHash([IO.File]::ReadAllBytes($taskPayload)))).Replace('-', '').ToLowerInvariant() }
finally { $taskHasher.Dispose() }
if ($taskHash -ne $taskExpected.sha256) { throw 'Bundled fallback checksum mismatch; installation stopped.' }
$taskTargetDirectory = Join-Path $taskRoot 'BepInEx\plugins'
if (!(Test-Path -LiteralPath (Join-Path $taskRoot 'BepInEx') -PathType Container)) { throw 'GameRoot must contain BepInEx.' }
[void][IO.Directory]::CreateDirectory($taskTargetDirectory)
$taskTarget = Join-Path $taskTargetDirectory 'Newtonsoft.Json.dll'
# File.Copy(..., false) also prevents a concurrent install from being overwritten.
[IO.File]::Copy($taskPayload, $taskTarget, $false)
Write-Output ('Installed bundled CLR dependency: ' + $taskTarget + '. Restart the game.')
