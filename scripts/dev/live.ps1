# One live-test cycle against the official client:
#   stops anything on the dev ports and the patched client, builds Auth, Patch and Gateway, starts all three (logs in
#   logs/live-*.log) and launches Client/Game.patched.exe through our patching server.
# Prerequisites (once): data/login-keys.json + Client/Game.patched.exe (GameServer.Auth keygen/patch) and the GGPK
# gateway list pointed at 127.0.0.1:20493 (GameServer.Patch gateway --login 127.0.0.1:20493).
# -NoClient keeps a running client (log in again after the restart).
param([switch]$NoClient, [switch]$NoBuild)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$out = Join-Path $repo 'logs'
New-Item -ItemType Directory -Force $out | Out-Null

if (-not $NoClient) { Get-Process -Name 'Game.patched' -ErrorAction SilentlyContinue | Stop-Process -Force }
foreach ($port in 20493, 12995, 8080, 6112) {
    Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue |
        ForEach-Object { Stop-Process -Id $_.OwningProcess -Force -ErrorAction SilentlyContinue }
}
Start-Sleep -Milliseconds 500

if (-not $NoBuild) {
    foreach ($p in 'GameServer.Auth', 'GameServer.Patch', 'GameServer.Gateway') {
        dotnet build (Join-Path $repo "src\$p") -v q -nologo | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "build failed: $p" }
    }
}

$bin = 'bin\Debug\net10.0'
Start-Process dotnet -ArgumentList "src\GameServer.Patch\$bin\GameServer.Patch.dll" -WorkingDirectory $repo -WindowStyle Hidden `
    -RedirectStandardOutput (Join-Path $out 'live-patch.log') -RedirectStandardError (Join-Path $out 'live-patch.err')
Start-Process dotnet -ArgumentList "src\GameServer.Gateway\$bin\GameServer.Gateway.dll" -WorkingDirectory $repo -WindowStyle Hidden `
    -RedirectStandardOutput (Join-Path $out 'live-gateway.log') -RedirectStandardError (Join-Path $out 'live-gateway.err')
Start-Process dotnet -ArgumentList "src\GameServer.Auth\$bin\GameServer.Auth.dll", 'serve' -WorkingDirectory $repo -WindowStyle Hidden `
    -RedirectStandardOutput (Join-Path $out 'live-auth.log') -RedirectStandardError (Join-Path $out 'live-auth.err')

# The patch server must read the GGPK root hash before the client locks the file.
$deadline = (Get-Date).AddSeconds(30)
while (-not (Select-String -Path (Join-Path $out 'live-patch.log') -Pattern 'patching protocol on' -Quiet -ErrorAction SilentlyContinue)) {
    if ((Get-Date) -gt $deadline) { throw 'patch server did not start' }
    Start-Sleep -Milliseconds 300
}

if (-not $NoClient) {
    $client = Join-Path $repo 'Client'
    $p = Start-Process (Join-Path $client 'Game.patched.exe') -ArgumentList '--patching-server', '127.0.0.1:12995' -WorkingDirectory $client -PassThru
    Write-Output "client pid $($p.Id)"
}
Write-Output 'servers up'
