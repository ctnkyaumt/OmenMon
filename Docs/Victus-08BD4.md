# HP Victus 08BD4 analysis and validation

This note separates findings that are proven by the supplied firmware or HP
software from behavior that still needs to be checked on the laptop.

## Inputs

- `08BD4.bin`, 16,777,216 bytes, SHA-256
  `79C6D0E47B79DF2E68ED38B91C2A2AE865362682CBD64C1EA8ECE4C64C7030D0`
- OMEN Gaming Hub MSIX `1101.2607.3.0`, SHA-256
  `F050487BD586510A7A2B4090E1A09BAD544DA523CE3396CA791EC95A22A8954F`
- ACPI tables were extracted from the Insyde image and decompiled with Intel
  ACPICA `iasl` 20260408.
- Selected managed HP assemblies were inspected with
  `tools/Disassemble-ManagedIl.ps1`.

## Firmware-confirmed behavior

### Performance mode (`0x20008`, command `0x1A`)

The 08BD4 `GC1A` handler reads input byte 1, stores only bit 0 in `OGHM`, and
notifies the platform. Consequently, `0x30` and `0x31` are the meaningful
Balanced/Performance pair on the current thermal policy. Values whose low bit
is identical are aliases on this BIOS; presenting every historical Omen mode
would imply distinctions the firmware does not implement.

Input byte 2 is accepted by HP's current software as the
`fanControlByBios` flag. The 08BD4 handler does not currently inspect that byte,
but OmenMon sends it when restoring Auto so the call also remains correct for
HP firmware that does.

### Maximum fan (`0x20008`, commands `0x26` and `0x27`)

`GC27` writes the maximum CPU/GPU fan-table values only when input byte 0 is
`1`. It has no `0`/off branch and still returns success. Therefore, calling
`SetMaxFan(false)` alone cannot restore Auto on this firmware.

HP's current client reapplies the performance/fan-control mode before sending
MaxFan off. OmenMon now follows that sequence through `RestoreAutomatic`:

1. Send command `0x1A` with the requested mode and firmware-control flag.
2. Send command `0x27` with off for compatibility with models that implement
   that branch.

### Fixed fan levels (`0x2D` and `0x2E`)

`GC2E` sends the two supplied bytes to internal EC commands `0x22` and `0x23`.
There is no firmware evidence that arbitrary flat EC addresses share those
semantics, so automatic address probing is not used.

### Keyboard (`0x20009`)

The firmware's color-table read handler reports `ZoneCount = 3` and returns
four color slots even though this Victus keyboard has one physical zone. Zone
count therefore cannot detect the physical layout. Baseboard product `8BD4`
is explicitly profiled as single-zone while the four identical color slots are
retained in the WMI payload.

HP Gaming Hub writes the color table before enabling brightness/backlight.
OmenMon now uses the same ordering; this is the likely fix for software control
requiring one initial press of the physical keyboard-light key.

## EC profile and safety boundary

OmenMon selects an EC layout from `Win32_BaseBoard.Product`:

- `8BD4`: the Victus readings found during EC monitoring (fan RPM `0x6C` and
  `0x70`; default temperature sensors `0xB0`, `0xB2`, and display-only `0xB7`).
- Other products: the original OmenMon 08A13/08A14-compatible layout.

The profile is deterministic and inspectable. It does not write test values to
unknown registers. A BIOS image can reveal ACPI/WMI handlers, but it cannot
reliably prove the meaning of every byte exposed through a runtime flat EC
window. New writable mappings should be added only after a read-only EC log and
a controlled, reversible device test.

## Laptop test checklist

Test on AC power, with HP Gaming Hub fully exited so both programs do not race.
Keep the machine idle and watch temperatures during fan tests.

1. Start OmenMon and confirm System Information reports product `8BD4`.
2. Select Max, wait for RPM to stabilize, then select Auto/Default. RPM should
   leave maximum and respond to temperature again within about 30 seconds.
3. Repeat Constant -> Auto and Fan Program -> Auto.
4. Repeat Max -> Auto/Performance, then Auto/Default. The two modes should be
   the only performance choices shown.
5. Start an EC log, perform one transition at a time, and stop with Ctrl+C or
   Enter. The absolute save path is printed and the file is checkpointed every
   ten samples.
6. With keyboard lighting initially off after a cold boot, turn it on in
   OmenMon without first pressing the physical key. Change the color and turn
   it off/on again. The GUI should show one color value and no zone dividers.
7. Record the `-Bios System`, `-Bios FanLevel`, `-Bios MaxFan`, and EC log
   outputs before and after each transition. Do not use arbitrary `-Ec ... =`
   writes while discovering registers.

Power/TGP controls are still only statically matched to the HP WMI commands;
their visible effect cannot be confirmed without the laptop and a load test.
