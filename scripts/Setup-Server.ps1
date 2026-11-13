<#
.SYNOPSIS
  First-time setup on the server machine: config files, patch signing key, first build.

.DESCRIPTION
  1. Copies the config\*.example files to their real names (existing files are kept).
  2. Builds aapatch.exe and creates the patch signing key with "aapatch keygen" unless the key exists.
  3. Writes the key's public half into patchPublicKey of config\launcher.config.json.
  4. Builds everything (scripts\Build.ps1).
  Safe to run again: it never replaces an existing key or config file.

.PARAMETER SigningKey
  Where the private key lives. Default %LOCALAPPDATA%\AAEmu-Launcher\patch-signing-key.pem.
  Keep it out of the patch folder and out of git, and back it up.

.PARAMETER Server
  Optional: written into defaultServer of a freshly copied launcher.config.json.
#>
param(
    [string]$SigningKey = '',
    [string]$Server = ''
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Common.ps1')
if ([string]::IsNullOrWhiteSpace($SigningKey)) { $SigningKey = $DefaultKey }

# 1. config files
foreach ($pair in @(
        @('launcher.config.example.json', 'launcher.config.json'),
        @('publish.config.example.json', 'publish.config.json'),
        @('patch-entries.example.txt', 'patch-entries.txt'))) {
    $target = Join-Path $ConfigDir $pair[1]
    if (Test-Path $target) {
        Write-Host "config\$($pair[1]) exists, kept."
    } else {
        Copy-Item (Join-Path $ConfigDir $pair[0]) $target
        Write-Host "config\$($pair[1]) created from the example."
        if ($pair[1] -eq 'launcher.config.json' -and $Server) {
            $text = [IO.File]::ReadAllText($target)
            $text = [regex]::Replace($text, '("defaultServer"\s*:\s*)"[^"]*"', ('$1"' + $Server + '"'))
            [IO.File]::WriteAllText($target, $text, (New-Object Text.UTF8Encoding $false))
        }
    }
}

# 2. signing key
Build-AAPatch
$aapatch = Get-AAPatchExe
if (Test-Path $SigningKey) {
    Write-Host "Signing key $SigningKey exists, kept."
} else {
    $output = & $aapatch keygen $SigningKey
    if ($LASTEXITCODE -ne 0) { throw "aapatch keygen failed with exit code $LASTEXITCODE" }
    $output | ForEach-Object { Write-Host $_ }
    Write-Host ''
    Write-Host "BACK UP $SigningKey now. Without it you cannot publish patches to existing launchers." -ForegroundColor Yellow
}
$publicKey = (& $aapatch pubkey $SigningKey)
if ($LASTEXITCODE -ne 0) { throw "aapatch pubkey failed with exit code $LASTEXITCODE" }
$publicKey = "$publicKey".Trim()
if ($publicKey -notmatch '^[A-Za-z0-9+/=]{80,}$') { throw "could not read the public key (got '$publicKey')" }

# 3. public key into the launcher config
$launcherConfig = Join-Path $ConfigDir 'launcher.config.json'
$text = [IO.File]::ReadAllText($launcherConfig)
if ($text -notmatch '"patchPublicKey"\s*:') { throw "config\launcher.config.json has no patchPublicKey line; add one: `"patchPublicKey`": `"`"" }
$updated = [regex]::Replace($text, '("patchPublicKey"\s*:\s*)"[^"]*"', ('$1"' + $publicKey + '"'))
if ($updated -ne $text) {
    [IO.File]::WriteAllText($launcherConfig, $updated, (New-Object Text.UTF8Encoding $false))
    Write-Host 'patchPublicKey written into config\launcher.config.json.'
} else {
    Write-Host 'patchPublicKey in config\launcher.config.json already matches the key.'
}

# 4. build
& (Join-Path $PSScriptRoot 'Build.ps1')

Write-Host ''
Write-Host 'Next steps (see README.md, "Server setup"):'
Write-Host '  - edit config\launcher.config.json (defaultServer, authMode, patchUrl) and run scripts\Build.ps1 again'
Write-Host '  - edit config\publish.config.json and config\patch-entries.txt'
Write-Host '  - scripts\Make-Patch.ps1, then scripts\Start-PatchServer.bat'
