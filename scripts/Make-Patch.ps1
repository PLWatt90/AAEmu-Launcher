<#
.SYNOPSIS
  Rebuilds the player patch folder from the server machine's game_pak.

.DESCRIPTION
  Exports the listed pak entries into the patch folder, writes manifest.txt with a new patch id,
  copies aapatch.exe and the launcher next to them, writes .gz copies of large files, index.txt
  ("path|size|sha256|gzSize|modifiedUtc") and index.txt.sig (signed with your private key).
  Run it after every change to the pak (and after rebuilding the launcher). Players get the new
  patch the next time they click Play; a newer launcher replaces itself ("Restart to update").

  Settings come from config\publish.config.json; any parameter given here wins.
  The game client using the pak must be closed (it locks game_pak).

.PARAMETER Config
  Publish settings file. Default config\publish.config.json.
.PARAMETER Pak
  The game_pak holding your client changes.
.PARAMETER PatchDir
  The folder the patch server serves.
.PARAMETER SigningKey
  Private key from "aapatch keygen". Default %LOCALAPPDATA%\AAEmu-Launcher\patch-signing-key.pem.
.PARAMETER Entries
  Inner pak paths to publish; default: the lines of the config's entriesFile.
.PARAMETER NoBuild
  Use the tools already in out\ instead of building missing ones.
#>
param(
    [string]$Config = '',
    [string]$Pak = '',
    [string]$PatchDir = '',
    [string]$SigningKey = '',
    [string[]]$Entries = @(),
    [switch]$NoBuild
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Common.ps1')

if (-not $Config) { $Config = Join-Path $ConfigDir 'publish.config.json' }
$settings = Read-JsonConfig $Config
$configFolder = Split-Path -Parent (Resolve-Path $Config).Path
if (-not $Pak) { $Pak = $settings.pak }
if (-not $PatchDir) { $PatchDir = $settings.patchDir }
if (-not $SigningKey) { $SigningKey = $settings.signingKey }
if (-not $SigningKey) { $SigningKey = $DefaultKey }
if ($Entries.Count -eq 0) {
    $entriesFile = $settings.entriesFile
    if (-not $entriesFile) { $entriesFile = 'patch-entries.txt' }
    if (-not [IO.Path]::IsPathRooted($entriesFile)) { $entriesFile = Join-Path $configFolder $entriesFile }
    if (-not (Test-Path $entriesFile)) { throw "entries file $entriesFile not found" }
    $Entries = @(Get-Content $entriesFile -Encoding UTF8 | ForEach-Object { $_.Trim() } | Where-Object { $_ -and -not $_.StartsWith('#') })
}
if (-not $Pak -or -not (Test-Path $Pak)) { throw "game_pak '$Pak' not found (set pak in $Config)" }
if (-not $PatchDir) { throw "patchDir is not set in $Config" }
if ($Entries.Count -eq 0) { throw 'no pak entries to publish (list them in config\patch-entries.txt)' }

New-Item -ItemType Directory -Force $PatchDir | Out-Null
$PatchDir = (Resolve-Path $PatchDir).Path
if (-not (Test-Path $SigningKey)) { throw "patch signing key $SigningKey not found (run scripts\Setup-Server.ps1 once)" }
if ((Resolve-Path $SigningKey).Path.StartsWith($PatchDir.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw "the signing key must not live inside the published folder $PatchDir"
}

$tool = Get-AAPatchExe
$launcher = Get-LauncherExe
if (-not (Test-Path $tool)) { if ($NoBuild) { throw "$tool not built" } else { Build-AAPatch } }
if (-not (Test-Path $launcher)) { if ($NoBuild) { throw "$launcher not built" } else { Build-Launcher } }
$launcherName = Split-Path -Leaf $launcher

& $tool manifest $Pak $PatchDir @Entries
if ($LASTEXITCODE -ne 0) { throw "aapatch manifest failed with exit code $LASTEXITCODE" }
Copy-Item $tool (Join-Path $PatchDir 'aapatch.exe') -Force
Copy-Item $launcher (Join-Path $PatchDir $launcherName) -Force
foreach ($extra in @($settings.extraFiles)) {
    if (-not $extra) { continue }
    $source = if ([IO.Path]::IsPathRooted($extra)) { $extra } else { Join-Path $configFolder $extra }
    Copy-Item $source $PatchDir -Force
    Write-Host "copied $source"
}
foreach ($client in @($settings.copyToClients)) {
    if (-not $client) { continue }
    Copy-Item $tool (Join-Path $client 'aapatch.exe') -Force
    Copy-Item $launcher (Join-Path $client $launcherName) -Force
    Write-Host "updated aapatch.exe and $launcherName in $client"
}

# index.txt (manifest.txt included), .gz copies of files >= 1 MB, and the signature. index.txt and its
# signature are written last: launchers treat a manifest.txt that disagrees with them as "a patch is
# being published right now" and ask the player to try again in a minute.
$utf8 = New-Object System.Text.UTF8Encoding $false
$lines = New-Object System.Collections.Generic.List[string]
$lines.Add('# patch server index: path|size|sha256|gzSize|modifiedUtc (signed: index.txt.sig)')
$published = @($Entries | ForEach-Object { ($_ -replace '\\', '/').TrimStart('/') }) + @('manifest.txt', 'aapatch.exe', $launcherName)
foreach ($rel in $published) {
    $file = Get-Item (Join-Path $PatchDir ($rel -replace '/', '\'))
    $sha = (Get-FileHash $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    $gzPath = $file.FullName + '.gz'
    $gzSize = 0
    if ($file.Length -ge 1MB) {
        $tmp = $gzPath + '.tmp'
        $in = [System.IO.File]::OpenRead($file.FullName)
        try {
            $out = [System.IO.File]::Create($tmp)
            $gz = New-Object System.IO.Compression.GZipStream($out, [System.IO.Compression.CompressionLevel]::Optimal)
            try { $in.CopyTo($gz) } finally { $gz.Dispose() }
        } finally { $in.Dispose() }
        Move-Item $tmp $gzPath -Force
        $gzSize = (Get-Item $gzPath).Length
    } elseif (Test-Path $gzPath) {
        Remove-Item $gzPath -Force
    }
    $lines.Add(('{0}|{1}|{2}|{3}|{4}' -f $rel, $file.Length, $sha, $gzSize, $file.LastWriteTimeUtc.ToString('yyyyMMddHHmmss')))
}
$stage = Join-Path ([System.IO.Path]::GetTempPath()) ('aa-index-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $stage | Out-Null
try {
    $stagedIndex = Join-Path $stage 'index.txt'
    [System.IO.File]::WriteAllText($stagedIndex, ($lines -join "`n") + "`n", $utf8)
    & $tool sign $SigningKey $stagedIndex
    if ($LASTEXITCODE -ne 0) { throw "aapatch sign failed with exit code $LASTEXITCODE" }
    $index = Join-Path $PatchDir 'index.txt'
    Copy-Item $stagedIndex "$index.tmp" -Force
    Copy-Item "$stagedIndex.sig" "$index.sig.tmp" -Force
    Move-Item "$index.tmp" $index -Force
    Move-Item "$index.sig.tmp" "$index.sig" -Force
} finally {
    Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
}
Write-Host "index.txt lists $($published.Count) files (signed)."
Write-Host "Patch folder $PatchDir is current. Serve it with scripts\Start-PatchServer.bat."
