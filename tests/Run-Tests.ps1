<#
.SYNOPSIS
  End-to-end test of the patch flow: Make-Patch -> patch server -> launcher update step -> aapatch.

.DESCRIPTION
  Needs no game client and touches nothing outside tests\work: it makes a throwaway signing key, a
  "server" game_pak with test entries, publishes it with scripts\Make-Patch.ps1, serves it with the
  patch server on a local port and drives the launcher's --test-update mode against empty client paks.
  Then it checks that tampered, unsigned or path-escaping patches are refused.

  Run from the repository root:  powershell -ExecutionPolicy Bypass -File tests\Run-Tests.ps1
#>
param([int]$Port = 8199)
$ErrorActionPreference = 'Stop'
$Repo = Split-Path -Parent $PSScriptRoot
. (Join-Path $Repo 'scripts\Common.ps1')
$W = Join-Path $PSScriptRoot 'work'
$script:failures = 0

function Check([string]$name, [bool]$ok, [string]$detail = '') {
    if ($ok) { Write-Host "PASS  $name" -ForegroundColor Green }
    else { Write-Host "FAIL  $name $detail" -ForegroundColor Red; $script:failures++ }
}

if (Test-Path $W) { Remove-Item $W -Recurse -Force }
New-Item -ItemType Directory $W | Out-Null

# ---- build
& (Join-Path $Repo 'scripts\Build.ps1') 3>$null
$mkpakDir = Join-Path $W 'mkpak'
& dotnet publish (Join-Path $PSScriptRoot 'MakeTestPak\MakeTestPak.csproj') -c Release -nologo -v q -o $mkpakDir | Out-Null
$mkpak = Join-Path $mkpakDir 'MakeTestPak.exe'
$aapatch = Get-AAPatchExe
$launcherBuilt = Get-LauncherExe
$serverExe = Get-PatchServerExe

# ---- key, server pak, publish config
$key = Join-Path $W 'keys\test-key.pem'
& $aapatch keygen $key | Out-Null
$publicKey = (& $aapatch pubkey $key).Trim()
Check 'keygen + pubkey' ($publicKey.Length -gt 80)

$src = Join-Path $W 'src'
New-Item -ItemType Directory $src | Out-Null
'hello from the server' | Set-Content (Join-Path $src 'hello.txt')
$big = New-Object byte[] (3MB); (New-Object Random 7).NextBytes($big); [IO.File]::WriteAllBytes((Join-Path $src 'big.bin'), $big)
$serverPak = Join-Path $W 'server\game_pak'
New-Item -ItemType Directory (Split-Path $serverPak) | Out-Null
& $mkpak $serverPak "game/test/hello.txt=$src\hello.txt" "game/test/big.bin=$src\big.bin" | Out-Null

$patchDir = Join-Path $W 'patch'
'game/test/hello.txt', 'game/test/big.bin' | Set-Content (Join-Path $W 'entries.txt')
@{ pak = $serverPak; patchDir = $patchDir; signingKey = $key; entriesFile = 'entries.txt'; extraFiles = @(); copyToClients = @() } |
    ConvertTo-Json | Set-Content (Join-Path $W 'publish.config.json')
& (Join-Path $Repo 'scripts\Make-Patch.ps1') -Config (Join-Path $W 'publish.config.json') -NoBuild | Out-Null
Check 'Make-Patch wrote a signed index' ((Test-Path "$patchDir\index.txt") -and (Test-Path "$patchDir\index.txt.sig"))
Check 'large file got a .gz copy' (Test-Path "$patchDir\game\test\big.bin.gz")

# ---- client: launcher copy configured by a side launcher.config.json
$clientTool = Join-Path $W 'launcher'
New-Item -ItemType Directory $clientTool | Out-Null
Copy-Item $launcherBuilt $clientTool
$launcher = Join-Path $clientTool (Split-Path -Leaf $launcherBuilt)
$url = "http://127.0.0.1:$Port/"
function Set-SideConfig([string]$pub) {
    @{ patchUrl = $url; patchPublicKey = $pub; defaultServer = '127.0.0.1'; authMode = 'password'; productName = 'TestGame' } |
        ConvertTo-Json | Set-Content (Join-Path $clientTool 'launcher.config.json')
}
Set-SideConfig $publicKey
# The launcher is a GUI exe: PowerShell does not wait for it unless started like this.
function Invoke-Launcher([string[]]$arguments) {
    $out = Join-Path $W 'launcher-out.txt'
    $quoted = $arguments | ForEach-Object { if ($_ -match '[\s"]' -or $_ -eq '') { '"' + ($_ -replace '"', '\"') + '"' } else { $_ } }
    $p = Start-Process $launcher -ArgumentList $quoted -Wait -PassThru -NoNewWindow -RedirectStandardOutput $out
    return @{ Code = $p.ExitCode; Text = (Get-Content $out -Raw) + '' }
}
$cfg = (Invoke-Launcher @('--print-config')).Text
Check 'side config overrides built-in config' ($cfg -match 'TestGame' -and $cfg -match [regex]::Escape($url))

function New-Client([string]$name) {
    $dir = Join-Path $W $name
    New-Item -ItemType Directory $dir | Out-Null
    & $mkpak "$dir\game_pak" | Out-Null
    return $dir
}
function Update([string]$dir, [string]$source = $url) {
    return Invoke-Launcher @('--test-update', "$dir\game_pak", $source, $clientTool, '127.0.0.1')
}

function Start-PatchServer([string]$root) {
    $process = Start-Process $serverExe -ArgumentList @('--root', "`"$root`"", '--port', $Port) -PassThru -WindowStyle Hidden
    for ($i = 0; $i -lt 60; $i++) {
        try { Invoke-WebRequest $url -UseBasicParsing -TimeoutSec 2 | Out-Null; return $process } catch { Start-Sleep -Milliseconds 500 }
        if ($process.HasExited) { throw "patch server exited with code $($process.ExitCode)" }
    }
    throw 'patch server did not answer within 30 s'
}

$server = Start-PatchServer $patchDir
try {
    $status = Invoke-WebRequest $url -UseBasicParsing
    Check 'patch server status page names the launcher' ($status.Content -match [regex]::Escape((Split-Path -Leaf $launcherBuilt)))

    # A: fresh client pak gets both entries, a second run is a no-op, verify hashes the content
    $a = New-Client 'clientA'
    $r = Update $a
    Check 'A: update applies the patch' ($r.Code -eq 0 -and $r.Text -match 'signature verified') $r.Text
    $r = Update $a
    Check 'A: second run is up to date' ($r.Code -eq 0 -and $r.Text -match 'up to date') $r.Text
    & $aapatch verify "$a\game_pak" $patchDir | Out-Null
    Check 'A: aapatch verify' ($LASTEXITCODE -eq 0)

    # B: one byte of index.txt changed -> refused
    Copy-Item "$patchDir\index.txt" "$W\index.bak" -Force
    $text = [IO.File]::ReadAllText("$patchDir\index.txt"); [IO.File]::WriteAllText("$patchDir\index.txt", $text.Replace('|0|', '|1|'))
    $r = Update (New-Client 'clientB')
    Check 'B: tampered index refused' ($r.Code -ne 0 -and $r.Text -match 'REFUSED') $r.Text
    Copy-Item "$W\index.bak" "$patchDir\index.txt" -Force

    # C: signature missing -> refused
    Move-Item "$patchDir\index.txt.sig" "$W\sig.bak" -Force
    $r = Update (New-Client 'clientC')
    Check 'C: unsigned index refused' ($r.Code -ne 0 -and $r.Text -match 'REFUSED') $r.Text
    Move-Item "$W\sig.bak" "$patchDir\index.txt.sig" -Force

    # D: a served file swapped while the index stays signed -> download fails, pak untouched
    $hello = "$patchDir\game\test\hello.txt"
    Copy-Item $hello "$W\hello.bak" -Force
    $bytes = [IO.File]::ReadAllBytes($hello); $bytes[0] = $bytes[0] -bxor 1; [IO.File]::WriteAllBytes($hello, $bytes)
    $d = New-Client 'clientD'
    $r = Update $d
    Check 'D: swapped file refused' ($r.Code -ne 0 -and $r.Text -match 'does not match the signed index') $r.Text
    & $aapatch status "$d\game_pak" $patchDir | Out-Null
    Check 'D: client pak left unpatched' ($LASTEXITCODE -eq 1)
    Copy-Item "$W\hello.bak" $hello -Force

    # E: wrong public key in the launcher -> refused
    $other = Join-Path $W 'keys\other-key.pem'
    & $aapatch keygen $other | Out-Null
    Set-SideConfig ((& $aapatch pubkey $other).Trim())
    $r = Update (New-Client 'clientE')
    Check 'E: index signed by another key refused' ($r.Code -ne 0 -and $r.Text -match 'REFUSED') $r.Text

    # F: no public key at all -> refused
    Set-SideConfig ''
    $r = Update (New-Client 'clientF')
    Check 'F: launcher without a key refuses HTTP patches' ($r.Code -ne 0 -and $r.Text -match 'no patch signing key') $r.Text
    Set-SideConfig $publicKey

    # G: server never serves key material
    'secret' | Set-Content "$patchDir\leak.pem"
    try { Invoke-WebRequest "${url}leak.pem" -UseBasicParsing | Out-Null; $code = 200 } catch { $code = [int]$_.Exception.Response.StatusCode }
    Check 'G: .pem blocked by the patch server' ($code -eq 404) "got $code"
    Remove-Item "$patchDir\leak.pem"
} finally {
    Stop-Process $server -Force -ErrorAction SilentlyContinue
}

# H: a signed index naming ../ paths -> refused, nothing written outside the download folder
$evil = Join-Path $W 'patch-evil'
New-Item -ItemType Directory $evil | Out-Null
[IO.File]::WriteAllText("$evil\manifest.txt", "id=evil`n../../evil.txt|5|5d41402abc4b2a76b9719d911017c592`n")
$m = Get-Item "$evil\manifest.txt"; $msha = (Get-FileHash $m.FullName -Algorithm SHA256).Hash.ToLower()
[IO.File]::WriteAllText("$evil\index.txt", "manifest.txt|$($m.Length)|$msha|0|20260101000000`n../../evil.txt|5|2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824|0|20260101000000`n")
& $aapatch sign $key "$evil\index.txt" | Out-Null
$server = Start-PatchServer $evil
try {
    $r = Update (New-Client 'clientH')
    Check 'H: signed ../ path refused' ($r.Code -ne 0 -and $r.Text -match 'REFUSED') $r.Text
    Check 'H: nothing escaped the work folder' (@(Get-ChildItem (Split-Path $W) -Recurse -Filter evil.txt -ErrorAction SilentlyContinue).Count -eq 0)
} finally {
    Stop-Process $server -Force -ErrorAction SilentlyContinue
}

# I: tokens match the documented formula (docs/login-protocol.md)
$token = (Invoke-Launcher @('--print-token', 'Alice', 'correct horse')).Text.Trim()
$expected = 'v1:' + [BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash([Text.Encoding]::UTF8.GetBytes('aaemu-launcher|alice|correct horse'))).Replace('-', '').ToLower()
Check 'I: password token formula' ($token -eq $expected) "$token <> $expected"

Write-Host ''
if ($script:failures -eq 0) { Write-Host 'All tests passed.' -ForegroundColor Green; exit 0 }
Write-Host "$script:failures test(s) failed." -ForegroundColor Red
exit 1
