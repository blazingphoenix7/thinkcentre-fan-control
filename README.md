<div align="center">

# 🌀 ThinkCentre Fan Control

**Your Lenovo ThinkCentre desktop tells every tool on the planet its fan spins at `0` RPM.**
### It's lying. This reads the real number — and lets you shift gears.

[![License: MIT](https://img.shields.io/badge/License-MIT-14b8a6?style=flat-square)](LICENSE)
[![Platform: Windows 10/11](https://img.shields.io/badge/Windows-10_|_11-0078D6?style=flat-square&logo=windows&logoColor=white)](#install)
[![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4?style=flat-square&logo=dotnet&logoColor=white)](#build-from-source)
[![Latest release](https://img.shields.io/github/v/release/blazingphoenix7/thinkcentre-fan-control?style=flat-square&color=14b8a6)](../../releases/latest)

A featherweight, open-source system-tray utility that surfaces the **real fan RPM** of Lenovo
**ThinkCentre / ThinkStation desktops** — pulled straight off the embedded controller — with
one-click firmware fan modes right beside it.

![Live fan RPM climbing under load, read straight from the embedded controller](docs/screenshots/demo.gif)

</div>

---

## Why it exists

Open Task Manager, HWiNFO, LibreHardwareMonitor, even Lenovo's own Vantage, and ask your
ThinkCentre desktop how fast its fan is spinning. Every one of them answers the same thing:

> **`0` RPM.**

The fan is *right there*, audibly spinning. The reading is fake — the firmware stubs out the
sensor the OS gets to see. The real number is hiding one layer down, on a **physical embedded
controller behind the virtualized one**. This tool goes and gets it, and drops it in your tray.

<div align="center">

![The tray readout: live RPM, hottest sensor, and firmware fan modes](docs/screenshots/monitor.png)

</div>

## What you get

- 🌀 &nbsp;**Live fan RPM in your tray** — the number *nothing else on the machine will show you*, refreshed every second straight from the EC tach register.
- 🌡️ &nbsp;**Temperature readout** — the hottest live EC sensor, labelled honestly as exactly that (the per-component mapping is unverified, so it never pretends to be "CPU").
- 🎛️ &nbsp;**One-click fan modes** — Quiet / Balanced / Performance, driven through the firmware's *own* interface (the same one Vantage uses), so the curve stays firmware-regulated and safe.
- 🪶 &nbsp;**Tiny, quiet, honest** — a tray app: no telemetry, no account, ~zero idle cost. MIT-licensed, and it touches the hardware **read-only by construction**.

## Honest scope

> **There is no manual "set it to 1,400 RPM" slider — and this README won't pretend there is.**
>
> On these desktops the writable fan knob lives inside an opaque, runtime-loaded ACPI method with
> no reachable register. I write-tested the EC directly; it genuinely isn't there. So fan control
> here is **presets, not a dial.** But what *is* here — live RPM plus firmware modes — is real,
> hardware-verified, and already more than anything else on the platform gives you. The full
> autopsy, dead ends included, is in [How it works](#how-it-works).

## Install

**You'll need:** Windows 10/11, **Administrator** rights, and ideally a **ThinkCentre M70t Gen 6**
(the verified model — [other boards here](#supported-hardware)).

**1. Install the PawnIO driver.**
Reading the EC needs a ring-0 driver, so the app uses **[PawnIO](https://pawnio.eu/)** — a small,
**code-signed** driver (the same one [FanControl](https://github.com/Rem0o/FanControl.Releases)
and LibreHardwareMonitor use; *not* the antivirus-flagged WinRing0). Download the installer from
**[pawnio.eu](https://pawnio.eu/)**, run it, accept the UAC prompt.

**2. Download the app.**
Grab the latest **[release ZIP](../../releases/latest)** and unzip it anywhere. The signed
`LpcACPIEC.bin` EC module is **already bundled** — keep the files together (don't move the exe out
on its own, or it won't find the module).

**3. Run it.**
Right-click **`Tcfc.Tray.exe`** → **Run as administrator** (a plain double-click works too — it
requests elevation). A little fan icon lands in your tray, maybe under the **`^`** overflow arrow.
Done.

> 💡 &nbsp;**First run:** the exe isn't code-signed, so SmartScreen may say *"Windows protected
> your PC."* Click **More info → Run anyway** — it's fully open source, so read it or
> [build it yourself](#build-from-source).

## Use it

- **Hover** the tray icon → tooltip shows your live **fan RPM**.
- **Right-click** it for the menu:
  - **Header** — `RPM <n>  |  hottest sensor <n> °C`, live every second.
  - **Fan mode → Quiet / Balanced / Performance** — click to switch; a ✓ marks the active one.
  - **Start with Windows** — launches at logon via an elevated scheduled task (no UAC nag each boot).
  - **Exit.**

The modes select the firmware's own thermal profile — *Quiet* keeps it calmer, *Performance* lets
it ramp sooner. Presets, firmware-regulated, never a raw override.

## Supported hardware

Everything is **verified on a ThinkCentre M70t Gen 6** (baseboard product `3376`).

| Board | Monitoring | Fan modes |
|---|---|---|
| **ThinkCentre M70t Gen 6** (`3376`) | ✅ Correct | ✅ Enabled |
| Other ThinkCentre / ThinkStation desktops | ⚠️ Readings use the M70t layout — **may be wrong** | 🔒 Disabled (won't write an unverified board) |

Want your model supported? The EC register layout has to be mapped per board.
**[Open an issue](../../issues)** with your model name + baseboard product and let's work it out.

## How it works

The stock ACPI/WMI surface is a decoy. The embedded controller Windows sees is a **stub** — `_STA`
returns zero, every field reads zero, and the fan telemetry routes through firmware tables that
aren't even statically present. *That's* the `0` everyone else reports.

But a **real, physical EC** is answering on ports `0x62/0x66` behind that virtual one. Reading its
RAM through the signed [PawnIO](https://pawnio.eu/) driver — and diffing it live while forcing the
fan up and down under CPU load — pinned the **tach**: a 16-bit big-endian value at `0x00:0x01`,
confirmed against a full load → spin-down curve. Temperatures sit at `0x21..0x2F`.

Fan *modes* came almost free: a Lenovo WMI class (`SetSmartFanMode`) that the firmware honours,
write-verified on the target board.

The fine-grained slider? Chased hard, then honestly buried — the write path is an ACPI method
(`_FSL → FNSL`) hidden in a runtime-loaded table, unreachable without a signed kernel driver *and*
neutering Intel's thermal daemon. Not worth risking your hardware for. The whole trail, dead ends
and all:

<details>
<summary><b>📖 The full reverse-engineering write-up</b></summary>

- **[Design spec & decisions](docs/specs/2026-07-08-thinkcentre-fan-control-design.md)** — architecture, safety gates, the pivot.
- **[EC decode](docs/research/ec-decode-m70t.md)** — the stubbed ACPI EC, the physical EC behind it, the tach hunt, and the write-test dead end that killed the slider.
- **[Temperature labelling](docs/research/temp-labeling.md)** — why sensors read "hottest sensor," not "CPU."
- **[On-hardware verification](docs/research/v1-cli-verify.md)** — RPM 932 idle → ~2,800 under load, matching the probe data.

</details>

## Build from source

Needs the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```bash
dotnet build
dotnet test tests/Tcfc.Tests
```

Run `src\Tcfc.Tray\bin\x64\Debug\net8.0-windows\Tcfc.Tray.exe` as Administrator. The build looks
for `LpcACPIEC.bin` next to the exe, at the repo's `lib\pawnio\`, or in
`C:\Program Files\PawnIO\modules\` — grab the signed `LpcACPIEC` module from the
[PawnIO.Modules releases](https://github.com/namazso/PawnIO.Modules/releases) if you don't have it.

A scriptable console harness builds alongside the tray (not shipped in the release ZIP) —
`Tcfc.Cli.exe`, run from an elevated terminal:

```
Tcfc.Cli monitor                            # live RPM + the full 15-byte EC temp block + mode
Tcfc.Cli mode                               # show current + supported modes
Tcfc.Cli mode quiet|balanced|performance    # set a mode (verified board only)
```

## Troubleshooting

| Symptom | Fix |
|---|---|
| **"EC not available"** on launch | Not running as **Administrator**, **PawnIO not installed**, or `LpcACPIEC.bin` isn't beside the exe (it ships in the ZIP — keep the files together). |
| Tray shows **`- RPM`** | A read timed out — usually another EC/fan/monitoring tool holds the EC lock (close it), or you're not elevated. |
| **Fan modes greyed out** ("monitoring only") | Your board isn't the verified `3376`; control is gated for safety ([see above](#supported-hardware)). |
| **"Windows protected your PC"** | Unsigned exe → **More info → Run anyway**, or [build from source](#build-from-source). |

## License

**MIT** — do whatever you like with it. See [LICENSE](LICENSE).

<div align="center">
<sub>Built for the ThinkCentre desktops nobody else bothered to reverse-engineer.</sub>
</div>
