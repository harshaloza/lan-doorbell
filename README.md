LAN DOORBELL PRO — COMPLETE SOURCE PACKAGE
==========================================

FEATURES
- Professional app icon and logo (generated from vector-like shapes in code; no external image required).
- System tray app plus an always-on-top floating Ring button (drag it to reposition; close hides it; re-enable from tray menu).
- Multiple receiver PCs: put one computer name or IPv4 address per line.
- Custom two-note "ding-dong" sound embedded in the EXE.
- Optional start with Windows.
- Optional temporary Windows mute override.
- Private-LAN firewall setup and removal scripts.

IMPORTANT
This is a source project, not a compiled installer. Build on Windows with .NET 8 SDK.
Computer names work only if your LAN can resolve them. For stable name lookup, use local DNS/router DHCP reservations. IP address fallback is supported.

BUILD
1. Install .NET 8 SDK.
2. Open PowerShell in this folder.
3. Run:
   dotnet restore
   dotnet publish -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true
4. EXE:
   bin\Release\net8.0-windows\win-x64\publish\LanDoorbell.exe

The app icon is embedded into the executable. The .ico file is also copied beside the app for the tray icon. Keep the icon beside the EXE when running the published app.

CONFIGURE RECEIVER PC(S)
1. Run the EXE.
2. Right-click tray icon > Settings and receivers.
3. Set a shared secret; use exactly the same secret on sender and every receiver.
4. Check "Enable receiver on this PC".
5. Optional: check "Start automatically with Windows".
6. Save.
7. Run Install-Firewall-Receiver.ps1 as Administrator on every receiver:
   Set-ExecutionPolicy -Scope Process Bypass
   .\Install-Firewall-Receiver.ps1
This allows inbound TCP 45872 on Private profile from LocalSubnet only. Use only on a trusted LAN. Never create router port forwarding.

CONFIGURE SENDER
1. Run EXE, open Settings and receivers.
2. In "Receiver computer names / IPs", enter each receiver on a separate line, e.g.:
   DESIGN-PC
   TOOLROOM-PC
   192.168.1.40
3. Set same port and shared secret as receiver(s).
4. Leave "Enable receiver on this PC" unchecked unless this PC should also receive rings.
5. Save. Use the floating **RING** button, or tray menu > **Ring all selected PCs**. The floating button can be dragged around the screen. Its × hides it; show it again from the tray menu.

TEST
On sender PowerShell:
   Test-NetConnection DESIGN-PC -Port 45872
Replace DESIGN-PC with actual receiver name. Look for TcpTestSucceeded: True.
If name lookup fails, use local DNS or a router DHCP reservation. Name-based connection does not itself prevent IP changes; the LAN's name-resolution service must update/resolve the name correctly.

SOUND
Tray menu > Test ding-dong sound. The sound is embedded in the app. If temporary unmute is enabled, Windows may briefly unmute and other apps may become audible. It cannot guarantee sound if volume is zero or output hardware is off.

SECURITY
Shared secret is stored in %AppData%\LanDoorbell\settings.json as plain text. Use a unique secret and only use this on a trusted LAN.

### Compact floating Ring button

The floating button is 44 × 44 pixels and sits just above the Windows taskbar. Left-click the bell to ring all configured receivers. To reposition it, right-click and drag. Use the tray menu's **Show floating Ring button** option to show it again if hidden.



### Floating Ring button and taskbar visibility

The app uses a 44 × 44 pixel always-on-top floating bell button. Left-click rings all configured receivers; right-click and drag to move it. Its position is clamped to the Windows desktop working area so it cannot be dragged onto or underneath the taskbar. No separate taskbar launcher executable is needed.
