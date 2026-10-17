# One run against the official client (see Agents/GettingStarted.md):
#   checks the one-time setup, stops anything on the dev ports and the patched client, builds Auth, Patch and Gateway,
#   starts all three (logs in logs/live-*.log) and launches Client/Game.patched.exe through the local patching server.
#   -NoClient   keep a running client (it drops to the login screen; log in again)
#   -NoBuild    skip the builds
#   -Walkmaps   also run scripts/walkmaps/walkmaps.py (needs Python + `pip install frida`): captures the walk map of every
#               area the client generates into data/walkability/
param([switch]$NoClient, [switch]$NoBuild, [switch]$Walkmaps)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$out = Join-Path $repo 'logs'
New-Item -ItemType Directory -Force $out | Out-Null

function Need($ok, $message) { if (-not $ok) { throw "setup incomplete: $message (see Agents/GettingStarted.md)" } }
Need (Get-Command dotnet -ErrorAction SilentlyContinue) '.NET SDK not found (dotnet)'
Need (Test-Path (Join-Path $repo 'data\login-keys.json')) 'data\login-keys.json missing: run GameServer.Auth keygen'
Need (Test-Path (Join-Path $repo 'Client\Game.exe')) 'Client\Game.exe missing: copy the client and its PathOfExile.exe'
Need (Test-Path (Join-Path $repo 'Client\Game.patched.exe')) 'Client\Game.patched.exe missing: run GameServer.Auth patch'
Need (Test-Path (Join-Path $repo 'Client\Content.ggpk')) 'Client\Content.ggpk missing: this needs the standalone client layout'

$python = $null
if ($Walkmaps) {
    # A virtual environment in the repository (.venv) wins over the python on PATH.
    $venv = Join-Path $repo '.venv\Scripts\python.exe'
    $python = if (Test-Path $venv) { $venv } else { (Get-Command python -ErrorAction SilentlyContinue).Source }
    if (-not $python) { $python = (Get-Command py -ErrorAction SilentlyContinue).Source }
    Need $python 'Python not found (needed for -Walkmaps)'
    & $python -c 'import frida' 2>$null
    Need ($LASTEXITCODE -eq 0) 'the frida Python package is missing: python -m pip install frida'
}

# Without this the Gateway stops after creating the player and the client never enters the area.
if (-not $env:Game_GATEWAY_ENTER) { $env:Game_GATEWAY_ENTER = '1' }

if (-not $NoClient) { Get-Process -Name 'Game.patched' -ErrorAction SilentlyContinue | Stop-Process -Force }
Get-CimInstance Win32_Process -Filter "Name like 'python%'" -ErrorAction SilentlyContinue |
    Where-Object { $_.CommandLine -like '*walkmaps.py*' } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
# Stop only our own servers (dotnet) on the dev ports; anything else there is reported, not killed.
foreach ($port in 20493, 12995, 8080, 6112) {
    foreach ($c in Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue) {
        $proc = Get-Process -Id $c.OwningProcess -ErrorAction SilentlyContinue
        if ($proc -and $proc.ProcessName -eq 'dotnet') { Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue }
        elseif ($proc) { throw "port $port is used by $($proc.ProcessName) (pid $($proc.Id)); close it or change the port (Agents/GettingStarted.md)" }
    }
}
Start-Sleep -Milliseconds 500
Remove-Item (Join-Path $out 'live-*.log'), (Join-Path $out 'live-*.err'), (Join-Path $out 'walkmaps.*') -ErrorAction SilentlyContinue

if (-not $NoBuild) {
    foreach ($p in 'GameServer.Auth', 'GameServer.Patch', 'GameServer.Gateway') {
        dotnet build (Join-Path $repo "src\$p") -v q -nologo | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "build failed: $p (run: dotnet build src\$p)" }
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
    if ((Get-Date) -gt $deadline) {
        $err = Get-Content (Join-Path $out 'live-patch.err') -Raw -ErrorAction SilentlyContinue
        throw "patch server did not start (logs\live-patch.err):`n$err"
    }
    Start-Sleep -Milliseconds 300
}
Start-Sleep -Seconds 1
foreach ($name in 'auth', 'gateway') {
    $err = Get-Content (Join-Path $out "live-$name.err") -Raw -ErrorAction SilentlyContinue
    if ($err) { throw "$name failed to start (logs\live-$name.err):`n$err" }
}

if ($Walkmaps) {
    Start-Process $python -ArgumentList ('"' + (Join-Path $repo 'scripts\walkmaps\walkmaps.py') + '"') -WorkingDirectory $repo -WindowStyle Hidden `
        -RedirectStandardOutput (Join-Path $out 'walkmaps.log') -RedirectStandardError (Join-Path $out 'walkmaps.err')
    Write-Output 'walk-map capture on (logs\walkmaps.log)'
}

if (-not $NoClient) {
    $client = Join-Path $repo 'Client'
    $p = Start-Process (Join-Path $client 'Game.patched.exe') -ArgumentList '--patching-server', '127.0.0.1:12995' -WorkingDirectory $client -PassThru
    Write-Output "client pid $($p.Id)"
}
Write-Output 'servers up'
