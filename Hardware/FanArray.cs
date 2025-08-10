  //\\   OmenMon: Hardware Monitoring & Control Utility
 //  \\  Copyright 2023 Piotr Szczepański * License: GPL3
     //  https://omenmon.github.io/

using System;
using OmenMon.Hardware.Bios;
using OmenMon.Hardware.Ec;
using OmenMon.Library;

namespace OmenMon.Hardware.Platform {

#region Interface
    // Defines an interface for interacting with the fan system
    public interface IFanArray {

        public IFan[] Fan { get; }

        // Retrieves or sets the countdown value
        // until automatic settings are restored [s]
        public int GetCountdown();
        public void SetCountdown(int countdown);

        // Retrieves or sets the levels
        // of all fans at the same time
        public byte[] GetLevels();
        public void SetLevels(byte[] levels);

        // Retrieves or sets maximum fan speed
        public bool GetMax();  
        public void SetMax(bool flag);

        // Retrieves or sets manual fan control state
        public bool GetManual();
        public void SetManual(bool flag);

        // Retrieves or sets the current fan mode
        public BiosData.FanMode GetMode();
        public void SetMode(BiosData.FanMode mode);

        // Retrieves the fan off switch status
        // or switches the fan off
        public bool GetOff();
        public void SetOff(bool flag);

        // Adds a method to release fan control back to Windows ACPI
        public void ReleaseControlToWindows(BiosData.FanMode mode = BiosData.FanMode.Default);

    }
#endregion

#region Implementation
    // Implements a mechanism for interacting with the fan system
    public class FanArray : IFanArray {

        // Fan array
        public IFan[] Fan { get; private set; }

        // Stores the countdown platform component
        protected IPlatformReadWriteComponent Countdown;

        // Stores the manual toggle component
        protected IPlatformReadWriteComponent Manual;

        // Stores the fan mode component
        protected IPlatformReadWriteComponent Mode;

        // Stores the fan on and off switch component
        protected IPlatformReadWriteComponent Switch;

        // Constructs a fan array instance
        public FanArray(
            IFan[] fan,
            IPlatformReadWriteComponent fanCountdown,
            IPlatformReadWriteComponent fanManual,
            IPlatformReadWriteComponent fanMode,
            IPlatformReadWriteComponent fanSwitch) {

            // Initialize the fan array
            this.Fan = new IFan[PlatformData.FanCount];

            // Define the CPU fan
            this.Fan[0] = fan[0];

            // Define the GPU fan
            this.Fan[1] = fan[1];

            // Define the countdown component
            this.Countdown = fanCountdown;

            // Define the mode component
            this.Manual = fanManual;

            // Define the mode component
            this.Mode = fanMode;

            // Define the switch component
            this.Switch = fanSwitch;

        }

        // Retrieves the countdown value [s]
        // until automatic settings are restored
        public int GetCountdown() {
            this.Countdown.Update();
            return this.Countdown.GetValue();
        }

        // Sets the countdown value [s]
        public void SetCountdown(int countdown) {
            this.Countdown.SetValue(countdown);
        }

        // Retrieves the levels of all fans at the same time
        public byte[] GetLevels() {
            return Hw.BiosGet(Hw.Bios.GetFanLevel);
        }

        // Sets the levels of all fans at the same time
        public void SetLevels(byte[] levels) {

            // Set manual fan mode, if needed
            if(Config.FanLevelNeedManual)
                this.SetManual(true);

            // Depending on the configuration setting,
            // use either the BIOS or the EC to set levels
            if(Config.FanLevelUseEc) {

                // Try to set the speed for each fan individually
                for(int i = 0; i < levels.Length; i++)
                    this.Fan[i].SetLevel(levels[i]);

            } else {
                try {

                    // Make a WMI BIOS call to set the level of both fans
                    Hw.BiosSet(Hw.Bios.SetFanLevel, levels);

                } catch {

                    // It has been reported on some models the settings
                    // take effect anyway, despite a BIOS error returned

                    // Thus, silently ignore if the call failed

                    // Regardless of the Config.BiosErrorReporting value,
                    // status is always checked, and reported in CLI mode

                }
            }
        }

        // Retrieves the manual fan speed toggle status
        public bool GetManual() {
            // Some models expose manual via OMCC (0x28), others via SFAN bit #1 (0xD0)
            bool omccManual = this.Manual.GetValue() == (byte) PlatformData.FanManual.On;
            try {
                byte sfan = Hw.EcGetByte((byte) EmbeddedControllerData.Register.SFAN);
                bool sfanManual = (sfan & 0x02) != 0; // Bit #1: Manual (constant)
                return omccManual || sfanManual;
            } catch {
                return omccManual;
            }
        }

        // Sets the manual fan speed toggle status
        public void SetManual(bool flag) {
            // Update OMCC (0x28) first
            this.Manual.SetValue(flag ?
                (byte) PlatformData.FanManual.On : (byte) PlatformData.FanManual.Off);

            // Also reflect the state in SFAN (0xD0) bit #1 while preserving other bits
            try {
                byte sfan = Hw.EcGetByte((byte) EmbeddedControllerData.Register.SFAN);
                byte newSfan = flag ? (byte) (sfan | 0x02) : (byte) (sfan & ~0x02);
                if(newSfan != sfan)
                    Hw.EcSetByte((byte) EmbeddedControllerData.Register.SFAN, newSfan);
            } catch { }
        }

        // Retrieves the maximum fan speed status
        public bool GetMax() {
            return Hw.BiosGet<bool>(Hw.Bios.GetMaxFan);
        }

        // Sets the maximum fan speed status
        public void SetMax(bool flag) {
            Hw.BiosSet(Hw.Bios.SetMaxFan, flag);
        }

        // Retrieves the current fan mode
        public BiosData.FanMode GetMode() {
            this.Mode.Update();
            return (BiosData.FanMode) this.Mode.GetValue();
        }

        // Sets the current fan mode
        public void SetMode(BiosData.FanMode mode) {
            Hw.BiosSet<BiosData.FanMode>(Hw.Bios.SetFanMode, mode);
            // Note: WMI BIOS call preferred over this.Mode.SetValue((byte) mode);
        }

        // Implementation of handover to Windows control (Auto) via EC flags clear and BIOS defaults
        public void ReleaseControlToWindows(BiosData.FanMode mode = BiosData.FanMode.Default) {
            try {
                // Disable overrides first
                this.SetMax(false);
                this.SetManual(false);

                // Clear Off/Manual/Max bits in SFAN (0xD0) to hand control back to Auto
                // Bit #0: Off, #1: Manual, #2: Max
                try {
                    byte sfan = Hw.EcGetByte((byte) EmbeddedControllerData.Register.SFAN);
                    byte cleared = (byte) (sfan & ~0x07);
                    if(cleared != sfan)
                        Hw.EcSetByte((byte) EmbeddedControllerData.Register.SFAN, cleared);
                } catch { }

                // For HP Victus ECs, writing 0xFF to PWM settings restores Auto
                try {
                    Hw.EcSetByte((byte) EmbeddedControllerData.Register.SRP1, 0xFF);
                    Hw.EcSetByte((byte) EmbeddedControllerData.Register.SRP2, 0xFF);
                } catch { }
                try {
                    Hw.EcSetByte((byte) EmbeddedControllerData.Register.XSS1, 0xFF);
                    Hw.EcSetByte((byte) EmbeddedControllerData.Register.XSS2, 0xFF);
                } catch { }

                // Make a BIOS call to explicitly set the mode
                this.SetMode(mode);

                // Wait briefly to allow settings to take effect
                System.Threading.Thread.Sleep(100);

                // Make a second BIOS call to ensure mode is set and persisted
                this.SetMode(mode);
            } catch {
                // Fallback if EC writes fail
                this.SetMode(mode);
            }
        }

        // Retrieves the fan off switch status
        public bool GetOff() {
            this.Switch.Update();
            return ((PlatformData.FanSwitch) this.Switch.GetValue()) == PlatformData.FanSwitch.Off;
        }

        // Switches the fan off or back on
        public void SetOff(bool flag) {
            if (Config.FanLevelUseEc)
                this.Switch.SetValue(flag ?
                    (int) PlatformData.FanSwitch.Off : (int) PlatformData.FanSwitch.On);
            else 
                try {
                    // Make a WMI BIOS call to set the level of both fans
                    Hw.BiosSet(Hw.Bios.SetFanLevel, new byte[]{0x00,0x00});
                } catch {}
                
        }
#endregion

    }

}
