<div align="center">

<img src="docs/icon.png" alt="Watt's Left" width="88" height="88">

# Watt's Left

**See how fast your laptop is charging or draining, live, in watts.**

<a href="https://apps.microsoft.com/detail/9N39TLT3SXV6"><img src="docs/btn-store.svg" alt="Recommended: get it from the Microsoft Store" height="54"></a>
&nbsp;
<a href="../../releases"><img src="docs/btn-exe.svg" alt="Direct: download the .exe" height="54"></a>

</div>

<br>

<table>
  <tr>
    <td valign="top" width="250">
      <img src="docs/screenshot.png" alt="The Watt's Left window: live watts in and out, time left, stored energy, voltage, health, and thirty-minute graphs" width="220">
    </td>
    <td valign="top">
      <h3>What it shows</h3>
      <ul>
        <li><b>IN</b> &nbsp;watts flowing from the charger into the battery. Higher is faster charging.</li>
        <li><b>OUT</b> &nbsp;watts the laptop is drawing from the battery. Lower lasts longer.</li>
        <li><b>Time left</b> &nbsp;stored energy divided by your average draw over the last five minutes, refreshed every thirty seconds.</li>
        <li><b>Stored energy</b> in watt-hours, <b>voltage</b>, <b>health</b> against design capacity, and <b>cycle count</b>.</li>
        <li><b>Thirty-minute graphs</b> of charge, watts in, watts out and voltage, kept across restarts.</li>
        <li>The live <b>percent in your tray icon.</b> Hover any number for a one-sentence explanation.</li>
      </ul>
    </td>
  </tr>
</table>

## Overview

Watt's Left is a free, open-source Windows app that shows, live, how many watts your charger
is putting into your laptop battery and how many watts your laptop is drawing out, plus time
left, stored energy, voltage and battery health. One small window. No account, no ads, no
tracking, and it never touches the network. More at [wattsleft.app](https://wattsleft.app).

**Requirements:** Windows 10 or 11, and a laptop with a battery. The Store package and the
installer each carry everything they need, with nothing else to set up.

## How it works

Windows itself only shows a percent. Watt's Left reads the battery hardware directly through
the Windows battery interface (`IOCTL_BATTERY_QUERY_STATUS`, which reports the rate in
milliwatts) four times a second, and turns it into the numbers above. The result is one small
window that stays out of the way, plus the live percent on your tray icon.

## Privacy

No network connections at all. Nothing is uploaded, and there is no account, no analytics,
and no ads. Your settings and the last half hour of readings live in the app's own private
folder on your machine, and they are removed when you uninstall. Full
[Privacy Policy](https://wattsleft.app/privacy/).

## What's with the little dancing guy?

I have literally no idea what you're talking about. Next question.

## License

MIT. See [LICENSE](LICENSE). Made by [Aron Frishberg](https://aronfrishberg.com/).
