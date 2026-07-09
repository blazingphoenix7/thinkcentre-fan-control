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

## RESULT (2026-07-08): Path (B) is ALIVE — a real physical EC responds.
Elevated PawnIO `LpcACPIEC` probe (standard ACPI EC read handshake, RD_EC=0x80, ports 0x62/0x66):
**256/256 offsets responded** with genuine, varied data despite the stubbed ACPI EC layer.
Initial status@0x66 = `0x08`. Notable live bytes (idle):
```
0x20:  17 2B 38 34 00 46 48 17 00 00 38 00 00 00 00 31   (23,43,56,52,70,72,23,56,49 — temp block?)
0x00:  03 A9    0xF0:  13 41                              (candidate fan/RPM words)
```
**Consequence:** fine-grain control via **direct EC access through PawnIO is viable** — we talk to
the physical EC and bypass the virtualized ACPI one. Signed driver, no WinRing0.

### Next steps (revised M1)
1. **EC-diff** (read-only): capture EC across fan states (SmartFanMode 1/2/3 + a brief CPU load)
   and diff to locate the RPM readout byte(s) and any writable fan-level/mode register.
2. **Gated write test**: carefully write the candidate control register and confirm RPM responds
   (raise-first, then step down, with abort) — the real M1 write gate.
The IPF/`ipfsvc` ownership question (spec adversary) still applies once we can set a level.

## EC-diff result (2026-07-08): RPM + temps found; control register NOT yet isolated
Captured EC across SmartFanMode 1/2/3 + a 15s CPU load, diffed.
- **Fan tachometer = `0x00:0x01`, 16-bit big-endian** (`0x00`=high byte). Idle ~937–1083,
  ~1721 under load, ~1795 while still hot. Consistent across two independent runs. This is our
  live RPM readout.
- **Temperature block ≈ `0x21`–`0x2F`**: values 45–98 °C, several rising with CPU load
  (`0x23` 58→82, `0x26` 81→98, `0x2A` 61→96, `0x2F` 58→77).
- **No writable fan-level/mode register was isolated.** SmartFanMode changes (1/2/3) barely
  moved the idle fan, so no byte cleanly tracked *mode*; and under load, duty (set by IPF) is
  indistinguishable from temperature in a passive read. `GetFanSpeed` (GameZone WMI) stayed 0
  throughout (stubbed) — but the EC tach works, so we don't need it.

### Consequence
- **Monitoring (read) is proven:** real RPM + temps directly from the EC.
- **Control (write) is unconfirmed:** the writable register was not identified passively, and may
  live inside the opaque `FNSL` method (outside 0x00–0xFF EC RAM). Confirming control requires a
  **bounded write test** — the first action that writes an unknown hardware register (volatile,
  reboot-reversible, but the first with any risk). Optionally preceded by a safe finer-grained
  spin-down/time-series read diff to better isolate a candidate before writing.

## Cooldown time-series (2026-07-08): RPM confirmed; control still not separable passively
20s CPU load then EC sampled every 2s through spin-down. RPM (`0x00:0x01` be) traced a clean
curve: **1038 idle → 2789 peak → 1043**. Bytes correlating with RPM are the temperature block:
`0x26` (r=0.92, ran 82→111), `0x2F` (0.90), `0x23` (0.87), `0x2A` (0.58, fast early drop).
**Conclusion:** passive reads cannot separate fan *duty* from *temperature* (they are coupled by
the firmware fan curve). Isolating/confirming a writable control register now requires a bounded
write test. Best write candidates, in order: `0x26` (value range too high for a plain °C sensor),
`0x2A` (behavioral outlier), then `0x2F`/`0x23`. Fan tach `0x00:0x01` is the write-test feedback.

## Bounded write test (2026-07-08): candidates are read-only; EC-write control likely DEAD
Wrote each candidate to its observed load-peak value at idle (read → write → read-back → watch
RPM → restore; all originals restored). **All four did not stick** (read-back = original) and RPM
did not move: `0x23`, `0x26`, `0x2A`, `0x2F` are **read-only sensor registers**.

### Conclusion on Path (B)
Very likely **not viable**. Decisive reasoning: when IPF ramped the fan under load, the only EC
bytes that changed were the tach (`0x00:0x01`) and read-only temperature sensors — **no writable
duty byte appeared**. So the fan PWM control is not in the 256-byte ACPI-EC RAM we can reach; it
lives inside the opaque `FNSL` method / a hardware register. Caveats (small, unpursued): a hidden
"manual-mode enable + duty" register pair that stays constant in auto mode wouldn't show in a diff
and could only be found by risky blind write-probing or EC-specific docs we don't have; extended
(banked) EC space >0xFF is also unreached.

### Net capability proven on this machine
- **READ (monitoring): works.** Live fan RPM (`0x00:0x01`) + full temperature block from the EC —
  data even Lenovo's own tools report as 0 on this desktop.
- **WRITE (fine-grain control): not achieved** via direct EC. Remaining fine-grain path is (A)
  ACPI `_FSL` method evaluation (custom signed driver + must quiet IPF) — a large lift.
- **Coarse control: works** via Lenovo WMI `SetSmartFanMode` (quiet/balanced/performance).
