# ThinkCentre Fan Control — v1 (Monitoring + Modes) Implementation Plan

> **For implementers:** execute tasks in order; each ends with an independently verifiable
> deliverable and a commit. Steps use checkbox (`- [ ]`). **🧪 TEST** steps are strict test-first.
> **⚙︎ HARDWARE** steps are verified by running on the reference M70t and observing output.

**Goal:** A safe, shippable Windows tray app for Lenovo ThinkCentre/ThinkStation desktops: live
fan RPM + temperatures (read from the physical EC via the signed PawnIO driver) and quiet/balanced/
performance fan-mode switching (Lenovo WMI). No EC writes, no custom driver, no Secure Boot changes.

**Architecture:** `Tcfc.Core` (class lib: EC reader via PawnIO P/Invoke, fan-mode controller via
WMI, machine guard, config) → `Tcfc.Cli` (console monitor/control for testing + power users) →
`Tcfc.Tray` (WinForms system-tray UI). Pure logic is unit-tested; hardware paths are verified by
running the CLI on the M70t.

**Tech stack:** .NET 8, WinForms (tray), xUnit; PawnIO `LpcACPIEC` signed module (already
installed) for EC port I/O; `root\wmi` `LENOVO_GAMEZONE_DATA` for fan modes.

## Global Constraints (copied from the spec + spike results; every task includes these)

- **Reference machine gate:** control features (mode switching) enable ONLY when
  `Win32_BaseBoard.Product == "3376"` (ThinkCentre M70t Gen 6). Other machines run monitoring-only.
- **Proven facts (verbatim):** fan tach = EC bytes `0x00` (high) `0x01` (low), 16-bit big-endian
  RPM. Temperature block ≈ EC `0x21`–`0x2F`. Fan modes via `LENOVO_GAMEZONE_DATA.SetSmartFanMode`
  (UInt32 `Data`): 1/2/3 supported (`GetSupportThermalMode`=14); current readable via
  `GetSmartFanMode`. `GetFanSpeed` WMI is stubbed (returns 0) — use the EC tach instead.
- **Safety/honesty:** never write the EC or any BIOS setting in v1 (read + mode-WMI only). Never
  label a sensor as "CPU" unless verified (Task 6). Never fake a reading.
- **EC access:** PawnIO `ioctl_pio_read`/`ioctl_pio_write` restricted to ports 0x62/0x66; standard
  ACPI EC read handshake (RD_EC=0x80); acquire `Global\Access_EC` mutex. Requires Administrator.
- **PawnIOLib signatures (from PawnIOLib.h):** `int pawnio_open(out IntPtr)`,
  `int pawnio_load(IntPtr, byte[], UIntPtr)`,
  `int pawnio_execute(IntPtr, [Ansi]string, long[], UIntPtr, long[], UIntPtr, out UIntPtr)`,
  `int pawnio_close(IntPtr)`; HRESULT 0 = success. Module: `lib/pawnio/LpcACPIEC.bin`.
- **Elevation:** the app manifests `requireAdministrator`. Naming rule: no AI/vendor names anywhere,
  no attribution trailers; commits by the owner's configured git identity.
- Reference PowerShell that already works end-to-end (port to C#): `work/ec-probe.ps1` (EC read
  protocol), `work/ec-diff.ps1` (WMI GameZone calls).

---

## Task 1: Solution scaffold + machine guard (with tests)

**Files:** `thinkcentre-fan-control.sln` (exists from M0 — reuse), `src/Tcfc.Core/Tcfc.Core.csproj`,
`src/Tcfc.Core/MachineGuard.cs`, `tests/Tcfc.Tests/MachineGuardTests.cs`

**Interfaces:**
- Produces: `MachineGuard.RpmFromBytes(int hi, int lo) -> int` (big-endian: `(hi<<8)|lo`);
  `MachineGuard.IsSupportedBoard(string? boardProduct) -> bool` — returns
  `boardProduct?.Trim() == "3376"` (the verified `Win32_BaseBoard.Product` for the M70t Gen 6).

- [ ] **Step 1: Confirm SDK + scaffold**

```bash
cd "<repo-root>"
dotnet --version    # must print an 8.x SDK; if not, stop — SDK not installed
dotnet new classlib -n Tcfc.Core -o src/Tcfc.Core -f net8.0
dotnet sln add src/Tcfc.Core
dotnet add tests/Tcfc.Tests reference src/Tcfc.Core   # Tcfc.Tests exists from M0
rm -f src/Tcfc.Core/Class1.cs
```

- [ ] **Step 2 🧪: Failing tests**

Create `tests/Tcfc.Tests/MachineGuardTests.cs`:
```csharp
using Tcfc.Core; using Xunit;
public class MachineGuardTests {
  [Theory]
  [InlineData(0x04,0x3B,1083)] [InlineData(0x06,0xB9,1721)] [InlineData(0x00,0x00,0)]
  public void RpmFromBytes_BigEndian(int hi,int lo,int rpm) => Assert.Equal(rpm, MachineGuard.RpmFromBytes(hi,lo));
  [Theory]
  [InlineData("3376",true)] [InlineData(" 3376 ",true)] [InlineData("3427",false)] [InlineData(null,false)]
  public void IsSupportedBoard_OnlyM70tGen6(string b,bool ok) => Assert.Equal(ok, MachineGuard.IsSupportedBoard(b));
}
```

- [ ] **Step 3 🧪: Run → fail** `dotnet test tests/Tcfc.Tests --filter MachineGuardTests` → FAIL (type missing).

- [ ] **Step 4 🧪: Implement**

Create `src/Tcfc.Core/MachineGuard.cs`:
```csharp
namespace Tcfc.Core;
public static class MachineGuard {
  public static int RpmFromBytes(int hi, int lo) => ((hi & 0xFF) << 8) | (lo & 0xFF);
  public static bool IsSupportedBoard(string? boardProduct) => boardProduct?.Trim() == "3376";
}
```

- [ ] **Step 5 🧪: Run → pass.** `dotnet test tests/Tcfc.Tests --filter MachineGuardTests` → PASS.

- [ ] **Step 6: Commit** `git add -A && git commit -m "v1: core scaffold + machine guard (RPM decode, board gate)"`

---

## Task 2: EC reader (PawnIO P/Invoke + ACPI EC read protocol)

**Files:** `src/Tcfc.Core/PawnIoNative.cs`, `src/Tcfc.Core/EcReader.cs`

**Interfaces:**
- Consumes: `MachineGuard.RpmFromBytes`; `lib/pawnio/LpcACPIEC.bin`; PawnIOLib.dll.
- Produces: `EcReader` (IDisposable): `int ReadByte(int off)`, `int Rpm()` (uses 0x00/0x01),
  `int[] Temps()` (offsets 0x21..0x2F). Throws `EcUnavailableException` if PawnIO/module/EC absent.

- [ ] **Step 1: P/Invoke** — create `PawnIoNative.cs` with the five `[DllImport("PawnIOLib.dll")]`
  signatures from Global Constraints (add `[DllImport("kernel32")] SetDllDirectory` and call it with
  the PawnIO dir `C:\Program Files\PawnIO` before first use).

- [ ] **Step 2: EcReader** — create `EcReader.cs` porting `work/ec-probe.ps1` exactly:
  ports `SC=0x66,DAT=0x62`, `OBF=1,IBF=2,RD=0x80`; `ioctl_pio_read`/`ioctl_pio_write` via
  `pawnio_execute`; `WaitFlag`, `ReadByte(off)` = RD_EC handshake; acquire `Global\Access_EC` mutex
  in the ctor, release on Dispose. `Rpm()` = `MachineGuard.RpmFromBytes(ReadByte(0),ReadByte(1))`.
  `Temps()` = `Enumerable.Range(0x21,0x0F).Select(ReadByte).ToArray()`.

- [ ] **Step 3 ⚙︎ HARDWARE: prove it** — add a temporary `Main` or a test-console that news up
  `EcReader`, prints `Rpm()` + `Temps()`. Hand the owner:
  `! powershell -NoProfile -Command "Start-Process '<repo>\src\Tcfc.Core\bin\Debug\net8.0\...' -Verb RunAs"`
  (or run via the CLI in Task 4). Expected: RPM ~900–1500 idle; temps in the 40–90 range. Record.

- [ ] **Step 4: Commit** `git commit -m "v1: EC reader (PawnIO) - live RPM + temperature block"`

---

## Task 3: Fan-mode controller (Lenovo WMI)

**Files:** `src/Tcfc.Core/FanModes.cs`, `tests/Tcfc.Tests/FanModeTests.cs`

**Interfaces:**
- Produces: `enum FanMode { Quiet=1, Balanced=2, Performance=3 }`;
  `FanModes.Get() -> FanMode`, `FanModes.Set(FanMode)`, `FanModes.SupportedFromMask(int mask) -> FanMode[]`.

- [ ] **Step 1 🧪: Failing test** for `SupportedFromMask` (pure logic):
```csharp
using Tcfc.Core; using Xunit;
public class FanModeTests {
  [Fact] public void Mask14_Gives123() =>
    Assert.Equal(new[]{FanMode.Quiet,FanMode.Balanced,FanMode.Performance}, FanModes.SupportedFromMask(14));
}
```
(14 = 0b1110 → bits 1,2,3 set.)

- [ ] **Step 2 🧪: Run → fail.**

- [ ] **Step 3: Implement** `FanModes.cs`: `SupportedFromMask(mask)` returns each `FanMode` whose
  bit is set (`(mask >> (int)m) & 1`). `Get()`/`Set()` call `LENOVO_GAMEZONE_DATA`
  `GetSmartFanMode`/`SetSmartFanMode` via `Invoke-CimMethod` equivalent
  (`ManagementObject`/`CimSession` on `root/wmi`, arg `Data`=(uint)mode). Port from `work/ec-diff.ps1`.

- [ ] **Step 4 🧪: Run → pass** (unit test). **⚙︎ HARDWARE:** via CLI in Task 4, verify `Get()` then
  `Set(Balanced)` then `Get()` reflects 2, then restore.

- [ ] **Step 5: Commit** `git commit -m "v1: fan-mode controller (WMI SetSmartFanMode)"`

---

## Task 4: CLI (end-to-end monitor + control on the machine)

**Files:** `src/Tcfc.Cli/Tcfc.Cli.csproj`, `src/Tcfc.Cli/Program.cs`, `src/Tcfc.Cli/app.manifest`

**Interfaces:** Consumes `EcReader`, `FanModes`, `MachineGuard`.

- [ ] **Step 1: Scaffold + manifest** — `dotnet new console -n Tcfc.Cli ...`; add
  `app.manifest` with `requireAdministrator`; reference `Tcfc.Core`.

- [ ] **Step 2: Commands** — `Program.cs`:
  - `monitor`: loop every 1s printing `RPM=… temps=[…] mode=…` until keypress.
  - `mode <quiet|balanced|performance>`: guard on board id (refuse + message if unsupported), then `FanModes.Set`.
  Board guard uses `Win32_BaseBoard.Product` + `MachineGuard.IsSupportedBoard`.

- [ ] **Step 3 ⚙︎ HARDWARE: full end-to-end** — hand owner the elevated run; verify `monitor` shows
  live RPM/temps, and `mode balanced`/`mode performance` change `GetSmartFanMode` (and are audible
  under load). Record in `docs/research/v1-cli-verify.md`.

- [ ] **Step 4: Commit** `git commit -m "v1: CLI monitor + mode control, verified end-to-end"`

---

## Task 5: WinForms tray app (the shippable UI)

**Files:** `src/Tcfc.Tray/Tcfc.Tray.csproj` (`<UseWindowsForms>true</UseWindowsForms>`, `net8.0-windows`),
`src/Tcfc.Tray/TrayApp.cs`, `src/Tcfc.Tray/app.manifest`

- [ ] **Step 1: Tray skeleton** — `NotifyIcon` + `ContextMenuStrip`; a `System.Windows.Forms.Timer`
  (1s) polling `EcReader.Rpm()` and updating the tray tooltip/text to `"<RPM> RPM"`. Manifest
  `requireAdministrator`. (Timers are cosmetic; reads are synchronous in the tick handler.)

- [ ] **Step 2: Menu** — context menu: live "RPM: … · Temp: …" (disabled label, updated each tick);
  a "Fan mode" submenu (Quiet/Balanced/Performance) with a check on the current mode — enabled only
  if `IsSupportedBoard`, else a disabled "Monitoring only (unsupported model)" item; "Start with
  Windows" toggle (HKCU `...\Run`); "Exit".

- [ ] **Step 3 ⚙︎ HARDWARE:** run elevated; verify tray shows live RPM, menu switches modes, autostart
  toggle writes/removes the Run key. Record.

- [ ] **Step 4: Commit** `git commit -m "v1: WinForms tray app - live RPM + mode presets"`

---

## Task 6: Honest temperature labeling + README + release polish

**Files:** `docs/research/temp-labeling.md`, `README.md`, `src/Tcfc.Core/EcMap` (labels)

- [ ] **Step 1 ⚙︎ HARDWARE: characterize temps honestly** — with the CLI `monitor` running, apply a
  brief CPU load; note which `0x21`–`0x2F` bytes track load (candidate CPU/package) vs stay flat.
  Only assign a human label ("CPU/package", "System") to a byte if its behavior justifies it;
  otherwise present as "Sensor @0xNN". Record reasoning in `docs/research/temp-labeling.md` and set
  labels accordingly. **Do not invent labels.**

- [ ] **Step 2: README** — what it is (first fan/thermal tool for ThinkCentre desktops), the honest
  capability (live RPM+temps + mode presets; slider not possible via safe means — link the spike
  writeup), install (needs PawnIO), supported model + how to contribute others, screenshots/GIF.
  No AI/vendor names.

- [ ] **Step 3: Commit** `git commit -m "v1: honest temp labels + README"`

---

## Self-review checklist (run after writing tasks)
- Every proven fact used with exact values (RPM bytes, board id 3376, mask 14, mode enum). ✅
- No EC/BIOS writes anywhere in v1. ✅
- Temp labeling gated on verification (Task 6). ✅
- Hardware steps are observational; pure logic is unit-tested. ✅
