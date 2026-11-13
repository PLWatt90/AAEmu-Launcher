<#
.SYNOPSIS
  Builds aapatch.exe, the launcher and the patch server into out\.

.DESCRIPTION
  out\aapatch\aapatch.exe              self-contained, no .NET needed on player PCs
  out\launcher\<exeName>.exe           self-contained; config\launcher.config.json is embedded
  out\patch-server\aa-patch-server.exe needs the .NET 10 runtime (server machine only)

  Rebuild the launcher after every change to config\launcher.config.json, then run Make-Patch.ps1 so
  players' launchers update themselves.

.PARAMETER Only
  Build just one part: aapatch, launcher or patchserver.
#>
param([ValidateSet('all', 'aapatch', 'launcher', 'patchserver')][string]$Only = 'all')
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Common.ps1')

if (-not (Test-Path (Join-Path $ConfigDir 'launcher.config.json'))) {
    Write-Warning 'config\launcher.config.json is missing: the launcher gets the example settings (127.0.0.1, no signing key).'
} elseif ([string]::IsNullOrWhiteSpace((Get-LauncherConfig).patchPublicKey)) {
    Write-Warning 'patchPublicKey is empty in config\launcher.config.json: this launcher will refuse every HTTP patch server.'
}

if ($Only -in 'all', 'aapatch') { Build-AAPatch }
if ($Only -in 'all', 'launcher') { Build-Launcher }
if ($Only -in 'all', 'patchserver') { Build-PatchServer }
Write-Host "Done. Output in $OutDir"
