# Evidence helper: assert that the installer file about to be run (or uploaded) has exactly the bytes
# that were recorded right after the installer build in this job. Appends one line per call to
# installer-hash-log.txt (label, short and full sha256, file name only). No paths or secrets.
param(
    [Parameter(Mandatory)][string]$Path,
    [Parameter(Mandatory)][string]$Label,
    [string]$Kind = 'player'
)
$ErrorActionPreference = 'Stop'
$builtFile = Join-Path $env:RUNNER_TEMP "$Kind-installer-built-sha256.txt"
if (-not (Test-Path $builtFile)) { throw "no recorded build hash for kind '$Kind' ($builtFile)" }
if (-not (Test-Path $Path)) { throw "installer to check is missing: $Path" }
$built = (Get-Content $builtFile -Raw).Trim().ToLowerInvariant()
$now = (Get-FileHash $Path -Algorithm SHA256).Hash.ToLowerInvariant()
$line = "installer-bytes[$Label] kind=$Kind file=$([IO.Path]::GetFileName($Path)) sha256=$now built=$built match=$($now -eq $built)"
Write-Host $line
Add-Content -Path (Join-Path $env:RUNNER_TEMP 'installer-hash-log.txt') -Value $line
if ($now -ne $built) { throw "installer bytes at '$Label' differ from the bytes recorded after the build" }
