#requires -RunAsAdministrator
<#
.SYNOPSIS
  Redirect the Path of Exile client to the local private server.

.DESCRIPTION
  Two things are needed so the client talks to us instead of GGG:
    1. DNS: patch.poecdn.com -> 127.0.0.1 (so our Patch server serves gateway_list.txt,
       which in turn points the login server at 127.0.0.1:20484).
    2. Trust: a local TLS certificate for patch.poecdn.com and the login hosts,
       trusted by the machine so libcurl accepts it.

  This script manages both. It does NOT modify the game binaries.

.PARAMETER Action
  install or remove.

.PARAMETER LoginHosts
  Additional login hosts to trust/redirect (defaults to the ams/eu/dal/us hosts).

.EXAMPLE
  ./setup-redirect.ps1 -Action install
  ./setup-redirect.ps1 -Action remove
#>
param(
    [ValidateSet('install', 'remove')]
    [string]$Action = 'install',
    [string]$PfxPassword = 'poe',
    [string[]]$LoginHosts = @('ams.login.pathofexile.com', 'dal.login.pathofexile.com', 'eu.login.pathofexile.com', 'us.login.pathofexile.com')
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$serverDir = Join-Path $repoRoot 'server'
$pfxPath = Join-Path $serverDir 'poe-dev.pfx'
$hostsPath = Join-Path $env:SystemRoot 'System32\drivers\etc\hosts'
$marker = '# PoEServer'

function Test-Admin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    (New-Object Security.Principal.WindowsPrincipal($id)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

if (-not (Test-Admin)) {
    throw 'This script must run elevated (it edits the hosts file and the machine trust store).'
}

$redirectHosts = @('patch.poecdn.com') + $LoginHosts

if ($Action -eq 'remove') {
    $lines = Get-Content $hostsPath | Where-Object { $_ -notmatch [regex]::Escape($marker) }
    Set-Content -Path $hostsPath -Value $lines -Encoding ASCII
    Get-ChildItem Cert:\LocalMachine\Root | Where-Object { $_.Subject -like '*PoEServer Dev*' -or $_.FriendlyName -eq 'PoEServer Dev' } | Remove-Item -Force
    Write-Host 'Removed hosts entries and trusted PoEServer Dev certificate.' -ForegroundColor Green
    return
}

# 1. Certificate (create + trust).
if (-not (Test-Path $pfxPath)) {
    New-Item -ItemType Directory -Force -Path $serverDir | Out-Null
    $dns = @('127.0.0.1', 'localhost', 'patch.poecdn.com') + $LoginHosts
    $cert = New-SelfSignedCertificate -DnsName $dns -CertStoreLocation 'Cert:\LocalMachine\My' -FriendlyName 'PoEServer Dev' -NotAfter (Get-Date).AddYears(3)
    $pwd = ConvertTo-SecureString -String $PfxPassword -Force -AsPlainText
    Export-PfxCertificate -Cert $cert -FilePath $pfxPath -Password $pwd | Out-Null
    Write-Host "Created $pfxPath" -ForegroundColor Green
} else {
    $pwd = ConvertTo-SecureString -String $PfxPassword -Force -AsPlainText
    $cert = Import-PfxCertificate -FilePath $pfxPath -Password $pwd -CertStoreLocation 'Cert:\LocalMachine\My'
    Write-Host "Imported $pfxPath" -ForegroundColor Green
}

# Trust it (root store) so libcurl accepts our TLS.
$trusted = Get-ChildItem Cert:\LocalMachine\Root | Where-Object { $_.Thumbprint -eq $cert.Thumbprint }
if (-not $trusted) {
    $tmp = New-TemporaryFile
    Export-Certificate -Cert $cert -FilePath $tmp.FullName | Out-Null
    Import-Certificate -FilePath $tmp.FullName -CertStoreLocation 'Cert:\LocalMachine\Root' | Out-Null
    Remove-Item $tmp.FullName -Force
    Write-Host 'Trusted certificate in LocalMachine\Root.' -ForegroundColor Green
}

# 2. Hosts entries.
$lines = Get-Content $hostsPath
$lines = $lines | Where-Object { $_ -notmatch [regex]::Escape($marker) }
$newLines = @()
foreach ($h in $redirectHosts) { $newLines += "127.0.0.1`t$h`t$marker" }
Set-Content -Path $hostsPath -Value ($lines + $newLines) -Encoding ASCII
Write-Host 'Hosts entries:' -ForegroundColor Green
$newLines | ForEach-Object { Write-Host "  $_" }

Write-Host @"

Done. Now run the servers (servers/run-all.ps1) and launch the client.
If the client still refuses the connection, TLS certificate pinning is in play and
a binary patch will be required (see docs/re/004-redirect-and-servers.md).
"@ -ForegroundColor Cyan
