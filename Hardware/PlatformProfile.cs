  //\\   OmenMon: Hardware Monitoring & Control Utility
 //  \\  Copyright © 2023-2024 Piotr Szczepański * License: GPL3
     //  https://omenmon.github.io/

using System;
using OmenMon.Hardware.Bios;

namespace OmenMon.Hardware.Platform {

    // EC layouts are model-specific.  Keeping them out of a global register
    // enum prevents a Victus adjustment from silently changing every Omen.
    public sealed class EcRegisterProfile {
        public byte Fan0Level, Fan1Level;
        public byte Fan0Rate, Fan1Rate;
        public byte Fan0RateSet, Fan1RateSet;
        public byte Fan0Rpm, Fan1Rpm;
        public byte FanCountdown, FanManual, FanMode, FanSwitch;

        public EcRegisterProfile(
            byte fan0Level, byte fan1Level,
            byte fan0Rate, byte fan1Rate,
            byte fan0RateSet, byte fan1RateSet,
            byte fan0Rpm, byte fan1Rpm,
            byte fanCountdown, byte fanManual, byte fanMode, byte fanSwitch) {

            Fan0Level = fan0Level;
            Fan1Level = fan1Level;
            Fan0Rate = fan0Rate;
            Fan1Rate = fan1Rate;
            Fan0RateSet = fan0RateSet;
            Fan1RateSet = fan1RateSet;
            Fan0Rpm = fan0Rpm;
            Fan1Rpm = fan1Rpm;
            FanCountdown = fanCountdown;
            FanManual = fanManual;
            FanMode = fanMode;
            FanSwitch = fanSwitch;
        }
    }

    public sealed class TemperatureProfile {
        public string Name;
        public byte Register;
        public bool Use;

        public TemperatureProfile(string name, byte register, bool use = true) {
            Name = name;
            Register = register;
            Use = use;
        }
    }

    // Describes only behavior that is safe to infer from a stable system ID.
    // Unknown writable EC registers are never probed automatically.
    public sealed class PlatformProfile {
        public string Name { get; private set; }
        public int KeyboardZoneCount { get; private set; }
        public string[] FanModeNames { get; private set; }
        public EcRegisterProfile Ec { get; private set; }
        public TemperatureProfile[] Temperature { get; private set; }

        public bool IsSingleZoneKeyboard {
            get { return KeyboardZoneCount == 1; }
        }

        private PlatformProfile(
            string name,
            int keyboardZoneCount,
            string[] fanModeNames,
            EcRegisterProfile ec,
            TemperatureProfile[] temperature) {

            Name = name;
            KeyboardZoneCount = keyboardZoneCount;
            FanModeNames = fanModeNames;
            Ec = ec;
            Temperature = temperature;
        }

        // Resolves a baseboard product ID without touching hardware.
        public static PlatformProfile ForProduct(string product) {
            if(string.Equals((product ?? "").Trim(), "8BD4",
                StringComparison.OrdinalIgnoreCase)) {

                return new PlatformProfile(
                    "HP Victus 8BD4",
                    1,
                    new string[] { "Default", "Performance" },
                    new EcRegisterProfile(
                        0x80, 0x81, 0x98, 0x99, 0x90, 0x91,
                        0x6C, 0x70, 0xA8, 0x28, 0x18, 0xD0),
                    new TemperatureProfile[] {
                        new TemperatureProfile("CPU", 0xB0),
                        new TemperatureProfile("GPU", 0xB2),
                        new TemperatureProfile("SSD", 0xB7, false)
                    });
            }

            // Original OmenMon layout (08A13/08A14 and compatible models).
            return new PlatformProfile(
                "HP Omen legacy EC",
                4,
                Enum.GetNames(typeof(BiosData.FanMode)),
                new EcRegisterProfile(
                    0x34, 0x35, 0x2E, 0x2F, 0x2C, 0x2D,
                    0xB0, 0xB2, 0x63, 0x62, 0x95, 0xF4),
                new TemperatureProfile[] {
                    new TemperatureProfile("CPUT", 0x57),
                    new TemperatureProfile("GPTM", 0xB7),
                    new TemperatureProfile("RTMP", 0x58),
                    new TemperatureProfile("TMP1", 0x59),
                    new TemperatureProfile("TNT2", 0x47),
                    new TemperatureProfile("TNT3", 0x48),
                    new TemperatureProfile("TNT4", 0x49),
                    new TemperatureProfile("TNT5", 0x4B)
                });
        }

        // HP's current thermal policy maps the friendly three modes to
        // 0x30/0x31/0x50.  Older systems expect 0/1/2 instead.
        public BiosData.FanMode ResolveFanMode(
            BiosData.FanMode mode,
            BiosData.ThermalPolicyVersion thermalPolicy) {

            if(thermalPolicy == BiosData.ThermalPolicyVersion.V0) {
                if(mode == BiosData.FanMode.Default)
                    return BiosData.FanMode.LegacyDefault;
                if(mode == BiosData.FanMode.Performance)
                    return BiosData.FanMode.LegacyPerformance;
                if(mode == BiosData.FanMode.Cool)
                    return BiosData.FanMode.LegacyCool;
            }
            return mode;
        }
    }
}
