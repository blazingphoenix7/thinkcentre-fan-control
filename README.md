<div align="center">

# 🌀 ThinkCentre Fan Control

**Every tool on your Lenovo ThinkCentre desktop swears the fan runs at `0` RPM.**
### It doesn't. This reads the real speed, shows every CPU core's temperature, and switches fan modes, up to a full blast nothing else on the machine will give you.

[![License: MIT](https://img.shields.io/badge/License-MIT-14b8a6?style=flat-square)](LICENSE)
[![Platform: Windows 10/11](https://img.shields.io/badge/Windows-10_|_11-0078D6?style=flat-square&logo=windows&logoColor=white)](#install)
[![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4?style=flat-square&logo=dotnet&logoColor=white)](#build-from-source)
[![Latest release](https://img.shields.io/github/v/release/blazingphoenix7/thinkcentre-fan-control?style=flat-square&color=14b8a6)](../../releases/latest)

A small, open-source app for Lenovo **ThinkCentre and ThinkStation desktops**. It keeps the
**real fan RPM** in your tray, read straight off the embedded controller, and opens a window with
**per-core CPU temperatures** and the fan modes, including a **Full Speed** setting that Vantage
won't let you touch.

![The window taking the fan from idle up to Full Speed: live RPM, per-core CPU temperatures, and the mode selector](docs/screenshots/demo.gif)

</div>

---

## Why it exists

Open Task Manager, HWiNFO, LibreHardwareMonitor, or Lenovo's own Vantage and ask your ThinkCentre
desktop how fast its fan is spinning. They all give you the same answer:

> **`0` RPM.**

The fan is obviously spinning, so that reading is just wrong. The firmware hides the real sensor
from the OS. The actual number lives one layer down, on a physical embedded controller sitting
behind the virtualized one that Windows gets to see. This app reads it and puts it in your tray.

## What you get

- 🌀 &nbsp;**Live fan RPM.** The number nothing else on the machine will show you, in the tray and in the window, updated every second from the EC's tach register.
- 🌡️ &nbsp;**Per-core CPU temperatures.** Every core, read from the CPU's own thermal sensors and laid out as a live graph in the window. The tray also carries the hottest EC sensor, labelled just "hottest sensor" because I haven't verified which one maps to which component.
- 🎛️ &nbsp;**Fan modes, including Full Speed.** Quiet, Balanced, and Performance switch instantly through the firmware's own interface (the same one Vantage uses). **Full Speed** flips a BIOS setting for real maximum airflow, the loud one Vantage won't give you, and it engages on the next restart.
- 🪟 &nbsp;**A real window, or just the tray.** Double-click the tray icon for the full readout, or leave it minimised and hover for the RPM. Your call.
- 🪶 &nbsp;**Small and quiet.** No telemetry, no account, near-zero idle CPU and around 15 MB of memory. MIT licensed, and it never pokes the hardware with a raw write. Mode changes go through the vendor's own supported interface.

## Honest scope

> **There's no manual "set it to 1,400 RPM" slider, and I'm not going to pretend there is.**
>
> On these desktops the fine-grained fan control sits inside an opaque ACPI method that only gets
> loaded at runtime, with no register you can actually reach. I write-tested the EC directly and
> it isn't there, so the dial is out.
>
> Everything else here is real and tested on hardware: live RPM, per-core temps, the firmware's
> presets, and **Full Speed**, a genuine maximum that turned out to be writable from Windows even
> though Vantage refuses to expose it (it sets a BIOS value, so it takes a restart to kick in). No
> fake numbers, no pretend control. If you want the gory details, they're in [How it works](#how-it-works).

## Install

**You'll need** Windows 10 or 11, **Administrator** rights, and ideally a **ThinkCentre M70t Gen 6**
(the board I verified; [other boards below](#supported-hardware)).

**1. Install the PawnIO driver.**
Reading the EC needs a ring-0 driver, so the app uses [PawnIO](https://pawnio.eu/), a small,
code-signed driver. It's the same one [FanControl](https://github.com/Rem0o/FanControl.Releases)
and LibreHardwareMonitor use, and it's not the antivirus-flagged WinRing0. Download the installer
from [pawnio.eu](https://pawnio.eu/), run it, and accept the UAC prompt.

**2. Download the app.**
Grab the latest [release ZIP](../../releases/latest) and unzip it anywhere. The signed
`LpcACPIEC.bin` EC module is already bundled, so there's nothing else to download. Keep the files
together, because if you move the exe out on its own it won't find the module.

**3. Run it.**
Right-click `Tcfc.Tray.exe` and pick **Run as administrator** (a plain double-click also works,
since it asks for elevation). A small fan icon shows up in your tray, possibly hidden under the
`^` arrow. That's it.

> 💡 &nbsp;**First run:** the exe isn't code-signed, so SmartScreen might say "Windows protected
> your PC." Click **More info**, then **Run anyway**. It's fully open source, so you can read it
> or [build it yourself](#build-from-source).

## Use it

![The fan-control window: the live RPM readout, the per-core temperature graph, the four fan modes, and the Start-with-Windows toggle](docs/screenshots/dashboard.png)

- **Hover** the tray icon and the tooltip shows your live fan RPM.
- **Double-click** it to open the window: the big live RPM, a per-core temperature graph, the four fan modes, and a Start-with-Windows toggle. Closing the window drops it back to the tray, it doesn't quit.
- **Right-click** the icon for a quick menu:
  - A header line, `RPM <n>  |  hottest sensor <n> °C`, refreshed when you open it.
  - **Fan mode**, with Quiet, Balanced, and Performance. Click one to switch, and a check mark shows the current mode.
  - **Start with Windows**, which launches it at logon as an elevated scheduled task (so no UAC prompt on every boot).
  - **Exit.**

Quiet, Balanced, and Performance are the firmware's own thermal profiles. Quiet keeps the fan
calmer, Performance lets it ramp sooner, and the firmware still regulates the curve underneath.
**Full Speed** (in the window) is the exception: it forces the fan flat out through a BIOS setting,
so it takes a restart to turn on, and another to turn back off.

## Supported hardware

I've only verified this on a **ThinkCentre M70t Gen 6** (baseboard product `3376`).

| Board | Fan RPM + EC temps | Fan modes + Full Speed |
|---|---|---|
| **ThinkCentre M70t Gen 6** (`3376`) | ✅ Correct | ✅ Enabled |
| Other ThinkCentre / ThinkStation desktops | ⚠️ Uses the M70t layout, so readings **may be wrong** | 🔒 Disabled (it won't write an unverified board) |

Per-core CPU temperatures read from the processor itself, so they're correct on any Intel machine
no matter the board.

Want your model to work? The EC register layout has to be mapped per board.
[Open an issue](../../issues) with your model name and baseboard product and we can sort it out.

## How it works

The normal ACPI and WMI interfaces are a dead end. The embedded controller Windows sees is a stub:
`_STA` returns zero, every field reads zero, and the fan telemetry runs through firmware tables
that aren't even loaded most of the time. That zero is what everything else reports.

But there's a real, physical EC answering on ports `0x62` and `0x66` behind that fake one. I read
its RAM through the signed [PawnIO](https://pawnio.eu/) driver and diffed it while pushing the fan
up and down under CPU load. That located the tach, a 16-bit big-endian value at `0x00:0x01`, which
I confirmed against a full load and spin-down curve. The temperatures sit at `0x21` to `0x2F`.

The per-core CPU temperatures don't touch the EC at all. They come straight from the processor:
each core's `IA32_THERM_STATUS` MSR (read through the same PawnIO driver), minus the offset from
Tjmax. That's why they're labelled per core and not guessed at.

The presets were easy: a Lenovo WMI method (`SetSmartFanMode`) that the firmware honours, verified
on the actual board. **Full Speed** was the surprise. Lenovo's BIOS has an "Intelligent Cooling"
setting with a "Full speed" option, and that setting turns out to be writable from Windows through
Lenovo's own BIOS WMI, even though Vantage never exposes it. So the app can put the fan flat out
without you ever opening the BIOS. The catch is it's a firmware setting, so it only engages after a
restart.

The fine-grained slider is the part I couldn't ship. The write path is an ACPI method (`_FSL`
calling `FNSL`) buried in a table that only exists at runtime, and reaching it needs a signed
kernel driver plus disabling Intel's thermal service. That wasn't worth risking the hardware. The
full write-up, dead ends included, is here:

<details>
<summary><b>📖 The full reverse-engineering write-up</b></summary>

- [Design spec and decisions](docs/specs/2026-07-08-thinkcentre-fan-control-design.md): architecture, safety gates, and the pivot.
- [EC decode](docs/research/ec-decode-m70t.md): the stubbed ACPI EC, the physical EC behind it, the tach hunt, and the write-test that killed the slider.
- [Temperature labelling](docs/research/temp-labeling.md): why the sensors read "hottest sensor" instead of "CPU."
- [On-hardware verification](docs/research/v1-cli-verify.md): RPM 932 idle, about 2,800 under load, matching the probe data.

</details>

## Build from source

You'll need the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```bash
dotnet build
dotnet test tests/Tcfc.Tests
```

Run `src\Tcfc.Tray\bin\x64\Debug\net8.0-windows\Tcfc.Tray.exe` as Administrator. The build looks
for `LpcACPIEC.bin` next to the exe, then at the repo's `lib\pawnio\`, then in
`C:\Program Files\PawnIO\modules\`. Grab the signed `LpcACPIEC` module from the
[PawnIO.Modules releases](https://github.com/namazso/PawnIO.Modules/releases) if you don't have it.

There's also a console tool that builds alongside the tray (it isn't in the release ZIP),
`Tcfc.Cli.exe`, which you run from an elevated terminal:

```
Tcfc.Cli monitor                            # live RPM, the full 15-byte EC temp block, and mode
Tcfc.Cli temps                              # Tjmax and every CPU core's temperature
Tcfc.Cli mode                               # show the current and supported modes
Tcfc.Cli mode quiet|balanced|performance    # set a mode (verified board only)
```

## Troubleshooting

| Symptom | Fix |
|---|---|
| **"EC not available"** on launch | You're not running as **Administrator**, **PawnIO isn't installed**, or `LpcACPIEC.bin` isn't next to the exe (it ships in the ZIP, so keep the files together). |
| Tray shows **`- RPM`** | A read timed out, usually because another EC or fan tool is holding the EC lock (close it), or you're not elevated. |
| **Fan modes greyed out** ("monitoring only") | Your board isn't the verified `3376`, so control is gated for safety ([see above](#supported-hardware)). |
| **Full Speed didn't do anything** | It's a BIOS setting, so it only takes effect after a **restart** (and stays on until you pick another mode and restart again). |
| **"Windows protected your PC"** | Unsigned exe. Click **More info**, then **Run anyway**, or [build from source](#build-from-source). |

## License

**MIT.** Do whatever you like with it. See [LICENSE](LICENSE).

<div align="center">
<sub>Built for the ThinkCentre desktops nobody else bothered to reverse-engineer.</sub>
</div>
