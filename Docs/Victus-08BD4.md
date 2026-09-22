# HP Victus 08BD4 reference audit and validation

## Inputs inspected on 2026-09-07 and 2026-09-14/15

User-supplied files in REF_PC:

- 08BD4.bin, 16,777,216 bytes, SHA-256
  79C6D0E47B79DF2E68ED38B91C2A2AE865362682CBD64C1EA8ECE4C64C7030D0.
- OMEN Gaming Hub MSIX 1101.2608.3.0, SHA-256
  27B078A65B7476E59C276D87BE9C3F927D9A181D9BFF2B13FB7398C3401ADF68.
  This is newer than the 1101.2607.3.0 package used for the earlier changes.
- UEFIExtract A75 extracted firmware sections. The repeat scan found 78
  distinct checksum-valid ACPI tables/variants; the WMITable SSDT and DSDT
  were decompiled with ACPICA iasl.
- ILSpyCmd 9.1.0 inspected HP.Omen.Core.Common, HP.Omen.Core.Model.Device,
  HP.Omen.Background.PerformanceControl and SystemVitals.
  Proprietary binaries and decompiled code are not included in this repository.

## Fan control

The WMITable SSDT dispatches command group 0x20008 to these handlers:

| Handler | Evidence | Consequence |
| --- | --- | --- |
| GC10 | Sends EC command 0x45/0xA0 while returning fan count | This is an OEM-control heartbeat, not a passive query. |
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

The earlier claim that [23, 23] releases Auto was incorrect: it requests a fixed
2300/2300 RPM target. Likewise, [255, 255] is not a release sentinel; this machine
clamps it to maximum speed. The GUI's unconditional 30-second GC10 heartbeat
kept the OEM session alive and prevented firmware recovery.

Live AC tests show that stopping GC10 allows native fan control to resume about
120 seconds after the last heartbeat. Both fixed -> Default and Max -> Performance
recovered. Performance alone, with no heartbeat or target writes, did not force
maximum fans. A subsequent 70-second Python CPU load raised native fan levels
from 0/0 to 40/43, followed by cooling down to 23/0 and 23/26. Values are 100 RPM.

OmenMon now sends the heartbeat when acquiring fixed/Max/Off/program control,
renews it only while that control is active, and stops renewal for Auto and exit.
Auto applies the requested firmware mode without writing a fixed fan target.
Off -> Auto first raises cooling to Max while waiting for the session to expire.
The displayed countdown estimates 120 seconds on AC or 600 seconds on battery
since the last successful local heartbeat. The power source is captured at that
heartbeat. This is not an EC register or proof of firmware state. Keyboard
writes also require GC10 and restart that wait.

A battery trial on BIOS F.29 started from native Auto (fans 0/0, EC `AB=08`),
sent one GC10 heartbeat and a 3500/3500 RPM manual target, then requested
Default/Auto after 15 seconds. With no further writes, fans stayed near 3500/3500
and `AB=00` through 601 seconds from the heartbeat. At the next five-second
sample, 606 seconds from the heartbeat, fans were 0/0 and `AB=08`; battery
status stayed on DC throughout. A repeat with the exact CI artifact on
2026-09-23, using WMI fan and temperature reads but no EC reads, stayed at
3500/3500 RPM through 834 seconds from the heartbeat and first showed 0/0 at
839 seconds. Two subsequent samples confirmed 0/0 at about 30 C. Both
trials confirm delayed native idle handback on battery, with unexplained
timing variation. The 600-second countdown tracks only this app's last local
heartbeat and can reach zero before firmware releases control; other clients
may also extend the wait. HP's software
fan controller is a different policy from the firmware's native curve. Its
manual slider midpoint is not an Auto API.

After the 2026-09-23 idle handback, an eight-worker CPU load ran for about three minutes
on battery. CPU temperature peaked at 48 C and the fans stayed at 0/0. The
load did not reach the native fan threshold, so this trial does not verify the
battery thermal fan curve. No safety guard or manual fan write was needed.

The same artifact then entered Max from native idle at 41 C on battery and
requested Auto/Performance after 15 seconds. Fans held near 5800/6100 RPM
through 1002 seconds after Max was requested, while CPU cooled to 27 C and
battery remained connected. The bounded trial ended without a measured Max
handback; requesting Default on exit did not itself prove release. Max -> Auto
on battery therefore remains unverified. Do not infer recovery from the
countdown or from the application's selected Auto label.

The upstream Linux `hp-wmi` Victus-S Auto path sends a zero/zero fan target
and a GC10 heartbeat ([source](https://github.com/torvalds/linux/blob/master/drivers/platform/x86/hp/hp-wmi.c)).
That sequence is not a safe release on this F.29 8BD4: the local zero-target
trial stopped both fans while CPU temperature rose to 74 C, before the guarded
load was stopped and Max cooling restored. OmenCore also reports that zero
targets can leave other Victus fans in manual zero mode
([report](https://github.com/theantipopau/omencore/blob/main/docs/CHANGELOG_v2.8.1.md)).
Do not substitute a zero target for the measured watchdog handback on this board.

The Performance label disappearing was a separate UI defect. Value 0x31 also
has enum aliases Turbo and L7; Enum.GetName returned Turbo, which was absent from
the two-item Victus dropdown. GUI and tray now resolve the numeric mode against
the profile's supported names, preserving Performance during refresh.

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
when on. Bit 7 normalizes on/off state. Off still sends HP's 0x64 value, but the
earlier brightness-preservation explanation was incorrect: LC05 sends zero
brightness whenever bit 7 is clear, whether input is 0x00 or 0x64.

After a cold boot, one backlight-on command returned success but left readback
at zero; the user confirmed the lights stayed off. After one GC10 heartbeat,
one identical command returned 0xE4 and the user confirmed physical illumination.
The old GUI first sent GC10 at its 30-second timer tick, explaining why repeated
clicks appeared to fix startup.

Keyboard writes now acquire access on demand before changing color or backlight.
The backlight setter waits 100 ms for readback and retries at most three times;
failure is reported and the GUI reloads actual state. Blind double writes were
removed. Color updates preserve the existing 128-byte buffer and replace only
RGB bytes 25..36, as HP's client does. BIOS calls are serialized and WMI result
objects remain alive until output data has been read.

## EC logging and automated checks

Redirected CLI execution now initializes once without loading a second copy of
the application or assuming console cursor operations work. EC reports include
the full baseline and only complete samples. Each checkpoint is flushed to a
sibling temporary file before atomic replacement. A failed write preserves the
previous checkpoint and reports failure; cancellation detaches its handler.

The GitHub build runs Tests/Regression.cs against the compiled application:
fan protocol payload/order and failure behavior, heartbeat renewal/release,
canonical dropdown labels, keyboard handshake/retries/metadata, legacy manual release, WMI
RPM, GPU presets, fresh-process sensor reload, custom/legacy sensors, fan-program
suspend/terminate restoration, partial/locked log files and redirected help.
These tests use mocks and never open hardware interfaces.

The workflow is manual/callable; pushing alone does not start it. Dispatch
OmenMon Build after pushing. No local build is required.

## Laptop checks for the new artifact

1. At idle, Max -> Auto/Default, then Max -> Auto/Performance -> Default.
   On AC allow about 120 seconds. On battery, the 2026-09-23 Max trial stayed
   latched beyond 16 minutes, so further observation is needed before calling
   this path verified. Check response under load after RPM returns to native
   idle. Max readback or an expired UI countdown alone does not prove Auto.
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
