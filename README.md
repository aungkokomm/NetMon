# NetMon

Lightweight Network Bandwidth Monitor for Windows — inspired by [DU Meter](https://hageltech.com/dumeter).

NetMon is a minimalist, high-performance network monitoring widget designed for Windows. Inspired by the classic DU Meter, it provides real-time insights into your upload and download speeds through a sleek, unobtrusive interface. Built with C# and WinForms on .NET 8, NetMon is optimized for users who need precise data tracking without the resource overhead of traditional monitoring suites.

---
<img width="371" height="133" alt="image" src="https://github.com/user-attachments/assets/96538715-b5ee-4446-9d60-929d5e09b2d2" />

![Stars](https://img.shields.io/github/stars/aungkokomm/NetMon?style=for-the-badge&color=blue)
![Downloads](https://img.shields.io/github/downloads/aungkokomm/NetMon/total?style=for-the-badge&color=brightgreen)

## Features

- **Live speed graph** — scrolling dual-channel area chart (green = download, red = upload)
- **Badge arrows** — DU Meter-style coloured badges with ↓↑ indicators and live bps readout
- **Compact / pill mode** — collapse to a tiny speed-only strip when screen space matters
- **Usage stats** — double-click to expand a Today / Month breakdown panel
- **Monthly data cap** — set a GB limit and watch a colour-coded progress bar (green → amber → red)
- **Start with Windows** — optional HKCU Run key, toggleable from the tray menu
- **Transparency** — slide window opacity from 20 % to 100 %
- **Background colour** — pick any colour; border and separators adapt automatically
- **System tray** — right-click for the full menu; double-click to show/hide the widget
- **Window memory** — position and size are restored exactly where you left them

---

## Installation

1. Download *Latest Release* from the [Releases](../../releases/latest) page
2. Run it — **no administrator rights required** (installs to `%LocalAppData%\Programs\NetMon`)
3. Optional: tick *Start NetMon automatically when Windows starts* during setup

> The installer is built with [Inno Setup 6](https://jrsoftware.org/isinfo.php) and targets Windows 10 x64 or later.

---

## Usage

| Action | Result |
|--------|--------|
| **Drag** anywhere on the widget | Move it |
| **Drag bottom-right corner** | Resize |
| **Double-click** speed bar | Expand / collapse stats panel |
| **Double-click** pill (compact mode) | Return to full view |
| **Right-click** anywhere | Context menu |
| **Right-click** tray icon | Same context menu |
| **Double-click** tray icon | Show / hide widget |

### Tray menu options

| Option | Description |
|--------|-------------|
| Hide / Show Window | Toggle widget visibility |
| Always on Top | Keep widget above all other windows |
| Start with Windows | Add / remove HKCU startup entry |
| Change Background… | Colour picker with live preview |
| Set Transparency… | Slider 20 %–100 %, live preview, cancel to revert |
| Monthly Limit… | Set a GB cap; 0 = disabled |
| View Usage… | Full history table (daily breakdown) |
| Exit | Quit completely |

---

## Build from Source

**Requirements:** .NET 8 SDK, Windows

```bash
git clone https://github.com/aungkokomm/NetMon.git
cd NetMon

# Debug run
dotnet run --project NetMon

# Self-contained release build
dotnet publish NetMon -c Release -r win-x64 --self-contained true
```

To rebuild the installer, open `NetMon_Setup.iss` in [Inno Setup 6](https://jrsoftware.org/isinfo.php) and press **Build → Compile**, or run:

```bash
"C:\Program Files (x86)\Inno Setup 6\iscc.exe" NetMon_Setup.iss
```

Output: `installer\NetMon_Setup_v1.0.exe`

---

## Performance

NetMon is designed to have negligible impact on CPU and battery:

- **Network adapter list cached** — `GetAllNetworkInterfaces()` is called at most once every 60 seconds; between polls only the thin native `GetIPv4Statistics()` call runs
- **Partial redraws** — only the speed strip is invalidated each tick; the graph redraws itself only when there is new traffic or a scale change
- **Zero-allocation rendering** — `PointF` arrays are cached by size; no heap allocation occurs during steady-state painting
- **4 HWNDs** — speed bar and resize grip are painted directly in the form, not separate controls

---

## Settings storage

Settings are saved to `%AppData%\NetMon\settings.json` and usage history to `%AppData%\NetMon\usage.json`. Both files are created automatically on first run.

---

## License

[MIT](LICENSE)
