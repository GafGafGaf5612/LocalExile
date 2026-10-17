# Starts all PoEServer components.
#   Patch   : https://0.0.0.0:443   (impersonates patch.poecdn.com)  - needs admin for port 443
#   Auth    : https://0.0.0.0:20484 (impersonates *.login.pathofexile.com)
#   Gateway : tcp     0.0.0.0:6112  (game/instance server)
#
# Use -NoTls to bind HTTP instead (for local testing without the dev cert).
param([switch]$NoTls)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$pfx = Join-Path $repo 'server\poe-dev.pfx'

if (-not $NoTls) {
    if (-not (Test-Path $pfx)) { throw "Missing $pfx. Run tools/redirect/setup-redirect.ps1 -Action install first." }
    $env:POE_TLS_PFX = $pfx
    $env:POE_TLS_PFX_PASSWORD = 'poe'
} else {
    Remove-Item Env:POE_TLS_PFX -ErrorAction SilentlyContinue
    $env:POE_PATCH_URL = 'http://0.0.0.0:8080'
    $env:POE_AUTH_URL = 'http://0.0.0.0:20484'
}

$cfg = 'Debug'
$procs = @()
$procs += Start-Process dotnet -ArgumentList "src/PoEServer.Patch/bin/$cfg/net10.0/PoEServer.Patch.dll" -PassThru -WorkingDirectory $repo
$procs += Start-Process dotnet -ArgumentList "src/PoEServer.Auth/bin/$cfg/net10.0/PoEServer.Auth.dll" -PassThru -WorkingDirectory $repo
$procs += Start-Process dotnet -ArgumentList "src/PoEServer.Gateway/bin/$cfg/net10.0/PoEServer.Gateway.dll" -PassThru -WorkingDirectory $repo

Write-Host "Started Patch/Auth/Gateway (PIDs: $($procs.Id -join ', ')). Ctrl+C to stop."
try {
    Wait-Process -Id $procs.Id
} finally {
    $procs | ForEach-Object { Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue }
}
