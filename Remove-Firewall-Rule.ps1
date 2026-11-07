Remove-NetFirewallRule -DisplayName "LAN Doorbell Pro TCP 45872 (Private LAN)" -ErrorAction SilentlyContinue
Write-Host "Rule removed if it existed."
