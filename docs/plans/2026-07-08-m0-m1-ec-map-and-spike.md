# ThinkCentre Fan Control — M0 + M1 Plan (EC Map + Feasibility Spike)

> **For implementers:** Execute tasks in order; each ends with an independently verifiable
> deliverable and a commit. Steps use checkbox (`- [ ]`) syntax. Review at each task boundary.
> Steps marked **⚙︎ HARDWARE** are verified by direct observation against the stated expected
> result (not an automated test) — record what you observed in the named file before committing.
> Steps marked **🧪 TEST** follow strict test-first: write the failing test, see it fail,
> implement, see it pass, commit.

**Goal:** Prove — on the reference Lenovo ThinkCentre M70t Gen 6 — that we can read and safely,
reversibly *set* the fan level from Windows and observe RPM respond, decoding the exact embedded
controller (EC) register map first. This is the make-or-break feasibility gate; no product code
past M1 is planned until it passes.

**Architecture:** M0 disassembles the already-captured ACPI tables with `iasl` to find the EC
byte offsets for the fan set-level field (`FNSL`), the fan tach (RPM) field, and the fan-control
ownership mechanism; records them as versioned data. M1 installs the signed PawnIO ring-0 driver,
reuses PawnIO's official `LpcACPIEC` module to read/write EC RAM from a small elevated .NET
console harness, and runs a tightly safety-gated sequence: read → raise fan → lower fan → resolve
Intel IPF ownership → assess the hardware thermal failsafe → GO/NO-GO report.

**Tech Stack:** .NET 8 (C#) console + xUnit; `iasl` (ACPICA) for AML disassembly; PawnIO
(signed kernel driver + `PawnIOLib.dll`) with the official `LpcACPIEC` module; LibreHardwareMonitor
(PawnIO build) for CPU temperature and fan RPM sensor reads.

## Global Constraints

*(Every task implicitly includes these. Values copied from the spec, §2/§5/§5.5/§7.)*

- **Reference machine gate:** all EC writes require a positive match on WMI board/model IDs
  `LENOVO 3376` / ThinkCentre M70t Gen 6. On any non-matching machine the code must refuse to
  write (read-only). Never write an EC offset absent from a verified map.
- **Fail toward loud & safe:** the temperature watchdog is the authority over any set level. On
  temp-cap breach, lost sensor readings, or harness exit, fan level goes to **maximum / firmware
  auto**, never left low.
- **Watchdog cap:** software comfort cap default **85 °C**; the M1 harness hard-abort ceiling is
  **88 °C** (writes max fan and aborts). Never design a test that approaches dangerous temps.
- **Reversibility (spec §5.5):** only volatile EC RAM is written (reboot/power-cycle resets it),
  plus at most the single whitelisted BIOS setting `IntelligentCoolingPerformanceMode`. **Forbidden
  forever:** BIOS flashing/modification; any Lenovo password / certificate / Secure Boot / TPM WMI
  call; EC writes outside a verified map; disabling CPU thermal protections.
- **Fan control fact:** `_FIF` reports FineGrainControl=1, StepSize=2 → `FNSL` accepts an integer
  **0–100** (percent duty). Primary control is % duty; RPM is a readback.
- **Attribution rule (hard):** no AI-assistant / model / vendor name anywhere — code, comments,
  docs, filenames, commit messages — and no machine/tool co-author or attribution trailer of any
  kind. All commits authored solely by the owner's own configured git identity.
- **Elevation:** EC/driver steps require Administrator. Elevated runs are launched by the owner via
  a ready `!`-prefixed command (UAC) — never self-elevated.

---

## File structure (created across M0+M1)

- `thinkcentre-fan-control.sln` — solution root.
- `src/Tcfc.EcMap/` — class lib: EC map model + `.dsl` Field-offset reader.
  - `EcMap.cs` (data model + JSON load/save), `DslField.cs`, `DslFieldReader.cs`.
- `src/Tcfc.Spike/` — elevated console harness for M1.
  - `Program.cs` (spike commands), `PawnIoNative.cs` (P/Invoke), `EcAccess.cs` (read/write via
    module), `LpcAcpiEcCommands.cs` (real IOCTL names, filled in M1.2), `TempGuard.cs` (abort
    logic), `Sensors.cs` (LHM temp/RPM).
- `tests/Tcfc.Tests/` — xUnit: `DslFieldReaderTests.cs`, `TempGuardTests.cs`.
- `ec-maps/m70t-gen6.json` — the decoded, committed EC map.
- `docs/research/ec-decode-m70t.md` — decode evidence (quoted AML + offset arithmetic).
- `docs/research/pawnio-lpcacpiec.md` — the module's IOCTL interface notes.
- `docs/research/m1-gate-report.md` — the GO/NO-GO decision.
- Vendored, git-ignored: `lib/pawnio/`, `lib/lhm/`, `tools/iasl/`, `work/acpi/` (disassembly scratch).

---

## Milestone 0 — Decode the EC map (zero writes)

### Task M0.1: Set up repo config + tooling, disassemble the ACPI tables

**Files:**
- Modify: `.git/info/exclude` (ignore `work/`), `.gitignore` (add `tools/`, `work/`, `lib/`)
- Create: `work/acpi/` (scratch, git-ignored), commit selected `.dsl` under `docs/research/`

**Interfaces:**
- Produces: `docs/research/dsdt.dsl` (+ `ssd9.dsl`) — human-readable AML containing the EC
  `OperationRegion`/`Field` and the `_FSL`/`_FST`/`_FIF`/`_FPS` fan methods.

- [ ] **Step 1: Confirm commit identity (owner's own; no trailers)**

The repo uses the owner's already-configured git identity. Verify it, and do **not** override it:
```bash
cd "<repo-root>"
git config user.name    # -> Aaryan Mehta
git config user.email   # -> the owner's own configured commit email (leave as-is)
```
Every later `git commit` is authored by the owner with no extra flags. Never pass a `--author`,
never substitute a different email, and never add any co-author or attribution trailer.

- [ ] **Step 2: Add ignores for tooling/scratch**

Append to `.gitignore`:
```
tools/
work/
lib/
```
Append to `.git/info/exclude` (local only): `work/`.

- [ ] **Step 3: Obtain `iasl` (ACPICA disassembler)**

Preferred (no admin, pinned to repo): download the ACPICA Windows binary (`iasl.exe`) into
`tools/iasl/`. Hand the owner this command to run in their terminal (network + possible prompt):
```
! choco install iasl -y
```
If Chocolatey is unavailable, download the "iASL Compiler and Windows ACPI Tools" zip from
`https://www.intel.com/content/www/us/en/download/774881/acpi-component-architecture-downloads-windows-binary-tools.html`
(or the RehabMan `Intel-iasl` release) and extract `iasl.exe` to `tools/iasl/iasl.exe`.
Verify: `tools/iasl/iasl.exe -v` (or `iasl -v`) prints a version.

- [ ] **Step 4: Stage the captured tables with extensions iasl accepts**

The recon dumps are raw AML. Copy them into scratch with `.aml` names:
```bash
cd "<repo-root>"
mkdir -p work/acpi && cp docs/research/recon/acpireg-*.bin work/acpi/
cd work/acpi
for f in acpireg-*.bin; do mv "$f" "$(echo "$f" | sed -E 's/acpireg-([A-Z0-9]+)_.*/\1.aml/')"; done
ls
```
Expected: `DSDT.aml`, `SSD1.aml` … `SSDK.aml`, `SSDT.aml`.

- [ ] **Step 5: Disassemble with all tables as externals**

Run (from `work/acpi`):
```bash
../../tools/iasl/iasl.exe -e SSD*.aml SSDT.aml -d DSDT.aml
../../tools/iasl/iasl.exe -e DSDT.aml SSD*.aml SSDT.aml -d SSD9.aml
```
Expected: `DSDT.dsl` and `SSD9.dsl` produced (warnings about external resolution are fine).
If disassembly errors hard, retry disassembling everything together: `iasl.exe -da *.aml`.

- [ ] **Step 6: Preserve the readable evidence**

```bash
cp work/acpi/DSDT.dsl docs/research/dsdt.dsl
cp work/acpi/SSD9.dsl docs/research/ssd9.dsl
```

- [ ] **Step 7: Commit**

```bash
git add .gitignore docs/research/dsdt.dsl docs/research/ssd9.dsl
git commit -m "M0: disassemble captured ACPI tables for EC decode"
```

---

### Task M0.2: Decode FNSL, tach, and the ownership mechanism; record the map

**Files:**
- Read: `docs/research/dsdt.dsl`, `docs/research/ssd9.dsl`
- Create: `ec-maps/m70t-gen6.json`, `docs/research/ec-decode-m70t.md`

**Interfaces:**
- Produces: `ec-maps/m70t-gen6.json` with fields `board_id`, `model`, `ec_region`, `fnsl` `{offset,bits}`,
  `tach` `{offset,bits,unit}`, `ownership` `{kind, detail}`. Consumed by M0.3, M1.3, M1.5.

- [ ] **Step 1: Locate the EC OperationRegion + Field, compute FNSL's byte offset**

In `docs/research/dsdt.dsl`, find the embedded-controller device (search `H_EC`, then
`OperationRegion (.*EmbeddedControl` within it) and the `Field (…)` block(s) that declare named
bytes. Field offsets accumulate: each `Offset (0xNN)` sets the current byte; each named member
consumes its bit-width; `,  N,` entries are reserved gaps. Find `FNSL` and compute its byte
offset = last `Offset()` before it + sum of member bit-widths since, ÷ 8. Note whether `FNSL`
lives directly in the EC field or under a `DPTF`-named field group (the usage path is
`\_SB.PC00.LPCB.H_EC.DPTF.FNSL`) — if `DPTF` is an `IndexField`/nested `Field`, decode within it.
Record the arithmetic.

- [ ] **Step 2: Find the RPM source via `_FST`**

Find the `_FST` method (fan status; search `dsdt.dsl`/`ssd9.dsl`). It returns
`Package {revision, control, speed}` where `speed` reads an EC field (a tach). Identify that EC
field's name and byte offset (same offset method as Step 1). Record its width and unit (raw RPM vs
a divided value — note any arithmetic `_FST` applies, e.g. `Divide`/`Multiply`).

- [ ] **Step 3: Determine the ownership mechanism**

Read the `_FSL` method (in `ssd9.dsl`). Confirm it writes `FNSL` (and note the `FNID` fan-index
handling and the "same level, ignoring" / "FNSL not available" branches already seen in recon).
Decide the ownership model and record it as one of:
- `writer-arbitration` — no separate auto/manual selector exists; "manual" = we write `FNSL`, and
  Intel IPF (`ipfsvc`, participant `INTC1063\TFN1`) is the competing writer (expected; confirmed
  in M1.6). `detail` = which service/participant competes.
- `mode-field` — a distinct EC field toggles auto vs manual; record its name/offset/values.

- [ ] **Step 4: Read the thermal trip points (feeds the M1 failsafe assessment)**

In `dsdt.dsl`, find the thermal zone(s) (`ThermalZone`, `_TMP`, `_CRT` critical, `_HOT`, `_PSV`
passive, and any `_AC0.._ACx` active-cooling trip points) and any EC logic that forces fans at a
critical threshold. Quote them into the evidence doc — this is the primary evidence the hardware
self-protects (used in M1.7 so we never have to induce a dangerous temperature).

- [ ] **Step 5: Write the evidence doc**

Create `docs/research/ec-decode-m70t.md` with, for each of FNSL / tach / ownership / trip-points:
the quoted `.dsl` excerpt, the file+line, and the offset arithmetic. This is the auditable trail
for the numbers in the JSON map.

- [ ] **Step 6: Write the map**

Create `ec-maps/m70t-gen6.json` (example shape — fill with the real decoded values):
```json
{
  "board_id": "LENOVO 3376",
  "model": "ThinkCentre M70t Gen 6",
  "ec_region": "H_EC",
  "fnsl":  { "offset": 0, "bits": 8, "min": 0, "max": 100, "step": 2 },
  "tach":  { "offset": 0, "bits": 16, "unit": "rpm", "transform": "none" },
  "ownership": { "kind": "writer-arbitration", "detail": "ipfsvc / INTC1063 TFN1" }
}
```
(`offset`/`bits` values are the decoded ones; `0` above is a schema placeholder to replace.)

- [ ] **Step 7: Commit**

```bash
git add ec-maps/m70t-gen6.json docs/research/ec-decode-m70t.md
git commit -m "M0: decode and record M70t EC map (FNSL, tach, ownership, trip points)"
```

---

### Task M0.3: Automated consistency check for the decoded offsets

Guards against transcription error and future DSDT drift by re-deriving the offsets from the
`.dsl` and asserting they equal the committed map.

**Files:**
- Create: `thinkcentre-fan-control.sln`, `src/Tcfc.EcMap/Tcfc.EcMap.csproj`, `src/Tcfc.EcMap/EcMap.cs`,
  `src/Tcfc.EcMap/DslField.cs`, `src/Tcfc.EcMap/DslFieldReader.cs`
- Test: `tests/Tcfc.Tests/Tcfc.Tests.csproj`, `tests/Tcfc.Tests/DslFieldReaderTests.cs`

**Interfaces:**
- Produces: `DslFieldReader.FieldOffsets(string dsl, string fieldGroup)` → `IReadOnlyDictionary<string,DslField>`
  where `DslField` is `record DslField(string Name, int ByteOffset, int Bits)`.
- Consumes: `ec-maps/m70t-gen6.json` (M0.2).

- [ ] **Step 1: Scaffold solution + projects**

Run:
```bash
cd "<repo-root>"
dotnet new sln -n thinkcentre-fan-control
dotnet new classlib -n Tcfc.EcMap -o src/Tcfc.EcMap -f net8.0
dotnet new xunit -n Tcfc.Tests -o tests/Tcfc.Tests -f net8.0
dotnet sln add src/Tcfc.EcMap tests/Tcfc.Tests
dotnet add tests/Tcfc.Tests reference src/Tcfc.EcMap
del src\Tcfc.EcMap\Class1.cs 2>$null; rm -f src/Tcfc.EcMap/Class1.cs
```
Expected: `dotnet build` succeeds (empty lib + test project).

- [ ] **Step 2 🧪: Write the failing test (offset arithmetic on a synthetic Field)**

Create `tests/Tcfc.Tests/DslFieldReaderTests.cs`:
```csharp
using Tcfc.EcMap;
using Xunit;

public class DslFieldReaderTests
{
    // Minimal AML-style Field block: offsets accumulate; ",N," are reserved gaps.
    const string Sample = @"
        Field (ECF2, ByteAcc, Lock, Preserve)
        {
            Offset (0x10),
            TMP1,   8,
            ,       8,
            FTCH,   16,
            Offset (0x40),
            FNSL,   8
        }";

    [Fact]
    public void ComputesByteOffsetsFromOffsetsAndWidths()
    {
        var f = DslFieldReader.FieldOffsets(Sample, "ECF2");
        Assert.Equal(0x10, f["TMP1"].ByteOffset);
        Assert.Equal(0x12, f["FTCH"].ByteOffset);   // 0x10 + 1 (TMP1) + 1 (gap)
        Assert.Equal(16,   f["FTCH"].Bits);
        Assert.Equal(0x40, f["FNSL"].ByteOffset);    // reset by explicit Offset(0x40)
    }
}
```

- [ ] **Step 3 🧪: Run it, verify it fails**

Run: `dotnet test tests/Tcfc.Tests --filter DslFieldReaderTests`
Expected: FAIL — `DslFieldReader` does not exist.

- [ ] **Step 4 🧪: Implement `DslField` + `DslFieldReader`**

Create `src/Tcfc.EcMap/DslField.cs`:
```csharp
namespace Tcfc.EcMap;
public record DslField(string Name, int ByteOffset, int Bits);
```
Create `src/Tcfc.EcMap/DslFieldReader.cs`:
```csharp
using System.Text.RegularExpressions;
namespace Tcfc.EcMap;

public static class DslFieldReader
{
    // Parses a `Field (<group>, ...) { ... }` body, accumulating byte offsets.
    public static IReadOnlyDictionary<string, DslField> FieldOffsets(string dsl, string fieldGroup)
    {
        var open = new Regex($@"Field\s*\(\s*{Regex.Escape(fieldGroup)}\b[^)]*\)\s*\{{",
                             RegexOptions.Singleline);
        var m = open.Match(dsl);
        if (!m.Success) throw new InvalidOperationException($"Field group '{fieldGroup}' not found");

        // Take the balanced { ... } body.
        int i = m.Index + m.Length, depth = 1, start = i;
        for (; i < dsl.Length && depth > 0; i++)
            depth += dsl[i] == '{' ? 1 : dsl[i] == '}' ? -1 : 0;
        var body = dsl.Substring(start, i - start - 1);

        var result = new Dictionary<string, DslField>();
        int bitPos = 0; // bit offset from region base
        foreach (var raw in body.Split(','))
        {
            var tok = raw.Trim().TrimEnd('}').Trim();
            if (tok.Length == 0) continue;

            var off = Regex.Match(tok, @"^Offset\s*\(\s*(0x[0-9A-Fa-f]+|\d+)\s*\)$");
            if (off.Success) { bitPos = Convert.ToInt32(off.Groups[1].Value, off.Groups[1].Value.StartsWith("0x") ? 16 : 10) * 8; continue; }

            // A width-only reserved gap is a bare integer following a name+comma; handle the
            // "<name> <width>" and bare "<width>" forms that Split leaves as separate tokens.
            var named = Regex.Match(tok, @"^([A-Za-z_][A-Za-z0-9_]{0,3})\s+(\d+)$");
            var widthOnly = Regex.Match(tok, @"^(\d+)$");
            if (named.Success)
            {
                int bits = int.Parse(named.Groups[2].Value);
                result[named.Groups[1].Value] = new DslField(named.Groups[1].Value, bitPos / 8, bits);
                bitPos += bits;
            }
            else if (widthOnly.Success) { bitPos += int.Parse(widthOnly.Groups[1].Value); }
        }
        return result;
    }
}
```

- [ ] **Step 5 🧪: Run it, verify it passes**

Run: `dotnet test tests/Tcfc.Tests --filter DslFieldReaderTests`
Expected: PASS. (If the real `.dsl` uses a comment form like `Offset (0xNN)` inside the members
that this misses, extend the regex and re-run — the synthetic test locks the arithmetic contract.)

- [ ] **Step 6: Add `EcMap` load + a real-data guard test**

Create `src/Tcfc.EcMap/EcMap.cs`:
```csharp
using System.Text.Json;
namespace Tcfc.EcMap;

public sealed record RegField(int Offset, int Bits);
public sealed record EcMap(string BoardId, string Model, string EcRegion, RegField Fnsl, RegField Tach)
{
    public static EcMap Load(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var r = doc.RootElement;
        RegField F(string k) => new(r.GetProperty(k).GetProperty("offset").GetInt32(),
                                     r.GetProperty(k).GetProperty("bits").GetInt32());
        return new EcMap(r.GetProperty("board_id").GetString()!, r.GetProperty("model").GetString()!,
                         r.GetProperty("ec_region").GetString()!, F("fnsl"), F("tach"));
    }
}
```
Append to `DslFieldReaderTests.cs` a test that reads the **real** committed files and asserts they
agree (replace `H_EC`/group name with the actual EC field group found in M0.2):
```csharp
    [Fact]
    public void RealMapMatchesDisassembly()
    {
        var root = FindRepoRoot();
        var map = Tcfc.EcMap.EcMap.Load(Path.Combine(root, "ec-maps", "m70t-gen6.json"));
        var dsl = File.ReadAllText(Path.Combine(root, "docs", "research", "dsdt.dsl"));
        var fields = DslFieldReader.FieldOffsets(dsl, map.EcRegion); // EcRegion = the Field group name
        Assert.Equal(map.Fnsl.Offset, fields["FNSL"].ByteOffset);
        // tach field name from M0.2 evidence doc:
        // Assert.Equal(map.Tach.Offset, fields["<TACHNAME>"].ByteOffset);
    }

    static string FindRepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "thinkcentre-fan-control.sln"))) d = d.Parent;
        return d!.FullName;
    }
```
Uncomment/complete the tach assertion using the real field name recorded in M0.2.

- [ ] **Step 7 🧪: Run the full suite**

Run: `dotnet test tests/Tcfc.Tests`
Expected: PASS — the committed map's FNSL (and tach) offsets match the disassembly. If it fails,
the map is wrong: fix `ec-maps/m70t-gen6.json` (and the evidence doc), not the test.

- [ ] **Step 8: Commit**

```bash
git add thinkcentre-fan-control.sln src/Tcfc.EcMap tests/Tcfc.Tests
git commit -m "M0: EC-map data model + disassembly-consistency test for decoded offsets"
```

---

## Milestone 1 — Feasibility spike (GATE). Elevated, on the reference machine.

> Everything below runs as Administrator on the M70t. Before any write step, the owner saves open
> work. The realistic worst case is a reboot (BSOD) or the machine getting briefly loud, never
> hardware damage (§5.5). Each write step arms `TempGuard` first.

### Task M1.1: Install PawnIO (signed driver) + the official `LpcACPIEC` module

**Files:** vendored into git-ignored `lib/pawnio/` (driver, `PawnIOLib.dll`, `PawnIOUtil.exe`,
`LpcACPIEC.bin`).

- [ ] **Step 1: Get PawnIO + module**

Download the signed PawnIO installer from `https://pawnio.eu/` and the signed `LpcACPIEC` module
build from the `namazso/PawnIO.Modules` Releases page
(`https://github.com/namazso/PawnIO.Modules`). Place `PawnIOLib.dll` and `LpcACPIEC.bin` in
`lib/pawnio/`. Using the official **signed** module means the driver loads it without us needing
our own signing key (`PAWNIO_UNRESTRICTED` stays off).

- [ ] **Step 2 ⚙︎ HARDWARE: Install the driver (elevated)**

Hand the owner:
```
! <path>\PawnIO_setup.exe
```
Then verify the service is present/running (elevated PowerShell the owner runs, or via a `!` cmd):
```
! powershell -NoProfile -Command "sc.exe query PawnIO; (Get-Service PawnIO).Status"
```
Expected: service `PawnIO` exists and is `RUNNING` (or `STOPPED` but startable). Record the
`PawnIOLib.dll` version via `pawnio_version` in the next task.

- [ ] **Step 3: Commit (docs only — binaries are git-ignored)**

Create `docs/research/pawnio-setup.md` noting versions installed and file locations, then:
```bash
git add docs/research/pawnio-setup.md
git commit -m "M1: install PawnIO driver + official LpcACPIEC module (setup notes)"
```

---

### Task M1.2: Record the `LpcACPIEC` IOCTL interface

**Files:** Create `docs/research/pawnio-lpcacpiec.md`, `src/Tcfc.Spike/LpcAcpiEcCommands.cs`

**Interfaces:**
- Produces: `LpcAcpiEcCommands.Read` and `.Write` (exact IOCTL command strings) + the input/output
  `long[]` layout (e.g., in=`[offset]` → out=`[value]`; in=`[offset,value]` for write). Consumed by M1.3/M1.5.

- [ ] **Step 1: Read the module source for its IOCTL contract**

Open `LpcACPIEC.p` (`https://github.com/namazso/PawnIO.Modules/blob/main/LpcACPIEC.p`). Find the
`DEFINE_IOCTL*` handlers — record the exact command name strings and their input/output element
counts and meaning (offset, value, width). Also note any required `_start`/init IOCTL and whether
the EC read/write is single-byte or supports width.

- [ ] **Step 2: Encode them as constants (real values from Step 1)**

Create `src/Tcfc.Spike/LpcAcpiEcCommands.cs` — fill the strings with the actual IOCTL names read
from the module (do not invent them):
```csharp
namespace Tcfc.Spike;
public static class LpcAcpiEcCommands
{
    // Exact IOCTL command strings as defined in LpcACPIEC.p (fill from the source):
    public const string Read  = /* e.g. */ "ioctl_read";
    public const string Write = /* e.g. */ "ioctl_write";
    // Input/output layout documented in docs/research/pawnio-lpcacpiec.md.
}
```

- [ ] **Step 3: Commit**

```bash
git add docs/research/pawnio-lpcacpiec.md src/Tcfc.Spike/LpcAcpiEcCommands.cs
git commit -m "M1: record LpcACPIEC IOCTL interface (read/write command contract)"
```

---

### Task M1.3: Spike harness — prove EC READ (read-only, safe)

**Files:**
- Create: `src/Tcfc.Spike/Tcfc.Spike.csproj`, `src/Tcfc.Spike/PawnIoNative.cs`,
  `src/Tcfc.Spike/EcAccess.cs`, `src/Tcfc.Spike/Sensors.cs`, `src/Tcfc.Spike/Program.cs`
- Modify: add `Tcfc.Spike` to the solution; reference `Tcfc.EcMap`.

**Interfaces:**
- Consumes: `LpcAcpiEcCommands` (M1.2), `EcMap.Load` (M0), `PawnIOLib.dll`, LHM lib (`lib/lhm/`).
- Produces: `EcAccess.ReadByte(int offset)`, `EcAccess.ReadWord(int offset)`; `Sensors.CpuTempC()`,
  `Sensors.FanRpm()`. Consumed by M1.4/M1.5/M1.6.

- [ ] **Step 1: Scaffold the console project**

```bash
cd "<repo-root>"
dotnet new console -n Tcfc.Spike -o src/Tcfc.Spike -f net8.0
dotnet sln add src/Tcfc.Spike
dotnet add src/Tcfc.Spike reference src/Tcfc.EcMap
```

- [ ] **Step 2: P/Invoke PawnIOLib**

Create `src/Tcfc.Spike/PawnIoNative.cs`. These signatures follow PawnIO's documented API
(`pawnio_open/load/execute/close`); **confirm exact arg widths against `PawnIOLib.h` from the SDK
and LibreHardwareMonitor's PawnIO wrapper** before running, and adjust `IntPtr`/`nuint` if needed:
```csharp
using System.Runtime.InteropServices;
namespace Tcfc.Spike;

internal static class PawnIoNative
{
    const string Dll = "PawnIOLib"; // PawnIOLib.dll on PATH or beside the exe

    [DllImport(Dll)] public static extern int pawnio_version(out uint version);
    [DllImport(Dll)] public static extern int pawnio_open(out IntPtr handle);
    [DllImport(Dll)] public static extern int pawnio_load(IntPtr handle, byte[] blob, IntPtr size);
    [DllImport(Dll, CharSet = CharSet.Ansi)]
    public static extern int pawnio_execute(IntPtr handle, string name,
        long[] inArray,  IntPtr inSize,
        long[] outArray, IntPtr outSize,
        out IntPtr returnSize);
    [DllImport(Dll)] public static extern int pawnio_close(IntPtr handle);
}
```

- [ ] **Step 3: EC access wrapper (read only for now)**

Create `src/Tcfc.Spike/EcAccess.cs`:
```csharp
using System.Runtime.InteropServices;
namespace Tcfc.Spike;

public sealed class EcAccess : IDisposable
{
    readonly IntPtr _h;
    public EcAccess(string modulePath)
    {
        Check(PawnIoNative.pawnio_open(out _h), "open");
        var blob = File.ReadAllBytes(modulePath);
        Check(PawnIoNative.pawnio_load(_h, blob, (IntPtr)blob.Length), "load");
    }

    public byte ReadByte(int offset)
    {
        var inp = new long[] { offset };
        var outp = new long[1];
        Check(PawnIoNative.pawnio_execute(_h, LpcAcpiEcCommands.Read,
              inp, (IntPtr)inp.Length, outp, (IntPtr)outp.Length, out _), "read");
        return (byte)(outp[0] & 0xFF);
    }

    public int ReadWord(int offset) => ReadByte(offset) | (ReadByte(offset + 1) << 8);

    static void Check(int hr, string what)
    { if (hr != 0) throw new InvalidOperationException($"PawnIO {what} failed: 0x{hr:X8}"); }

    public void Dispose() => PawnIoNative.pawnio_close(_h);
}
```
(Adjust the in/out array layout to match the IOCTL contract recorded in M1.2 — e.g. if read takes
`[offset,width]` or returns the byte in a different slot.)

- [ ] **Step 4: Sensors via LibreHardwareMonitor**

Vendor the PawnIO-based LHM build into `lib/lhm/` (git-ignored) and reference
`LibreHardwareMonitorLib.dll`. Create `src/Tcfc.Spike/Sensors.cs` exposing `CpuTempC()` (CPU
package temperature) and `FanRpm()` (fan tach), using the same assembly-resolve pattern already
proven in recon (`docs/research/recon/lhm-enum.txt`). Keep it read-only.

- [ ] **Step 5 ⚙︎ HARDWARE: Read live and cross-check**

`Program.cs` command `read`: load `ec-maps/m70t-gen6.json`, verify the running machine matches the
map via WMI (`Win32_BaseBoard.Product` == `3376`) — **abort if not** — then print, once/second for
~15 s: `FNSL`
(`EcAccess.ReadByte(map.Fnsl.Offset)`), EC tach (`ReadWord(map.Tach.Offset)`), LHM `FanRpm()`, LHM
`CpuTempC()`. Hand the owner:
```
! powershell -NoProfile -Command "Start-Process '<repo>\src\Tcfc.Spike\bin\Debug\net8.0\Tcfc.Spike.exe' -ArgumentList 'read' -Verb RunAs"
```
Expected: FNSL reads a plausible 0–100; EC tach ≈ LHM `FanRpm()` (validates the decoded tach
offset). Record the numbers in `docs/research/m1-gate-report.md` (start the file).

- [ ] **Step 6: Commit**

```bash
git add src/Tcfc.Spike docs/research/m1-gate-report.md
git commit -m "M1: spike harness reads EC FNSL + tach live, cross-checked vs sensor RPM"
```

---

### Task M1.4: Safety watchdog (`TempGuard`) — required before any write

**Files:**
- Create: `src/Tcfc.Spike/TempGuard.cs`
- Test: `tests/Tcfc.Tests/TempGuardTests.cs`

**Interfaces:**
- Produces: `TempGuard.Decide(double? tempC, double capC)` → `enum GuardAction { Ok, RampMax }`.
  Consumed by M1.5/M1.6 (harness calls it each sample; `RampMax` ⇒ write FNSL=100 and abort).

- [ ] **Step 1 🧪: Failing test for the abort decision**

Create `tests/Tcfc.Tests/TempGuardTests.cs`:
```csharp
using Tcfc.Spike;
using Xunit;

public class TempGuardTests
{
    [Theory]
    [InlineData(70.0, GuardAction.Ok)]
    [InlineData(87.9, GuardAction.Ok)]
    [InlineData(88.0, GuardAction.RampMax)]   // hard ceiling reached
    [InlineData(95.0, GuardAction.RampMax)]
    public void RampsAtOrAboveCeiling(double temp, GuardAction expected)
        => Assert.Equal(expected, TempGuard.Decide(temp, 88.0));

    [Fact]
    public void MissingReadingFailsSafe()               // lost sensor ⇒ ramp, never trust silence
        => Assert.Equal(GuardAction.RampMax, TempGuard.Decide(null, 88.0));
}
```
Add a project reference so tests can see `Tcfc.Spike`: `dotnet add tests/Tcfc.Tests reference src/Tcfc.Spike`.

- [ ] **Step 2 🧪: Run, verify it fails**

Run: `dotnet test tests/Tcfc.Tests --filter TempGuardTests`
Expected: FAIL — `TempGuard`/`GuardAction` undefined.

- [ ] **Step 3 🧪: Implement**

Create `src/Tcfc.Spike/TempGuard.cs`:
```csharp
namespace Tcfc.Spike;
public enum GuardAction { Ok, RampMax }
public static class TempGuard
{
    // Fail toward loud & safe: no reading, or at/above the ceiling ⇒ ramp to max.
    public static GuardAction Decide(double? tempC, double capC)
        => (tempC is null || tempC.Value >= capC) ? GuardAction.RampMax : GuardAction.Ok;
}
```

- [ ] **Step 4 🧪: Run, verify it passes**

Run: `dotnet test tests/Tcfc.Tests --filter TempGuardTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Tcfc.Spike/TempGuard.cs tests/Tcfc.Tests/TempGuardTests.cs
git commit -m "M1: temperature watchdog abort logic (fail toward max fan)"
```

---

### Task M1.5: Gated WRITE test — prove the fan responds (careful, reversible)

**Files:** Modify `src/Tcfc.Spike/EcAccess.cs` (add `WriteByte`), `src/Tcfc.Spike/Program.cs` (add
`sweep` command). Update `docs/research/m1-gate-report.md`.

**Interfaces:**
- Produces: `EcAccess.WriteByte(int offset, byte value)`; a recorded FNSL→RPM response table.

- [ ] **Step 1: Add guarded `WriteByte` + reference-machine + range assertions**

In `EcAccess.cs`:
```csharp
    public void WriteByte(int offset, byte value)
    {
        var inp = new long[] { offset, value };   // match the M1.2 write IOCTL layout
        var outp = Array.Empty<long>();
        Check(PawnIoNative.pawnio_execute(_h, LpcAcpiEcCommands.Write,
              inp, (IntPtr)inp.Length, outp, (IntPtr)0, out _), "write");
    }
```
In `Program.cs` `sweep`: refuse to run unless board id matches the map; clamp any FNSL write to
`[0, 100]` (the fine-grain range from `_FIF`). Never write outside that.

- [ ] **Step 2 ⚙︎ HARDWARE: Safety pre-flight**

Owner saves all work. Confirm: an idle CPU temp reading is live; `Sensors.CpuTempC()` updates; a
manual "press any key to restore & exit" abort is wired; `TempGuard.Decide` is polled every 500 ms
and, on `RampMax`, writes `FNSL=100` and exits. Record the original `FNSL` value to restore.

- [ ] **Step 3 ⚙︎ HARDWARE: Raise first (louder = safer), confirm RPM rises**

The `sweep` command first writes `FNSL=100`, waits ~5 s, records RPM; the point of going *up*
first is that the risky direction is never entered before we've proven the write moves the fan at
all. Expected: audible fan increase and LHM `FanRpm()` clearly rises. If RPM does **not** change,
stop — control is not working; jump to M1.8 and record NO-GO (investigate ownership in M1.6 first,
since IPF may be overwriting instantly).

- [ ] **Step 4 ⚙︎ HARDWARE: Step down in stages, watch temp, record response**

With the guard armed, step `FNSL` 100 → 80 → 60 → 40 → 20 → (floor) with ~10 s dwell, logging
`(FNSL, RPM, tempC)` at each. Stop stepping down if temp climbs toward the cap (guard will force
max at 88 °C regardless). Expected: RPM falls with FNSL and rises again on the way back up —
a monotonic-ish response = fine-grained control proven. Restore original `FNSL` at the end.

- [ ] **Step 5 ⚙︎ HARDWARE: Record the response table**

Write the `(FNSL, RPM, temp)` table into `docs/research/m1-gate-report.md`. This table also tells
us whether a closed-loop Target-RPM mode (spec §8) is viable (monotonic mapping).

- [ ] **Step 6: Commit**

```bash
git add src/Tcfc.Spike docs/research/m1-gate-report.md
git commit -m "M1: gated FNSL write sweep proves fan RPM responds to control"
```

---

### Task M1.6: Resolve Intel IPF ownership

**Files:** Modify `src/Tcfc.Spike/Program.cs` (add `ownership` command). Update gate report.

- [ ] **Step 1 ⚙︎ HARDWARE: Detect override**

`ownership` command: write a distinct `FNSL` (e.g. 40), then poll it every 200 ms for 10 s. If it
drifts back toward a firmware value, Intel IPF (`ipfsvc`) is re-asserting. Record the behavior.

- [ ] **Step 2 ⚙︎ HARDWARE: Test quieting IPF (reversible)**

If overridden, hand the owner a reversible stop of the service:
```
! powershell -NoProfile -Command "Stop-Service ipfsvc -Force; Start-Sleep 1; (Get-Service ipfsvc).Status"
```
Repeat Step 1. Expected: with `ipfsvc` stopped, our `FNSL` write holds steady. **Always restart it
after:**
```
! powershell -NoProfile -Command "Start-Service ipfsvc; (Get-Service ipfsvc).Status"
```

- [ ] **Step 3 ⚙︎ HARDWARE: Record the ownership strategy for M2**

Write the verdict into the gate report: either (a) `FNSL` holds without touching IPF (best), (b)
holds only with `ipfsvc` stopped (M2 manages the service, restoring on exit), or (c) needs
continuous re-writing faster than IPF's poll. Also note whether the higher-privilege bit-level
model of §5.4 (verified-map guard) remains satisfied. Update `ec-maps/m70t-gen6.json` `ownership`
if the decode was refined.

- [ ] **Step 4: Commit**

```bash
git add src/Tcfc.Spike ec-maps/m70t-gen6.json docs/research/m1-gate-report.md
git commit -m "M1: characterize and resolve Intel IPF fan-ownership behavior"
```

---

### Task M1.7: Assess the hardware thermal failsafe (safe — no induced overheating)

**Files:** Update `docs/research/m1-gate-report.md`.

- [ ] **Step 1: Evidence from AML (primary)**

From M0.2's trip-point notes (`_CRT`, `_HOT`, active-cooling `_ACx`, and any EC critical-fan
logic), document that the firmware forces cooling at defined thresholds independent of software.
This is the primary, zero-risk evidence the machine self-protects.

- [ ] **Step 2 ⚙︎ HARDWARE: Bounded behavioral confirmation (optional, conservative)**

Only if the owner consents: with `FNSL` held low and a **mild** CPU load, observe whether the
fan ramps on its own as temp rises — with the guard's hard abort at 88 °C forcing max and exiting.
Never approach dangerous temps; the goal is to see *independent* fan ramp begin, not to test the
critical trip. If temps rise faster than the fan responds, the guard ends it — that itself is a
finding.

- [ ] **Step 3: Verdict → dead-man's-switch decision**

Record one of: **(a) confirmed** hardware failsafe → M2 may rely on it for the hard-kill case; or
**(b) inconclusive/absent** → M2 **must** ship a guardian helper process / heartbeat-revert (spec
§5.2) before any quiet-fan capability. Either outcome is acceptable for the gate; it only decides
M2's safety scope.

- [ ] **Step 4: Commit**

```bash
git add docs/research/m1-gate-report.md
git commit -m "M1: thermal-failsafe assessment and dead-man's-switch decision"
```

---

### Task M1.8: M1 GATE report — GO / NO-GO

**Files:** Finalize `docs/research/m1-gate-report.md`.

- [ ] **Step 1: Consolidate the evidence**

Summarize: EC read ✓/✗; FNSL write → RPM response ✓/✗ (attach the table); IPF ownership strategy;
failsafe verdict (a/b); reversibility confirmed (values reset on reboot; nothing outside the map or
the one whitelisted BIOS setting was touched). State any deviations from the decoded map.

- [ ] **Step 2: Decision**

Write an explicit **GO** or **NO-GO**:
- **GO** (fan responds, holds under a known ownership strategy, failsafe resolved) → M2 planning may
  begin (control core + full safety per spec §5, headless).
- **NO-GO** (no RPM response, or cannot hold control safely, or unacceptable risk) → stop; document
  what failed and the alternative to investigate (e.g. GameZone `SetFanZoneData` re-test, DPTF
  policy path, or accepting coarse preset-only control).

- [ ] **Step 3: Commit**

```bash
git add docs/research/m1-gate-report.md
git commit -m "M1 GATE: consolidated feasibility findings and GO/NO-GO decision"
```

---

## Post-M1

On **GO**, return to planning for M2 (control core + safety, headless) per spec §6 — not before.
On **NO-GO**, revisit the spec's channel table (§2) with the gate findings and choose the fallback
before writing more code.
