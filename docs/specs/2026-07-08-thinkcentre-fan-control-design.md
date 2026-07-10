# ThinkCentre Fan Control — Design Spec

> A Windows tray app that gives ThinkCentre / ThinkStation **desktop** owners the fan
> control their BIOS won't: a real **0–100% fan slider** (with live RPM), fan curves, and
> presets — driven safely from Windows, no reboot into BIOS.

- **Status:** design approved 2026-07-08; ready for implementation planning.
- **Author machine (reference target):** Lenovo ThinkCentre M70t Gen 6.
- **Repo:** `thinkcentre-fan-control` (new, standalone; unrelated to the Bitcrush project).

---

## 1. Problem & mission

Lenovo ThinkCentre desktops expose only **Auto** or **Full speed** fan behavior via the BIOS
`IntelligentCoolingPerformanceMode` menu. "Full speed" is extremely loud; "Auto" gives no
manual control. There is **no on-the-fly, fine-grained fan control** from Windows — and unlike
Lenovo *laptops* (covered by LenovoLegionToolkit and NoteBook FanControl), the ThinkCentre /
ThinkStation *desktop* line has **no working community tool**. Forum threads asking for M70q /
M70t / ThinkStation fan control go unanswered.

**Goals (in priority order):**
1. Ship a genuinely useful, polished, **shareable open-source** tray app that gives desktop
   ThinkCentre owners a real fan slider — owning the discoverable niche (stars via an
   underserved, exact-match-searchable audience).
2. Stand as an impressive engineering artifact: ACPI reverse-engineering, embedded-controller
   register I/O, modern sandboxed ring-0 access, and a rigorous safety model.

**Non-goals (v1):** Lenovo laptops (already served); GPU/OC control; Linux (future); machines
whose EC layout we have not verified (they get **read-only** monitoring, never blind writes).

---

## 2. Verified hardware findings (recon complete)

All of the following was confirmed on the reference M70t via elevated read-only probing on
2026-07-08. Raw evidence is in `docs/research/recon/`.

**Machine:** Lenovo ThinkCentre M70t Gen 6 · MT `12YH0026US` · board `LENOVO 3376` ·
BIOS `M5OKT3DA` · Intel Core Ultra 5 225 (Arrow Lake-S) · Windows 11 · iGPU only.

**Three fan-control channels exist; one is the real prize:**

| Channel | Evidence | Verdict |
|---|---|---|
| Lenovo GameZone WMI `SetSmartFanMode` | **Write-verified**: flipped mode 3→2→3, confirmed by readback | Works, but only **coarse presets**, not RPM |
| BIOS `IntelligentCoolingPerformanceMode` (WMI `Lenovo_SetBiosSetting`) | Enumerated: options `Performance / Balance / Full speed` | Writable from Windows; unlocks hidden "Full speed" but still coarse |
| **ACPI `_FSL` → EC field `FNSL`** | **`_FIF` returns FineGrainControl=1, StepSize=2** | **The real 0–100% slider (2% resolution)** |

**Key decode — the `_FIF` (Fan Information) object** returns AML
`12 07 04 | 00 | 01 | 0A 02 | 00` = Package{ Revision=0, **FineGrainControl=1**,
StepSize=2, LowSpeedNotification=0 }. Per ACPI spec, FineGrainControl=1 means the firmware
accepts **any integer 0–100** into `_FSL` — not just discrete states. This is the definitive
proof that a smooth fan slider is supported at the firmware level.

**Control path:** `_FSL` writes `\_SB.PC00.LPCB.H_EC.DPTF.FNSL` (an embedded-controller field).
`_FST` reports fan status incl. actual RPM (tach). The fan is enumerated to Windows as the
**Intel IPF Fan Participant `ACPI\INTC1063\TFN1`**, with 5 ACPI fan objects (`PNP0C0B\0..4`).

**No user-probeable Super I/O:** LibreHardwareMonitor found no Super I/O hwmon chip — fan
tach/PWM are **EC-mediated**, consistent with the `_FSL`/`FNSL` path. So control goes through
the **EC**, not a Super I/O PWM register.

**The adversary:** Intel's `ipfsvc` (Innovation Platform Framework) actively owns `TFN1` and
writes `FNSL` on its own schedule. Any value we set may be overridden unless we take ownership
(quiet the IPF fan participant while we drive; restore on exit).

**GameZone fan-curve API (`LENOVO_FAN_METHOD.SetFanZoneData`) is a stub here:**
`GetFanZoneSupportList=0`, empty zone tables, `GetCustomModeAbility=0` — the Legion-style
temp→RPM table API is present in WMI but not backed on this EC. We do **not** rely on it.

---

## 3. Feasibility verdict

**Confirmed feasible.** The firmware advertises fine-grain 0–100% fan control; the control
surface is a known EC field (`FNSL`); live RPM is readable (tach). The only real engineering
risks are (a) locating `FNSL`'s exact EC offset, (b) winning ownership from Intel IPF, and
(c) proving the hardware thermal failsafe — all addressed in Milestone 1 as an explicit gate.

---

## 4. Architecture

Four independently-testable layers, plus a reused sensor stack:

```
┌────────────────────────────────────────────────┐
│  Tray UI  (.NET 8 WinForms)                     │  slider, live RPM/temp, curve editor, presets, autostart
├────────────────────────────────────────────────┤
│  Fan Control Core  +  SAFETY WATCHDOG           │  % → EC level, thermal guard, dead-man's switch, model-guard
├────────────────────────────────────────────────┤
│  EC Access Layer   (read/write FNSL, locked)    │  ACPI EC protocol: cmd 0x80 read / 0x81 write, ports 0x62/0x66, ACPI global lock
├────────────────────────────────────────────────┤
│  PawnIO module  (thinkcentre_ec)  ── ring 0     │  the only privileged code; small, auditable Pawn bytecode
└────────────────────────────────────────────────┘
        ▲ sensor reads (CPU temp, fan tach RPM) via the PawnIO-based LibreHardwareMonitor build
```

**Driver decision — PawnIO (not WinRing0).** WinRing0 (the historic hardware-access driver) is
now flagged by Microsoft Defender as `VulnerableDriver:WinNT/Winring0` (CVE-2020-14979) and is
quarantined on end-user machines — fatal for a shareable app. As of 2025, **FanControl and
LibreHardwareMonitor both migrated to [PawnIO](https://pawnio.eu/)**, a signed, HVCI-compatible
driver that runs sandboxed ring-0 bytecode modules. We use PawnIO for the privileged EC I/O and
the **PawnIO-based LHM build** for all sensor reading (already solved and signed).

**Net-new code surface** (kept deliberately small):
- `thinkcentre_ec` PawnIO module — EC read/write with proper 0x62/0x66 handshake + global lock.
- Fan Control Core + safety layer (the heart of the correctness/safety story).
- WinForms tray UI.
- Config-driven **EC map** + model detection (for generalization).

**Why the control path is a direct EC write (not an ACPI `_FSL` call):** evaluating an ACPI
method from user mode has no clean Windows API, and PawnIO does port/MMIO I/O, not ACPI
namespace evaluation. So we write the `FNSL` byte directly at its EC offset (the same byte
`_FSL` targets) — the NoteBook-FanControl model — and manage "same value" / mode state ourselves.

---

## 5. Safety model (non-negotiable — every path fails toward *loud & safe*)

1. **Thermal watchdog (authority over the slider).** A high-priority thread polls CPU package
   temp several times/sec. Above a configurable cap (**default 85 °C**, below the ~100 °C
   throttle) it instantly overrides the user setting and ramps the fan up. The slider is a
   *request*; the watchdog is the *authority*, and cannot be disabled from the normal UI.

2. **Dead-man's switch (our process dies).**
   - *Graceful exit* (quit / Windows shutdown): restore firmware/IPF auto control — reliable.
   - *Hard kill* (Task Manager / power loss / BSOD): rely on the **EC firmware critical-temp
     failsafe** (ECs force fans up at a hardware-critical threshold regardless of the manual
     byte). **Verifying this failsafe actually fires on the M70t is a gated Milestone-1 test.**
     If absent, add a guardian helper process and/or heartbeat-revert scheme before shipping
     any quiet-fan capability.

3. **Minimum floor.** The slider clamps to a safe idle floor; reaching fan-stop (0%) requires a
   deliberate "I know what I'm doing" toggle with a plain-language warning.

4. **Refuse-to-write on unknown hardware.** EC writes happen **only** on a model whose EC map is
   in our verified config (matched on WMI board/model IDs; reference = `LENOVO 3376` / M70t Gen
   6). Unrecognized machines run **read-only** (temps + RPM, no control) until a verified map is
   contributed. This protects both users and the project's reputation.

The throughline: **every path fails toward *loud and safe*, never toward *quiet and hot*.**

### 5.5 Reversibility — no irreversible hardware damage, by construction

Owner requirement: extensive testing happens on the reference machine; nothing we do may cause
permanent damage there or on any user's system. Why each thing we touch is reversible:

- **EC register writes are volatile RAM.** `FNSL` (and any EC field) lives in the EC's RAM, not
  flash. A reboot — or worst case a full power cycle (shutdown + unplug ~30 s) — resets the EC to
  firmware defaults. Additionally we only ever write **verified offsets** from the model map
  (§5.4), one byte at a time, never scan-writes across unknown EC space.
- **CPU thermal safety is enforced in silicon beneath us.** Modern Intel CPUs throttle at Tjmax
  and hard-shutdown on thermal trip regardless of what any software does. Our watchdog (§5.1)
  keeps the machine *comfortable*; the silicon guarantees *survival*. Sustained-heat component
  aging is exactly what the watchdog cap (default 85 °C) prevents.
- **Only one BIOS setting is ever written** (`IntelligentCoolingPerformanceMode`, whitelisted),
  and it is trivially revertible via the same API or the BIOS menu. No other
  `Lenovo_SetBiosSetting` target is permitted.
- **Driver worst case is a BSOD, not damage.** A kernel-side fault reboots the machine; EC state
  resets with it. (Testing protocol: save work before M1 spike sessions — the realistic risk is
  *lost unsaved work*, and we treat it as such.)

**Forbidden operations — never, in any milestone, for any reason:**
- BIOS flashing or firmware modification (the only true "brick" vector in this domain).
- Any Lenovo password / certificate / Secure Boot / TPM WMI operation
  (`SetBiosPassword`, `Lenovo_SetBiosCertificate`, `securewipe`, …) — lockout risk.
- EC writes to offsets not present in a verified model map.
- Disabling or tampering with the CPU's own thermal protections.

---

## 6. Milestone plan

- **M0 · Decode the EC map (zero writes).** Disassemble the DSDT (`docs/research/recon/acpireg-
  DSDT*.bin`) to pin `FNSL`'s exact EC offset + width, the fan **tach** field, and the
  **auto/manual mode** byte. Desk work on already-captured data.

- **M1 · Spike — prove the write on the M70t (GATE, make-or-break).** Load PawnIO; stand up the
  `thinkcentre_ec` module; read `FNSL` live; carefully write it and confirm the **tach RPM
  moves**; confirm we can quiet `ipfsvc` and have the value **hold**; deliberately verify the
  **firmware critical-temp failsafe**. If fan responds + holds + hardware self-protects → proven,
  and the rest is software. If any fail → stop and rethink before building UI. *No later
  milestone is planned in detail until this gate passes.*

- **M2 · Control core + safety, headless.** Wrap the proven write path with all four Section-5
  guarantees; CLI-driveable for testing; prove correctness/safety before polish.

- **M3 · Tray app.** WinForms tray: live RPM + temp, the slider, curve editor, presets
  (Silent / Balanced / Max), auto-start on boot.

- **M4 · Generalize + release.** Config-driven EC maps, model auto-detection, read-only fallback
  on unknown hardware, **signed** installer, README + demo GIF, GitHub release.

---

## 7. Project specifics

- **Location:** a standalone git repo, separate from any other project.
- **Stack:** .NET 8 + WinForms; PawnIO SDK; PawnIO-based LibreHardwareMonitorLib for sensors.
- **Distribution:** signed installer + portable zip; GitHub releases; MIT (or similar) license.
- **App display name:** "ThinkCentre Fan Control".

---

## 8. Risks & open questions

- **`FNSL` offset/width** — unknown until M0 DSDT decode (captured data in hand; low risk).
- **IPF ownership** — mechanism to quiet `ipfsvc`/`TFN1` cleanly and restore it (M1). Fallback:
  out-write it at higher frequency.
- **Firmware failsafe existence** — must be verified on M70t (M1 gate). Determines whether a
  guardian process is required.
- **% → RPM mapping** — nonlinear with a minimum spin floor. The primary slider is **% duty**
  (what the firmware natively accepts); RPM is displayed live. To honor the literal "set an exact
  RPM" ask, a **closed-loop Target-RPM mode** is planned (M3+): because we read the tach *and*
  control %, a small control loop can nudge % until measured RPM matches the user's target. Viable
  only where the tach is reliable and the %→RPM curve is monotonic — validated during M1/M3.
- **PawnIO module signing/loading UX** — confirm install flow keeps "just works" on stock Win11.
- **Generalization data** — other ThinkCentre EC maps need community contribution; v1 ships the
  M70t map + a safe read-only fallback.

---

## 9. Success criteria

- **M1 gate:** on the M70t, a manual `FNSL` write visibly changes fan RPM, holds against IPF, and
  the hardware thermal failsafe is demonstrated.
- **v1 ship:** a signed tray app that gives quiet/precise fan control on the M70t with the full
  safety model, plus read-only monitoring on any other machine — no reboot, no BIOS. Control is
  primarily a % slider with live RPM; the optional closed-loop Target-RPM mode (§8) lands if M1/M3
  validate the tach and mapping.
- **Project:** discoverable, well-documented, and credibly the first working fan-control tool for
  ThinkCentre desktops.

---

## ADDENDUM (2026-07-08) — architecture revised after the M0/M1 spike

The spike (full record: `docs/research/ec-decode-m70t.md`) overturned the original control mechanism.
This addendum supersedes §4/§5.2 where they conflict.

**What the spike proved on the reference M70t:**
- `FNSL` is an ACPI **method** (not a writable EC byte); the ACPI EC is virtualized/stubbed.
- A **physical EC** answers at ports 0x62/0x66 (via PawnIO `LpcACPIEC`) and yields **live fan RPM
  (`0x00:0x01`, big-endian) + a temperature block (`0x21`–`0x2F`)** — data Vantage shows as 0.
- The fan PWM control is **not** in the reachable 256-byte EC RAM (load-correlated bytes are all
  read-only sensors). Direct-EC write control (original plan) is **dead**.
- Coarse control via Lenovo WMI `SetSmartFanMode` (quiet/balanced/performance) **works**.

**Chosen path (owner decision):** fine-grain 0–100% control via **evaluating the ACPI `_FSL`
method** with a custom **KMDF filter driver** (`IOCTL_ACPI_EVAL_METHOD`) bound to the fan
participant device, with `ipfsvc`/IPF quieted while we drive. Rationale: firmware-sanctioned,
portable, and safe across machines — the correct foundation for a *shareable* slider (blind EC
register-writing was rejected as unsafe for other users' hardware).

**Revised layered architecture:**
```
Tray UI (.NET 8 WinForms) — RPM/temp readout, mode presets, fine slider
      │  reads: PawnIO LpcACPIEC (RPM 0x00:0x01 + temps) — PROVEN
      │  coarse: Lenovo WMI SetSmartFanMode — PROVEN
      └  fine:  our KMDF driver -> IOCTL_ACPI_EVAL_METHOD "_FSL"(0..100) — TO BUILD
```

**Build sequencing:**
1. **App foundation (safe, shippable, needed regardless):** .NET tray app = EC monitoring (proven)
   + coarse WMI modes (proven). Delivers the no-BIOS noise control that was the original need.
2. **Fine-grain slider (R&D, gated):** the `_FSL` KMDF filter driver. Constraints to accept:
   test-signing mode (Secure Boot OFF) for dev, unsigned-driver installs + reboots + BSOD risk,
   attestation signing for public distribution, and quieting IPF. Feasibility is not guaranteed
   (attaching to the IPF-owned device + evaluating `_FSL` + beating IPF must be proven).
