# Removes the LAN Doorbell Pro firewall rule(s). Run as Administrator.
$Group = "LAN Doorbell Pro"
Get-NetFirewallRule -Group $Group -ErrorAction SilentlyContinue | Remove-NetFirewallRule
# Rule name used by version 1.
Get-NetFirewallRule -DisplayName "LAN Doorbell Pro TCP 45872 (Private LAN)" -ErrorAction SilentlyContinue | Remove-NetFirewallRule
Write-Host "LAN Doorbell Pro firewall rules removed (if there were any)."
