# M0 decode finding — M70t Gen 6 fan-control surface

**Method:** disassembled all 22 captured ACPI tables (`docs/research/recon/acpireg-*.bin`)
standalone with `iasl` 20260408 (`iasl -d <table>`). Evidence: `dsdt.dsl`, `ssd9.dsl`.

## Headline: the planned "write `FNSL` byte at an EC offset" mechanism does not exist here.

### 1. `FNSL` is an ACPI *method*, not a writable EC field
`ssd9.dsl:44`:
```
External (_SB_.PC00.LPCB.H_EC.DPTF.FNSL, MethodObj)    // 3 Arguments
```
It is invoked (not stored to) by the fan set-level method `_FSL`:
```
ssd9.dsl:604   \_SB.PC00.LPCB.H_EC.DPTF.FNSL (FNID, Arg0, FSLV)   ; FanID, level(0-100), FSLV
ssd9.dsl:614   ADBG ("_FSL: FNSL not available")
```
`FNSL`'s *body* is in none of the 22 static tables (only the `External` declaration + call
sites appear). It is supplied by a **runtime-loaded DPTF/IPF table** we cannot see statically.
There is therefore **no fixed EC byte offset for `FNSL`** to write.

### 2. The ACPI embedded controller is virtualized / stubbed
- **No `OperationRegion(..., EmbeddedControl, ...)` in any of the 22 tables** (case-insensitive
  search: zero hits).
- `H_EC` (`_HID` `PNP0C09`) is a stub (`dsdt.dsl:10757+`): `_STA` returns `Zero`; its EC-read
  method `ECRD` is a `Switch` that returns `Zero` for every register; an `ECAV` ("EC available")
  name defaults to `Zero`.
- `_FST` (fan status / RPM, `ssd9.dsl:624`) has `"_FST: EC not available"` / `"GFNS not
  available"` branches — RPM read also routes through the same virtualized/DPTF path.

### 3. Consistent with the original recon
Every EC-sourced value read back empty then: `GetFanSpeed`=0, `GetSensorTemperature`=0xFFFF,
`Lenovo_DT_GetCPUFan`=0. That was not a bug — the ACPI EC surface is genuinely stubbed.

## What is still true (unchanged)
- `_FIF` reports **FineGrainControl=1, StepSize=2** — the firmware fan participant *does* accept
  0–100% fine-grain levels. The capability is real; only our *access path* to it was wrong.
- Coarse mode control via Lenovo WMI `SetSmartFanMode` **works** (write-verified in recon:
  mode 3↔2). The BIOS `IntelligentCoolingPerformanceMode` (Performance/Balance/Full speed) is
  writable from Windows.

## Implication for the approach
Fine-grain 0–100% control is exposed **only through the ACPI `_FSL` method** (which internally
calls the opaque `FNSL`). Reaching it from Windows requires one of:
- **(A) Evaluate `_FSL` as an ACPI method** — needs a Windows ACPI method-evaluation path
  (`IOCTL_ACPI_EVAL_METHOD` via a custom kernel driver bound to the ACPI fan/thermal device).
  This is NOT PawnIO EC I/O; it is a heavier, separately-signed driver.
- **(B) A real physical EC behind ports 0x62/0x66** — the ACPI layer is virtualized, but a
  physical EC *may* still exist at the hardware port level for firmware/IPF use. **Unverified.**
  If present, PawnIO's `LpcACPIEC` could read/diff EC RAM to find a fan register empirically.
- **(C) Coarse-only fallback** — ship the proven WMI mode control (quiet/balanced/full), no
  fine-grain slider.

## Open question that decides the path (cheap to answer)
Does a real EC respond at ports 0x62/0x66? A single elevated PawnIO `LpcACPIEC` read of EC RAM
`0x00–0xFF` settles it: real varied bytes ⇒ path (B) is alive; all-zero/garbage ⇒ (B) is dead,
choose between (A) and (C).
