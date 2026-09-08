# Watt's Left

Watt's Left is a free, open-source Windows app that shows, live, how many watts your charger is putting into your laptop battery and how many watts your laptop is drawing out. Plus real time left, stored energy, voltage and battery health. One small window. No account, no ads, no tracking, and it never touches the network.

**Website:** https://wattsleft.app · **Microsoft Store:** https://apps.microsoft.com/detail/9N39TLT3SXV6 · **Direct .exe:** see [Releases](../../releases)

![Watt's Left](https://wattsleft.app/img/app.png)

## What it shows

- **IN** – watts flowing from the charger into the battery. Higher is faster charging.
- **OUT** – watts the laptop is drawing from the battery. Lower lasts longer.
- **Time left** – stored energy divided by your average draw over the last five minutes, refreshed every thirty seconds.
- Stored energy (Wh), voltage, health against design capacity, cycle count.
- Thirty-minute graphs of charge, watts in, watts out and voltage, kept across restarts.
- The percent in the tray icon. Hover any number for a one-sentence explanation.

It reads the battery hardware directly through the Windows battery interface (`IOCTL_BATTERY_QUERY_STATUS`, which reports the rate in milliwatts) four times a second. Windows itself only shows a percent.

## Requirements

Windows 10 or 11. A laptop with a battery. Nothing else to install: the Store package and the installer carry everything they need.

## Layout

- `Battery/` – Win32 battery reads
- `Tray/` – tray icon (Shell_NotifyIcon, GDI-rendered)
- `MainWindow.*` – the window; `MainWindow.Party.cs` – nothing to see here
- `HoverCards.cs` – the plain-English explanations
- `Settings.cs` – settings and autostart, packaged and unpackaged
- `Store/` – Store artwork generators and the Inno Setup script

## Definitely no little dancing guy

There is no setting for it. If there were, he would not dance on the charged part of your battery bar, climb down a ladder when you switch him off, or cry when you unplug. He is not in `MainWindow.Party.cs`. Do not look there.

## Privacy

No network connections at all. Settings and a half hour of readings live in the app's private folder and are removed on uninstall. Privacy Policy: https://wattsleft.app/privacy/

## License

MIT. See [LICENSE](LICENSE).

Made by [Aron Frishberg](https://aronfrishberg.com/).
