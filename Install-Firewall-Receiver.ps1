<#
.SYNOPSIS
  Allows LAN Doorbell Pro to receive rings. Run as Administrator on EACH RECEIVER PC.

.DESCRIPTION
  Creates one inbound TCP rule for the Private network profile, limited to your local subnet.
  Nothing is opened on Public networks. Never forward this port on your router.

.PARAMETER Port
  The TCP port set in LAN Doorbell Settings -> Connection (default 45872).

.PARAMETER ProgramPath
  Optional: full path to LanDoorbell.exe. When given, the rule only applies to that program.

.EXAMPLE
  .\Install-Firewall-Receiver.ps1
  .\Install-Firewall-Receiver.ps1 -Port 46000 -ProgramPath "$env:LOCALAPPDATA\Programs\LanDoorbell\LanDoorbell.exe"
#>
param(
    [ValidateRange(1024, 65535)]
    [int]$Port = 45872,
    [string]$ProgramPath
)

$ErrorActionPreference = "Stop"
$Group = "LAN Doorbell Pro"

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host "Please run this script as Administrator (right-click PowerShell -> Run as administrator)." -ForegroundColor Red
    exit 1
}

# Remove earlier rules: this group, plus the fixed-name rule from version 1.
Get-NetFirewallRule -Group $Group -ErrorAction SilentlyContinue | Remove-NetFirewallRule
Get-NetFirewallRule -DisplayName "LAN Doorbell Pro TCP 45872 (Private LAN)" -ErrorAction SilentlyContinue | Remove-NetFirewallRule

$rule = @{
    DisplayName   = "LAN Doorbell Pro TCP $Port (Private LAN)"
    Group         = $Group
    Description   = "LAN Doorbell Pro receiver. Private networks and local subnet only."
    Direction     = "Inbound"
    Action        = "Allow"
    Protocol      = "TCP"
    LocalPort     = $Port
    Profile       = "Private"
    RemoteAddress = "LocalSubnet"
}
if ($ProgramPath) {
    if (-not (Test-Path $ProgramPath)) { throw "Program not found: $ProgramPath" }
    $rule.Program = (Resolve-Path $ProgramPath).Path
}
New-NetFirewallRule @rule | Out-Null

Write-Host "Firewall rule created for TCP port $Port (Private network, local subnet only)." -ForegroundColor Green
$profiles = Get-NetConnectionProfile | Select-Object -ExpandProperty NetworkCategory
if ($profiles -notcontains "Private") {
    Write-Host "Note: none of your network connections is set to 'Private'. Rings will be blocked until your" -ForegroundColor Yellow
    Write-Host "trusted home/office network is set to Private (Settings -> Network & internet -> your network)." -ForegroundColor Yellow
}
