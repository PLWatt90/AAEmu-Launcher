# Shared helpers for the scripts in this folder (dot-sourced, not run on its own).
# Works in Windows PowerShell 5.1 and PowerShell 7.

$RepoRoot   = Split-Path -Parent $PSScriptRoot
$ConfigDir  = Join-Path $RepoRoot 'config'
$OutDir     = Join-Path $RepoRoot 'out'
$DefaultKey = Join-Path $env:LOCALAPPDATA 'AAEmu-Launcher\patch-signing-key.pem'

function Read-JsonConfig([string]$Path) {
    # The config files allow whole-line // comments; Windows PowerShell's ConvertFrom-Json does not.
    if (-not (Test-Path $Path)) { throw "config file $Path not found (run scripts\Setup-Server.ps1 first)" }
    $text = (Get-Content $Path -Encoding UTF8 | Where-Object { $_.TrimStart() -notlike '//*' }) -join "`n"
    return $text | ConvertFrom-Json
}

function Get-LauncherConfig {
    $path = Join-Path $ConfigDir 'launcher.config.json'
    if (-not (Test-Path $path)) {
        Write-Warning "config\launcher.config.json not found; using config\launcher.config.example.json"
        $path = Join-Path $ConfigDir 'launcher.config.example.json'
    }
    return Read-JsonConfig $path
}

function Get-LauncherExeName {
    $name = (Get-LauncherConfig).exeName
    if ([string]::IsNullOrWhiteSpace($name)) { $name = 'AAEmu-Launcher' }
    if ($name -notmatch '^[A-Za-z0-9][A-Za-z0-9._ -]{0,63}$') { throw "exeName '$name' is not a usable file name" }
    return $name
}

function Get-AAPatchExe { Join-Path $OutDir 'aapatch\aapatch.exe' }
function Get-LauncherExe { Join-Path $OutDir ("launcher\" + (Get-LauncherExeName) + '.exe') }
function Get-PatchServerExe { Join-Path $OutDir 'patch-server\aa-patch-server.exe' }

function Invoke-Publish([string]$Project, [string]$Target, [string[]]$Extra = @()) {
    Write-Host "Building $Project -> $Target"
    & dotnet publish (Join-Path $RepoRoot $Project) -c Release -nologo -v q -o $Target @Extra
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish $Project failed with exit code $LASTEXITCODE" }
}

function Build-AAPatch { Invoke-Publish 'src\AAPatch\AAPatch.csproj' (Join-Path $OutDir 'aapatch') }
function Build-Launcher { Invoke-Publish 'src\Launcher\Launcher.csproj' (Join-Path $OutDir 'launcher') @("-p:LauncherName=$(Get-LauncherExeName)") }
function Build-PatchServer { Invoke-Publish 'src\PatchServer\PatchServer.csproj' (Join-Path $OutDir 'patch-server') }
