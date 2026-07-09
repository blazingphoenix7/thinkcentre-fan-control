# v1 CLI — on-hardware verification (M70t Gen 6)

Ran `Tcfc.Cli monitor` elevated on the reference machine (board 3376 → control enabled).

| Condition | RPM (`0x00:0x01` via C# EcReader) | Mode |
|---|---|---|
| Idle | 932–933 | Performance |
| CPU load | ~2800 | Performance |

**Result:** matches the PowerShell spike data (idle ~937–1083; load peak ~2789). The C# stack —
PawnIO P/Invoke, the ACPI EC read handshake, big-endian RPM decode, and the WMI fan-mode read —
reads the real hardware correctly, and RPM tracks load as expected. EC read path + `FanModes.Get`
verified. (`FanModes.Set` uses the same WMI call write-verified in the spike; confirmed via the
tray in the next step.)
