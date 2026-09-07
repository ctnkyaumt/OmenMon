# HP Victus 08BD4 reference audit and validation

## Inputs inspected on 2026-09-07

User-supplied files in REF_PC:

- 08BD4.bin, 16,777,216 bytes, SHA-256
  79C6D0E47B79DF2E68ED38B91C2A2AE865362682CBD64C1EA8ECE4C64C7030D0.
- OMEN Gaming Hub MSIX 1101.2608.3.0, SHA-256
  27B078A65B7476E59C276D87BE9C3F927D9A181D9BFF2B13FB7398C3401ADF68.
  This is newer than the 1101.2607.3.0 package used for the earlier changes.
- UEFIExtract A75 extracted firmware sections. Fourteen distinct ACPI tables
  passed length/checksum validation and were decompiled with ACPICA iasl
  20260408.
- ILSpyCmd 9.1.0 inspected HP.Omen.Core.Common, HP.Omen.Core.Model.Device,
  HP.Omen.Background.PerformanceControl and SystemVitals.
  Proprietary binaries and decompiled code are not included in this repository.

## Fan control

The WMITable SSDT dispatches command group 0x20008 to these handlers:

| Handler | Evidence | Consequence |
| --- | --- | --- |
| GC1A | Input byte 1 contributes only bit 0 to OGHM; then notifies NPCF | 0x30 Default/Balanced and 0x31 Performance are distinct. Other low-bit aliases are not extra modes. |
| GC26 | Compares actual fan levels with maximum fan-table values | Max readback is an RPM comparison, not an authoritative control-mode latch. |
| GC27 | Writes maximum table values only for input byte 0 = 1; no off branch | Max off alone reports success without restoring automatic control. |
| GC2D | EC commands 0x20/0x21, first two bytes of a 128-byte result | Use WMI actual fan levels, in units of 100 RPM, on this board. |
| GC2E | Passes the first two input bytes to EC commands 0x22/0x23 | Fan targets are CPU then GPU; flat EC writes are not an equivalent verified interface. |

HP's PerformanceControlHelper.SetSwFanControlLevel sends a **128-byte**
input buffer to 0x2E, not the four-byte buffer previously used by OmenMon.
Unused bytes are zero.

SetFanMode sends [255, mode, fanControlByBios, 0]. The 8BD4 handler ignores
the third byte, although other firmware may use it. HP's ApplySettings applies
the mode before MaxFan. Its software fan controller subsequently writes targets.

On the laptop, Max readback (GC26) is an instantaneous RPM threshold check
rather than a control-mode latch. While fans spin down from Max, GC26 returns 1
for up to 30 seconds. Without caching, the periodic GUI refresh immediately
rechecked Max and overturned Auto. Therefore, OmenMon maintains a cached
`LastSetMax` state on boards with RPM-based GC26 so user-commanded Auto is preserved
during deceleration.

Furthermore, on 8BD4 the ACPI AML method GC27 has no off branch (passing 0 is a
no-op), and GC1A only updates the low bit of OGHM without clearing EC target
speed registers. The EC target registers stay latched at manual speed until
explicitly updated. GC2E sets target speeds in units of 100 RPM; passing 255
corresponds to 25,500 RPM, which the EC firmware clamps to MAX speed (5800/6100 RPM).
The verified baseline automatic target is level 23 (2300 RPM), corresponding to
HP OGH `SetSwFanControlLevelManualSlider(50)` for Bigred. Calling 0x2E with [23, 23]
breaks the latch and returns fans to baseline, from where the firmware thermal curve
takes over.

OmenMon now:
1. Maintains a cached `LastSetMax` state for boards with RPM-based GC26.
2. In `RestoreAutomatic`, sets baseline targets via 0x2E with [23, 23] before
   sending the requested mode (GC1A) and disabling Max (GC27).
3. Clears `LastSetMax` and `LastSetOff` on restore so Auto mode is cleanly reported.

All GUI and fan-program exit paths use RestoreAutomatic. Choosing the same
mode in the tray also restores Auto and stops a running program. Fixed speed
is applied after mode/power notifications. The GUI does not continually reapply
mode based on an unverified Victus EC countdown, which could undo fixed speed.

Victus ignores legacy XML options requesting raw EC fan control/manual toggles.
Unknown manual/countdown registers are not accessed through these controls.
Other profiles retain their original EC behavior. WMI failures in Victus fan
target writes are surfaced rather than silently claiming success.

## Power controls

GC21 reads [CTGP, DTGP, DTCL + 1, GPSV]. GC22 consumes all four bytes,
including the final GPU temperature threshold; zero is not an omitted value.
Gaming Hub's SetTgpPpabAsync sends **87** in this field. OmenMon's GPU presets
now do the same instead of writing a 0-degree threshold.

| OmenMon preset | Custom TGP | PPAB | DState | Threshold |
| --- | --- | --- | --- | --- |
| Minimum | Off | Off | D1 | 87 C |
| Medium | On | Off | D1 | 87 C |
| Maximum | On | On | D1 | 87 C |

Raw readback/restoration retains all original bytes, including a different
threshold if the firmware supplied one. GPU power writes invalidate cached
readback so later comparisons can detect firmware changes.

These presets control TGP/PPAB flags; they do not specify fixed watts and are
not replacements for every Gaming Hub power plan. HP's Eco and Windows power
plan synchronization include additional software policy. Two firmware modes
remain exposed for 8BD4. Actual power/performance effects require load testing.

## Temperatures

Gaming Hub's SystemPerformanceHelper uses CPU package temperature from its
CPU status provider, with a CPU SDK fallback. GetGpuTemperatureV2 uses GPU
driver/SDK providers (NVAPI for NVIDIA). These are different sources from
OmenMon's flat EC sensors and can update at different rates.

The BIOS identifies CTMP, EST3, and EST2 fields. GC23 returns EST3 for
selector 1 and EST2 otherwise; it does not return CPU package temperature.
The DSDT's EC-memory offsets are not themselves proof of flat register mapping.

Retain laptop-observed EC defaults: CPU 0xB0, GPU 0xB2, display-only SYS
0xB7. There is no reference evidence proving EST2 is the SSD, so the old SSD
label is migrated to SYS. Zero/unavailable GUI readings display a dash.
No guessed offset or calibration is used to force agreement with Gaming Hub.

Configuration now stores explicit EC register addresses. Without them, the old
save/restart path changed GPU 0xB2 into global-default 0xB4. Older symbolic
names resolve after product selection; custom and BIOS sensors survive saving.

## Keyboard

The firmware reports ZoneCount 3 and four color slots even though this device
has one physical zone. Product 8BD4 remains explicitly single-zone. All slots
carry the same chosen color; color is written before enabling backlight.

The firmware reports LC04 backlight status as 0x00 when off and 0xE4 (bit 7 set)
when on. Bit 7 is checked to normalize on/off state. To match HP OGH FourZoneHelper,
backlight off sends 0x64 (100% brightness level without bit 7) rather than 0x00,
preventing brightness register zeroing. Backlight commands are written with dual
latching and user toggle clicks force hardware updates without cached-state suppression.

## EC logging and automated checks

Redirected CLI execution now initializes once without loading a second copy of
the application or assuming console cursor operations work. EC reports include
the full baseline and only complete samples. Each checkpoint is flushed to a
sibling temporary file before atomic replacement. A failed write preserves the
previous checkpoint and reports failure; cancellation detaches its handler.

The GitHub build runs Tests/Regression.cs against the compiled application:
fan protocol payload/order and failure behavior, legacy manual release, WMI
RPM, GPU presets, fresh-process sensor reload, custom/legacy sensors, fan-program
suspend/terminate restoration, partial/locked log files and redirected help.
These tests use mocks and never open hardware interfaces.

The workflow is manual/callable; pushing alone does not start it. Dispatch
OmenMon Build after pushing. No local build is required.

## Laptop checks for the new artifact

1. At idle, Max -> Auto/Default, then Max -> Auto/Performance -> Default.
   Allow approximately 30 seconds for RPM to settle; do not treat Max readback
   alone as proof of automatic mode.
2. Constant -> Auto and Fan Program -> Auto, both from the main window and tray.
   Confirm fixed targets remain steady across GUI refreshes.
3. Save settings, restart, and confirm CPU/GPU sensor addresses/readings persist.
   Compare EC versus Gaming Hub CPU-package/GPU-driver readings under the same
   workload, accounting for their different sources.
4. Check GPU preset readbacks and behavior under a representative load.
5. Start/stop an EC log with redirected output and verify a complete saved file.
6. After a cold boot, change keyboard color/on/off without pressing its key first.

Only one fan-control application should be actively changing settings during
comparisons. The automated checks do not prove real-device timing or load behavior.
