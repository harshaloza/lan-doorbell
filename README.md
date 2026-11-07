<p align="center">
  <img src="Assets/LanDoorbell-Logo.png" width="160" alt="LAN Doorbell Pro logo">
</p>

# LAN Doorbell Pro

Ring other Windows PCs on your local network with one click. A small tray app with an
always-on-top bell button. The PC being rung plays a ding-dong, shows who is ringing, and can
answer **"On my way 👍"**.

## Features

- **Floating bell button.** It stays on top, never takes focus from your work, stays out of
  Alt+Tab, scales with display scaling and remembers where you put it.
- **Ring everyone or one PC.** Ring all PCs, or pick one from the tray or button menu. You can
  give PCs friendly names (`Toolroom = 192.168.1.40`).
- **Shows who is ringing.** "🔔 Reception is ringing", with a large flashing alert window and
  a taskbar flash.
- **Replies.** The receiver can answer *On my way* or *Busy, 5 minutes*, and the answer
  appears on the sender's PC.
- **Do not disturb.** Turn it on from the tray, or set a daily schedule (for example
  22:00–07:00). The sender is told the ring was refused.
- **Choose the sound.** Use the built-in ding-dong or your own `.wav`/`.mp3`, at the volume you
  want. Optionally the app unmutes Windows or raises the volume while ringing.
- **Ring history.** A log of every ring sent and received, with replies.
- **Test connection.** Checks each PC is reachable and uses the same secret, without ringing it.
- **Secure on the LAN.** The shared secret never crosses the network (it uses challenge–response
  with HMAC-SHA256). The secret is stored encrypted with Windows DPAPI. Spam and brute-force
  attempts are limited.
- **Works on IPv4 and IPv6, runs one copy only, and starts with Windows if you want.**

## Install

### Option A: download the release (easiest)

Download `LanDoorbell-<version>-win-x64.zip` from the
[Releases page](https://github.com/harshaloza/lan-doorbell/releases), extract it, open PowerShell
in that folder and run the installer as shown below. Nothing else is needed: the EXE includes
the .NET runtime. Windows SmartScreen may warn you because the EXE is not code-signed; choose
*More info → Run anyway*.

### Option B: installer script from source

Open PowerShell in this folder:

```powershell
powershell -ExecutionPolicy Bypass -File .\Install.ps1            # a PC that only sends rings
powershell -ExecutionPolicy Bypass -File .\Install.ps1 -Receiver  # a PC that receives rings
```

The script uses `LanDoorbell.exe` beside it, or builds it (this needs the .NET 10 SDK or newer) and installs it to
`%LOCALAPPDATA%\Programs\LanDoorbell`. It also adds a Start menu shortcut and starts the app.
With `-Receiver`, it also creates the firewall rule; Windows asks for Administrator permission.
Use `-ExePath` to install an EXE you already built, and `-Port` if you changed the port.

To remove the app, run `Uninstall.ps1` from the install folder. Add `-RemoveSettings` to also
delete your settings and history.

### Option C: build it yourself

```powershell
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish
```

The result is a single compressed file, `publish\LanDoorbell.exe` (about 70 MB, because it includes
the .NET runtime). The icon, logo and sound are inside it, so nothing else needs to sit beside it.

## Set up

Every PC needs the **same port** and the **same shared secret**.

1. On the first PC, open **Settings**: double-click the tray bell, or right-click it and choose
   Settings. Go to **Connection** and click **Generate** to create a secret, then **Copy** to
   copy it. Enter that secret on every other PC.
2. **On each PC that should be rung:** go to **Receive rings** and tick **Enable receiver on this
   PC**. Then create the firewall rule, once, as Administrator:

   ```powershell
   powershell -ExecutionPolicy Bypass -File .\Install-Firewall-Receiver.ps1 -Port 45872
   ```

   The rule only allows your **local subnet** on **Private** networks. Set your trusted
   network to *Private* in Windows Settings. **Never forward this port on your router.**
3. **On each PC that rings others:** go to **Ring other PCs** and enter one PC per line:

   ```text
   Toolroom = 192.168.1.40
   Design = DESIGN-PC
   RECEPTION-PC
   ```

   Then click **Test connection**. Each PC should show ✔.

Computer names only work if your network can resolve them. If a name fails, use the IP address,
and give that PC a fixed address (a DHCP reservation) in your router.

## Use

| Action | How |
| --- | --- |
| Ring everyone | Left-click the floating bell, or tray menu → **Ring all PCs** |
| Ring one PC | Right-click the bell or the tray icon → **Ring one PC** |
| Move the bell | Right-click and drag it |
| Hide or show the bell | Bell menu → **Hide this button**; tray menu → **Show floating Ring button** |
| Do not disturb | Tray menu → **Do not disturb**, or schedule it in Settings → Receive rings |
| See past rings | Tray menu → **Ring history…** |

## Upgrading from version 1

Version 2 uses a **new, secure network protocol**. Update **every PC** at the same time: a
version 1 PC and a version 2 PC cannot ring each other. Your old settings are converted
automatically. The plain-text secret is moved to encrypted storage, and your receivers are
kept. If your old secret was the example value, or shorter than 8 characters, Settings asks
you for a new one.

## Files

| Path | Contents |
| --- | --- |
| `%APPDATA%\LanDoorbell\settings.json` | Settings (the secret is DPAPI-encrypted for your Windows account) |
| `%APPDATA%\LanDoorbell\history.jsonl` | Ring history (the last 1000 entries) |
| `%APPDATA%\LanDoorbell\landoorbell.log` | Diagnostic log (rolls over at 1 MB) |

## Troubleshooting

- **"No answer"**: the PC is off, the address is wrong, or the firewall rule is missing. You can
  also check from PowerShell: `Test-NetConnection RECEIVER-PC -Port 45872`.
- **"Connection refused"**: LAN Doorbell is not running on that PC, or its receiver is off.
- **"Shared secret does not match"**: enter exactly the same secret on both PCs (use **Copy**).
- **"Connected, but no greeting"**: that PC still runs version 1. Update it.
- **No sound**: check the volume, or turn on *Raise Windows volume* in Settings → Receive rings.
  The app cannot play sound if the speakers are off.
- See the log file (Settings → General → **Open data folder**) for details.

## Project layout

```text
src/Program.cs            Startup, single instance, crash logging
src/DoorbellContext.cs    Tray icon, menus, wiring between the parts
src/RingServer.cs         Receiver (listens for rings)
src/RingClient.cs         Sender (rings and connection tests)
src/Protocol.cs           Wire protocol and HMAC helpers
src/AppSettings.cs        Settings model
src/SettingsStore.cs      Load/save/migrate settings, start-with-Windows
src/SoundService.cs       Doorbell sound and volume handling
src/RingHistory.cs        Ring history file
src/*Form.cs              Windows: floating button, settings, alert, history
src/BellArt.cs            Vector bell for the floating button (matches the icon)
Assets/design/            Icon/logo sources (SVG) and render_icons.py, which builds the .ico and .png
Install.ps1 / Uninstall.ps1 / Install-Firewall-Receiver.ps1 / Remove-Firewall-Rule.ps1
```

## License

[MIT](LICENSE) © 2026 harshaloza. You can use, change and share this app freely, as long as the
copyright and license notice stay with it.
