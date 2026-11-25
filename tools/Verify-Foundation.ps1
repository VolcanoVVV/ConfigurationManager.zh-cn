param([string]$OtherProject = '')
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$manifest = Get-Content -LiteralPath (Join-Path $taskRoot 'Shared/manifest.json') -Raw | ConvertFrom-Json
foreach ($entry in $manifest.files.PSObject.Properties) {
    $path = Join-Path $taskRoot "Shared/$($entry.Name)"
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $entry.Value) { throw "Foundation changed without updating the manifest: $($entry.Name)" }
    if ($OtherProject -and (Get-FileHash -LiteralPath (Join-Path $OtherProject "Shared/$($entry.Name)") -Algorithm SHA256).Hash -ne $entry.Value) {
        throw "Foundation differs between projects: $($entry.Name)"
    }
}
Write-Output 'PASS: portable translation foundation hashes.'
