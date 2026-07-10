# EC temperature block: measured behavior and labeling decision

The EC exposes a 15-byte temperature block at offsets `0x21..0x2F` (read via the
standard RD_EC handshake, see `ec-decode-m70t.md`). Before shipping any UI that
displays these values I measured how each byte behaves from idle through a
sustained CPU load ramp on the verified board (M70t Gen 6, board `3376`).

## Measured behavior (idle -> under CPU load)

| Offset | Idle -> load | Behavior | Reading |
|--------|--------------|----------|---------|
| 0x21 | ~45 | stable | plausible C, load-insensitive |
| 0x22 | ~57-60 | stable | plausible C, load-insensitive |
| 0x23 | 58 -> 82 | tracks load | plausible C |
| 0x24 | 0 | constant | unused |
| 0x25 | ~70 | stable | plausible C, load-insensitive |
| 0x26 | 82 -> 111 | tracks load | suspect: implausibly high for a running machine; likely not a plain degrees-C temperature (offset/encoded value?) |
| 0x27 | 0 | constant | unused |
| 0x28 | 0 | constant | unused |
| 0x29 | 0 | constant | unused |
| 0x2A | 60 -> 96 | tracks load | high but possible (e.g. a hotspot/junction-style reading) |
| 0x2B | 0 | constant | unused |
| 0x2C | 0 | constant | unused |
| 0x2D | 0 | constant | unused |
| 0x2E | 0 | constant | unused |
| 0x2F | 58 -> 77 | tracks load | plausible C |

(`Temps()` additionally reports `-1` for an offset whose EC read handshake
timed out; that is a transport artifact, not a sensor value.)

## What I do not know

I have no verified mapping from these offsets to physical components. Several
bytes track CPU load, but that alone does not prove any of them is the CPU
package sensor (VRM, PCH, DIMM and airflow sensors all correlate with load
too). The ACPI tables are no help: the EC region is not declared in any static
table (see `ec-decode-m70t.md`), so there are no field names to borrow.

## Decision (v1)

1. No per-component labels. The UI never says "CPU", "GPU", etc. It says
   "hottest sensor", which is what the data actually supports.
2. Show the hottest plausible sensor: the maximum reading in the range
   (0, 100] C, implemented in `Tcfc.Core.TempSummary.Representative`.
   - `0` bytes are unused slots, skipped.
   - `-1` marks a timed-out read, skipped.
   - Values above 100 are filtered because they cannot be a plain Celsius
     temperature of a machine that is still running. This exists specifically
     for `0x26`, which reads 111 under load; whatever it encodes, displaying
     it as "111 C" would be false.
3. The CLI keeps printing the raw block unfiltered, so nothing is hidden;
   the filtering only affects the single headline value in the tray.

## Future work

A contributor with a reliable reference reading (e.g. a machine where some
other vendor-blessed interface reports labelled temperatures, or a thermal
probe) could correlate each offset against the reference across a load sweep
and turn these into named sensors. Until then I would rather show an honest
unlabeled value than a confidently wrong one. `0x26` in particular needs a
decode attempt (fixed offset from 0x23? different unit? tjunction-relative?)
before it is ever shown.
