<#
.SYNOPSIS
  Installs LAN Doorbell Pro for the current user (no admin needed, except for -Receiver).

.DESCRIPTION
  - Uses -ExePath, or LanDoorbell.exe next to this script (release zip), or builds it from
    source (requires the .NET 10 SDK or newer).
  - Copies it to %LOCALAPPDATA%\Programs\LanDoorbell and adds a Start menu shortcut.
  - With -Receiver: also creates the firewall rule (asks for Administrator permission).
  - Starts the app. Choose "Start automatically with Windows" in its Settings.

.EXAMPLE
  .\Install.ps1                      # sender PC
  .\Install.ps1 -Receiver            # PC that should receive rings
  .\Install.ps1 -Receiver -Port 46000 -ExePath D:\Downloads\LanDoorbell.exe
#>
param(
    [string]$ExePath,
    [switch]$Receiver,
    [ValidateRange(1024, 65535)]
    [int]$Port = 45872
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$installDir = Join-Path $env:LOCALAPPDATA "Programs\LanDoorbell"
$target = Join-Path $installDir "LanDoorbell.exe"

if (-not $ExePath -and (Test-Path (Join-Path $root "LanDoorbell.exe"))) {
    $ExePath = Join-Path $root "LanDoorbell.exe"      # extracted release zip
}
if (-not $ExePath) {
    $ExePath = Join-Path $root "publish\LanDoorbell.exe"
    if (-not (Test-Path $ExePath)) {
        if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
            throw "LanDoorbell.exe was not found and the .NET SDK is not installed. Install the .NET 10 SDK (or newer), or pass -ExePath."
        }
        Write-Host "Building LanDoorbell.exe (the first build takes a minute)..."
        & dotnet publish (Join-Path $root "LanDoorbell.csproj") -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o (Join-Path $root "publish")
        if ($LASTEXITCODE -ne 0) { throw "Build failed." }
    }
}
if (-not (Test-Path $ExePath)) { throw "Not found: $ExePath" }

# Close a running copy so the EXE can be replaced.
$running = Get-Process LanDoorbell -ErrorAction SilentlyContinue
if ($running) {
    Write-Host "Closing the running LAN Doorbell..."
    $running | Stop-Process -Force
    $running | Wait-Process -Timeout 10 -ErrorAction SilentlyContinue
}

New-Item -ItemType Directory -Force $installDir | Out-Null
if ((Resolve-Path $ExePath).Path -ne $target) { Copy-Item $ExePath $target -Force }
foreach ($script in "Install-Firewall-Receiver.ps1", "Remove-Firewall-Rule.ps1", "Uninstall.ps1") {
    $source = Join-Path $root $script
    if ((Test-Path $source) -and ((Resolve-Path $source).Path -ne (Join-Path $installDir $script))) {
        Copy-Item $source $installDir -Force
    }
}

# Start menu shortcut
$shortcut = Join-Path ([Environment]::GetFolderPath("Programs")) "LAN Doorbell Pro.lnk"
$shell = New-Object -ComObject WScript.Shell
$link = $shell.CreateShortcut($shortcut)
$link.TargetPath = $target
$link.WorkingDirectory = $installDir
$link.Description = "Ring other PCs on your local network"
$link.Save()
Write-Host "Installed to $installDir" -ForegroundColor Green

if ($Receiver) {
    Write-Host "Creating the firewall rule (Windows will ask for Administrator permission)..."
    $firewallScript = Join-Path $installDir "Install-Firewall-Receiver.ps1"
    $arguments = "-NoProfile -ExecutionPolicy Bypass -File `"$firewallScript`" -Port $Port -ProgramPath `"$target`""
    $process = Start-Process powershell -Verb RunAs -Wait -PassThru -ArgumentList $arguments
    if ($process.ExitCode -eq 0) {
        Write-Host "Firewall rule created." -ForegroundColor Green
    } else {
        Write-Host "The firewall rule was not created. Run Install-Firewall-Receiver.ps1 as Administrator later." -ForegroundColor Yellow
    }
    Write-Host "In the app: Settings -> Receive rings -> tick 'Enable receiver on this PC'."
}

Start-Process $target
Write-Host "LAN Doorbell Pro is running. Look for the bell in the system tray."
