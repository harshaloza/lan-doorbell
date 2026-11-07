<#
.SYNOPSIS
  Removes LAN Doorbell Pro for the current user.

.DESCRIPTION
  Closes the app, removes the Start menu shortcut, the start-with-Windows entry, the program folder
  and (with Administrator permission) the firewall rule. Your settings and ring history in
  %APPDATA%\LanDoorbell are kept unless you add -RemoveSettings.
#>
param([switch]$RemoveSettings)

$installDir = Join-Path $env:LOCALAPPDATA "Programs\LanDoorbell"

Get-Process LanDoorbell -ErrorAction SilentlyContinue | Stop-Process -Force
Remove-ItemProperty -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" -Name "LanDoorbell" -ErrorAction SilentlyContinue
Remove-Item (Join-Path ([Environment]::GetFolderPath("Programs")) "LAN Doorbell Pro.lnk") -ErrorAction SilentlyContinue

$hasRule = Get-NetFirewallRule -Group "LAN Doorbell Pro" -ErrorAction SilentlyContinue
if (-not $hasRule) {
    $hasRule = Get-NetFirewallRule -DisplayName "LAN Doorbell Pro TCP 45872 (Private LAN)" -ErrorAction SilentlyContinue
}
if ($hasRule) {
    Write-Host "Removing the firewall rule (Windows will ask for Administrator permission)..."
    $removeScript = Join-Path $PSScriptRoot "Remove-Firewall-Rule.ps1"
    Start-Process powershell -Verb RunAs -Wait -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$removeScript`""
}

Start-Sleep -Milliseconds 500   # let the closed app release its files
Remove-Item $installDir -Recurse -Force -ErrorAction SilentlyContinue

if ($RemoveSettings) {
    Remove-Item (Join-Path $env:APPDATA "LanDoorbell") -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host "Settings and history removed."
} else {
    Write-Host "Settings and history kept in $env:APPDATA\LanDoorbell (use -RemoveSettings to delete them)."
}
Write-Host "LAN Doorbell Pro was uninstalled." -ForegroundColor Green
