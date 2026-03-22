# Inkbridge — Windows Service

Inkbridge runs as a headless background service that pairs with the Inkbridge Android tablet app over your local Wi-Fi.

---

## Requirements

- Windows 10 (version 2004 / build 19041) or newer
- [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/en-us/download/dotnet/8.0) installed
- Both PC and tablet on the **same Wi-Fi network**

---

## Running the Service

1. Open **File Explorer** and navigate to:
   ```
   f:\Projects\TabletEasyWrite\Inkbridge.Windows\
   ```
2. Double-click **`run.bat`**
   - It will build the project the first time and launch it automatically.
3. A small **system tray icon** will appear (bottom-right corner of the taskbar). That means the service is running.

> To stop the service: right-click the tray icon → **Exit**.

---

## Auto-Start on Login (Optional)

To make Inkbridge start automatically with Windows:

1. Press `Win + R`, type `shell:startup`, and press Enter.
2. Place a shortcut to `publish\Inkbridge.Windows.exe` in the folder that opens.

---

## How It Works

| Feature | Detail |
|---|---|
| **Discovery** | Broadcasts `_inkbridge._tcp` via mDNS on port `8765`. The Android app finds it automatically — no IP config needed. |
| **Text Injection** | Receives `{type:"inject", text:"…"}` from the tablet and types it into the focused window using the best available method (UIAutomation → SendInput → Clipboard). |
| **Focus reporting** | Watches for window/focus changes and sends `{type:"focus", app, window, method}` to the tablet so the context bar stays accurate. |
| **Canvas mirror** | Captures the active window at up to 60 fps (JPEG 70%), streams it to the tablet. Receives back stylus strokes and replays them using the Windows Pointer API. |

---

## Firewall

If Windows Firewall blocks the connection:

1. Open **Windows Defender Firewall** → *Allow an app through firewall*
2. Click *Allow another app* and browse to `publish\Inkbridge.Windows.exe`
3. Allow on **Private** networks (and Public if on a shared Wi-Fi).

Or run this once in an **elevated** PowerShell:
```powershell
New-NetFirewallRule -DisplayName "Inkbridge" -Direction Inbound -Protocol TCP -LocalPort 8765 -Action Allow
```

---

## Troubleshooting

| Symptom | Fix |
|---|---|
| Android app can't find PC | Ensure both devices are on the same subnet. Some routers block mDNS — try a hotspot. |
| Text not injecting | Run Inkbridge **as Administrator** for apps that require elevated input. |
| Screen not mirroring | Check that GPU driver supports DXGI Desktop Duplication (most modern drivers do). |
| Service crashes on start | Verify .NET 8 Desktop Runtime is installed: `dotnet --list-runtimes` |
