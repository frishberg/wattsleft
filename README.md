<div align="center">

<img src="docs/icon.png" alt="Watt's Left" width="88" height="88">

# Watt's Left

**See how fast your laptop is charging — or draining — live, in watts.**

[![Microsoft Store](https://img.shields.io/badge/Microsoft%20Store-Download-0067b8?logo=microsoftstore&logoColor=white)](https://apps.microsoft.com/detail/9N39TLT3SXV6)
[![Direct .exe](https://img.shields.io/badge/Direct-.exe-1e6fff)](../../releases)
[![Windows](https://img.shields.io/badge/Windows-10%20%7C%2011-0078d6?logo=windows11&logoColor=white)](#requirements)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue)](LICENSE)

[wattsleft.app](https://wattsleft.app)

<br>

<img src="docs/screenshot.png" alt="The Watt's Left window: watts in, watts out, time left, and 30-minute graphs" width="720">

</div>

Watt's Left is a free, open-source Windows app that shows, live, how many watts your
charger is putting into your laptop battery and how many watts your laptop is drawing out —
plus time left, stored energy, voltage and battery health. One small window. No account,
no ads, no tracking, and it never touches the network.

## What it shows

- **IN** — watts flowing from the charger into the battery. Higher is faster charging.
- **OUT** — watts the laptop is drawing from the battery. Lower lasts longer.
- **Time left** — stored energy divided by your average draw over the last five minutes, refreshed every thirty seconds.
- **Stored energy** (Wh), **voltage**, **health** against design capacity, and **cycle count**.
- **Thirty-minute graphs** of charge, watts in, watts out and voltage — kept across restarts.
- The **percent in the tray icon**. Hover any number for a one-sentence explanation.

## Install

- **Microsoft Store** — one click, auto-updates: [apps.microsoft.com](https://apps.microsoft.com/detail/9N39TLT3SXV6)
- **Direct installer (.exe)** — grab the latest from [Releases](../../releases)

Both carry everything they need — nothing else to install.

<a id="requirements"></a>**Requirements:** Windows 10 or 11, and a laptop with a battery.

## How it works

Windows itself only shows a percent. Watt's Left reads the battery hardware directly through
the Windows battery interface (`IOCTL_BATTERY_QUERY_STATUS`, which reports the rate in
milliwatts) four times a second, and turns it into the numbers above.

## Privacy

No network connections at all. Settings and a half hour of readings live in the app's private
folder and are removed on uninstall. See the [Privacy Policy](https://wattsleft.app/privacy/).

## Building from source

Open `BatteryChecker.csproj` in Visual Studio (or `dotnet build`) — .NET, WPF, Windows only.
The layout:

| Path | What's there |
| --- | --- |
| `Battery/` | Win32 battery reads |
| `Tray/` | tray icon — `Shell_NotifyIcon`, GDI-rendered |
| `MainWindow.*` | the window (`MainWindow.Party.cs` — nothing to see here) |
| `HoverCards.cs` | the plain-English explanations |
| `Settings.cs` | settings and autostart, packaged and unpackaged |
| `Store/` | Store artwork generators and the Inno Setup script |

## Definitely no little dancing guy

There is no setting for it. If there were, he would not dance on the charged part of your
battery bar, climb down a ladder when you switch him off, or cry when you unplug. He is not
in `MainWindow.Party.cs`. Do not look there.

## License

MIT — see [LICENSE](LICENSE). Made by [Aron Frishberg](https://aronfrishberg.com/).
