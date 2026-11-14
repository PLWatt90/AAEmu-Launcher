<#
.SYNOPSIS
  Rebuilds the player patch folder from the server machine's game_pak.

.DESCRIPTION
  Exports the listed pak entries into the patch folder, writes manifest.txt with a new patch id,
  copies aapatch.exe and the launcher next to them, copies new or changed loose client files
  (looseFiles) under files\, writes .gz copies of large files, index.txt
  ("path|size|sha256|gzSize|modifiedUtc") and index.txt.sig (signed with your private key), and
  last removes loose files that are no longer published.
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

# The entries go through a list file: a long list does not fit on the Windows command line.
$entriesList = Join-Path ([System.IO.Path]::GetTempPath()) ('aa-entries-' + [guid]::NewGuid().ToString('N') + '.txt')
[System.IO.File]::WriteAllLines($entriesList, [string[]]$Entries, (New-Object System.Text.UTF8Encoding $false))
try {
    & $tool manifest $Pak $PatchDir "@$entriesList"
    if ($LASTEXITCODE -ne 0) { throw "aapatch manifest failed with exit code $LASTEXITCODE" }
} finally { Remove-Item $entriesList -Force -ErrorAction SilentlyContinue }
Copy-Item $tool (Join-Path $PatchDir 'aapatch.exe') -Force
Copy-Item $launcher (Join-Path $PatchDir $launcherName) -Force
foreach ($extra in @($settings.extraFiles)) {
    if (-not $extra) { continue }
    $source = if ([IO.Path]::IsPathRooted($extra)) { $extra } else { Join-Path $configFolder $extra }
    Copy-Item $source $PatchDir -Force
    Write-Host "copied $source"
}
# Loose client files: looseFiles = [{ "from": <file or folder on this PC>, "to": <path under the client root> }].
# They are published under files/<to> and the launcher writes them into the client folder (not into game_pak).
# Only new or changed files are copied (compared by size and SHA-256), so an unchanged file and its .gz stay
# downloadable during the whole run. Files that left the list are removed at the end of the script, once
# the new index.txt is in place. With no looseFiles and no files\ folder nothing is created or deleted.
$looseRoot = Join-Path $PatchDir 'files'
$looseRel = New-Object System.Collections.Generic.List[string]
$looseKeep = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
$looseSha = @{}     # 'files/<to>' -> SHA-256 of a published copy found unchanged (saves hashing it again below)
$looseCopied = 0
foreach ($item in @($settings.looseFiles)) {
    if (-not $item) { continue }
    $from = if ([IO.Path]::IsPathRooted($item.from)) { $item.from } else { Join-Path $configFolder $item.from }
    $to = ("$($item.to)" -replace '\\', '/').Trim('/')
    if (-not $to -or $to -match '(^|/)\.\.?(/|$)' -or $to.Contains(':')) { throw "looseFiles: bad target '$($item.to)'" }
    if (-not (Test-Path $from)) { throw "looseFiles: $from not found" }
    $src = Get-Item $from
    $pairs = New-Object System.Collections.Generic.List[object]
    if ($src.PSIsContainer) {
        foreach ($f in Get-ChildItem $src.FullName -Recurse -File) {
            $pairs.Add([pscustomobject]@{ From = $f.FullName; To = $to + '/' + $f.FullName.Substring($src.FullName.TrimEnd('\').Length + 1).Replace('\', '/') })
        }
    } else { $pairs.Add([pscustomobject]@{ From = $src.FullName; To = $to }) }
    foreach ($pair in $pairs) {
        $rel = 'files/' + $pair.To
        $dest = Join-Path $looseRoot ($pair.To -replace '/', '\')
        $source = Get-Item -LiteralPath $pair.From
        $sourceTime = $source.LastWriteTimeUtc
        $sourceSha = (Get-FileHash -LiteralPath $source.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        $oldLength = -1
        $oldTime = [DateTime]::MinValue
        if (Test-Path -LiteralPath $dest -PathType Leaf) {
            $old = Get-Item -LiteralPath $dest
            $oldLength = $old.Length
            $oldTime = $old.LastWriteTimeUtc
        }
        if ($oldLength -eq $source.Length -and (Get-FileHash -LiteralPath $dest -Algorithm SHA256).Hash.ToLowerInvariant() -eq $sourceSha) {
            $looseSha[$rel] = $sourceSha
        } else {
            # A path that changed between file and folder since the last run makes room first.
            if (Test-Path -LiteralPath $dest -PathType Container) { Remove-Item -LiteralPath $dest -Recurse -Force }
            for ($parent = Split-Path $dest -Parent; $parent.Length -gt $looseRoot.Length; $parent = Split-Path $parent -Parent) {
                if (Test-Path -LiteralPath $parent -PathType Leaf) { Remove-Item -LiteralPath $parent -Force }
            }
            New-Item -ItemType Directory -Force (Split-Path $dest -Parent) | Out-Null
            # The old .gz goes first: an interrupted run must never leave the .gz of another version behind.
            if (Test-Path -LiteralPath "$dest.gz") { Remove-Item -LiteralPath "$dest.gz" -Force }
            Copy-Item -LiteralPath $source.FullName $dest -Force
            # Launchers take a loose file with the index's size and time as current without hashing it. New
            # content under the old size and time would never reach them, so it gets a new time here.
            if ($oldLength -eq $source.Length -and [Math]::Abs(($oldTime - $sourceTime).TotalSeconds) -le 2) {
                (Get-Item -LiteralPath $dest).LastWriteTimeUtc = [DateTime]::UtcNow
            }
            $looseSha.Remove($rel)
            $looseCopied++
        }
        $looseRel.Add($rel)
        [void]$looseKeep.Add($pair.To)
        [void]$looseKeep.Add($pair.To + '.gz')
    }
}
if ($looseRel.Count -gt 0) { Write-Host "$($looseRel.Count) loose client files: $looseCopied copied, $($looseRel.Count - $looseCopied) unchanged" }

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
$published = @($Entries | ForEach-Object { ($_ -replace '\\', '/').TrimStart('/') }) + @('manifest.txt', 'aapatch.exe', $launcherName) + @($looseRel)
foreach ($rel in $published) {
    $file = Get-Item -LiteralPath (Join-Path $PatchDir ($rel -replace '/', '\'))
    # An unchanged loose file was hashed above and keeps the .gz an earlier run made from the same content.
    $unchanged = $looseSha.ContainsKey($rel)
    $sha = if ($unchanged) { $looseSha[$rel] } else { (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
    $gzPath = $file.FullName + '.gz'
    $gzSize = 0
    if ($file.Length -ge 1MB) {
        if (-not ($unchanged -and (Test-Path -LiteralPath $gzPath))) {
            $tmp = $gzPath + '.tmp'
            $in = [System.IO.File]::OpenRead($file.FullName)
            try {
                $out = [System.IO.File]::Create($tmp)
                $gz = New-Object System.IO.Compression.GZipStream($out, [System.IO.Compression.CompressionLevel]::Optimal)
                try { $in.CopyTo($gz) } finally { $gz.Dispose() }
            } finally { $in.Dispose() }
            Move-Item $tmp $gzPath -Force
        }
        $gzSize = (Get-Item -LiteralPath $gzPath).Length
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

# Only now, with the new index in place, remove what is no longer published under files\ (and its .gz):
# until here a launcher still holding the old index could fetch those files.
$script:looseRemoved = 0
function Remove-StaleLooseFiles([string]$Dir, [string]$Rel) {
    foreach ($child in @(Get-ChildItem -LiteralPath $Dir -Force)) {
        $childRel = if ($Rel) { "$Rel/$($child.Name)" } else { $child.Name }
        if ($child.PSIsContainer) {
            # Never walk through a junction or symlink: what it points to is not ours to clean up.
            if ($child.Attributes -band [IO.FileAttributes]::ReparsePoint) { continue }
            Remove-StaleLooseFiles $child.FullName $childRel
            if (@(Get-ChildItem -LiteralPath $child.FullName -Force).Count -eq 0) { Remove-Item -LiteralPath $child.FullName -Force }
        } elseif (-not $looseKeep.Contains($childRel)) {
            Remove-Item -LiteralPath $child.FullName -Force
            $script:looseRemoved++
        }
    }
}
if (Test-Path -LiteralPath $looseRoot -PathType Container) {
    Remove-StaleLooseFiles $looseRoot ''
    if (@(Get-ChildItem -LiteralPath $looseRoot -Force).Count -eq 0) { Remove-Item -LiteralPath $looseRoot -Force }
    if ($script:looseRemoved -gt 0) { Write-Host "removed $script:looseRemoved files under files\ that are no longer published" }
}
Write-Host "index.txt lists $($published.Count) files (signed)."
Write-Host "Patch folder $PatchDir is current. Serve it with scripts\Start-PatchServer.bat."
