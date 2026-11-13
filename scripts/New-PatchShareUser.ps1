<#
.SYNOPSIS
  Optional: shares the patch folder read-only over SMB (Windows file sharing) for LAN players.

.DESCRIPTION
  Only needed when you want the launcher's share fallback (shareName in launcher.config.json). The
  HTTP patch server is the main route and needs none of this.

  Run ONCE from an elevated PowerShell on the server machine:
      powershell -ExecutionPolicy Bypass -File scripts\New-PatchShareUser.ps1 -PatchDir C:\AAEmu\Patch

  It
    1. creates the share (if missing) for the patch folder,
    2. creates a local, non-admin user (password prompted, never expires),
    3. gives only that user Read on the share (Windows 11 clients refuse guest access by default),
    4. optionally hides the user from the Windows sign-in screen.

  Players then store the credential once per PC with the launcher's "Store share credential..."
  button, or: cmdkey /add:<server> /user:<UserName> /pass:<password>
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PatchDir,
    [string]$UserName = 'aapatch',
    [string]$ShareName = 'AAEmuPatch',
    [bool]$HideFromLoginScreen = $true
)
$ErrorActionPreference = 'Stop'

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) { throw 'Run this from an elevated (Administrator) PowerShell.' }
if (-not (Test-Path $PatchDir)) { throw "Patch folder '$PatchDir' does not exist." }

$password = Read-Host -AsSecureString "Password for the share account '$UserName'"
$confirm  = Read-Host -AsSecureString 'Repeat the password'
$p1 = [Runtime.InteropServices.Marshal]::PtrToStringBSTR([Runtime.InteropServices.Marshal]::SecureStringToBSTR($password))
$p2 = [Runtime.InteropServices.Marshal]::PtrToStringBSTR([Runtime.InteropServices.Marshal]::SecureStringToBSTR($confirm))
if ($p1 -ne $p2) { throw 'The passwords do not match.' }
if ($p1.Length -lt 8) { throw 'Use at least 8 characters.' }
$p1 = $null; $p2 = $null

if (Get-LocalUser -Name $UserName -ErrorAction SilentlyContinue) {
    Write-Host "Local user '$UserName' already exists; updating its password."
    Set-LocalUser -Name $UserName -Password $password -PasswordNeverExpires $true -UserMayChangePassword $false
} else {
    New-LocalUser -Name $UserName -Password $password -Description 'Read-only access to the game patch share' `
        -PasswordNeverExpires -UserMayNotChangePassword -AccountNeverExpires | Out-Null
    Write-Host "Local user '$UserName' created."
}

if (-not (Get-SmbShare -Name $ShareName -ErrorAction SilentlyContinue)) {
    New-SmbShare -Name $ShareName -Path (Resolve-Path $PatchDir).Path -ReadAccess $UserName | Out-Null
    Write-Host "Share '$ShareName' created for $PatchDir."
}
if (Get-SmbShareAccess -Name $ShareName | Where-Object { $_.AccountName -eq 'Everyone' }) {
    Revoke-SmbShareAccess -Name $ShareName -AccountName 'Everyone' -Force | Out-Null
    Write-Host "Removed 'Everyone' from share '$ShareName'."
}
Grant-SmbShareAccess -Name $ShareName -AccountName $UserName -AccessRight Read -Force | Out-Null
Write-Host "Granted Read on '$ShareName' to '$UserName'."

if ($HideFromLoginScreen) {
    $key = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon\SpecialAccounts\UserList'
    if (-not (Test-Path $key)) { New-Item -Path $key -Force | Out-Null }
    New-ItemProperty -Path $key -Name $UserName -PropertyType DWord -Value 0 -Force | Out-Null
    Write-Host "'$UserName' hidden from the Windows sign-in screen."
}

Write-Host ''
Get-SmbShareAccess -Name $ShareName | Format-Table AccountName, AccessRight, AccessControlType -AutoSize
Write-Host "Set `"shareName`": `"$ShareName`" and `"shareUser`": `"$UserName`" in config\launcher.config.json and rebuild the launcher."
