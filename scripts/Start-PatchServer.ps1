<#
.SYNOPSIS
  Serves the patch folder over HTTP for the launchers (builds the patch server on first use).

.DESCRIPTION
  Uses patchDir from config\publish.config.json and patchPort from config\launcher.config.json unless
  given here. Leave the window open; Ctrl+C stops it. Players need TCP <port> allowed in this machine's
  firewall and, from outside your network, forwarded by the router.
#>
param(
    [string]$Root = '',
    [int]$Port = 0
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Common.ps1')
if (-not $Root) { $Root = (Read-JsonConfig (Join-Path $ConfigDir 'publish.config.json')).patchDir }
if (-not $Port) { $Port = [int](Get-LauncherConfig).patchPort }
if (-not $Port) { $Port = 8099 }
$exe = Get-PatchServerExe
if (-not (Test-Path $exe)) { Build-PatchServer }
$host.UI.RawUI.WindowTitle = "AAEmu patch server :$Port"
& $exe --root $Root --port $Port --name "$((Get-LauncherConfig).productName) patch server"
