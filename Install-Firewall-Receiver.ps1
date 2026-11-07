# Run in PowerShell as Administrator on EACH RECEIVER PC.
# LocalSubnet scope and Private profile only; no Public profile exposure.
$ErrorActionPreference = "Stop"
$RuleName = "LAN Doorbell Pro TCP 45872 (Private LAN)"
Get-NetFirewallRule -DisplayName $RuleName -ErrorAction SilentlyContinue | Remove-NetFirewallRule
New-NetFirewallRule `
  -DisplayName $RuleName `
  -Description "LAN Doorbell Pro receiver; private LAN only" `
  -Direction Inbound -Action Allow -Protocol TCP -LocalPort 45872 `
  -Profile Private -RemoteAddress LocalSubnet
Write-Host "Firewall rule created. Ensure this is a trusted Private network."
