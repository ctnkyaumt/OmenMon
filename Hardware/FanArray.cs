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
            IPlatformReadWriteComponent fanSwitch,
            bool isVictus = false) {

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

            // Victus detection flag
            this.IsVictus = isVictus;

        }

        // True if platform is HP Victus (model-specific behavior)
        private bool IsVictus;

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
            // Rely on OMCC (0x28) manual flag, which matches Victus behavior
            return this.Manual.GetValue() == (byte) PlatformData.FanManual.On;
        }

        // Sets the manual fan speed toggle status
        public void SetManual(bool flag) {
            // Update OMCC (0x28) only
            this.Manual.SetValue(flag ?
                (byte) PlatformData.FanManual.On : (byte) PlatformData.FanManual.Off);
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
            // Victus: ensure HPCM indicates non-Eco (0xFF) when setting built-in modes
            if (this.IsVictus) {
                try { Hw.EcSetByte((byte) EmbeddedControllerData.Register.HPCM, 0xFF); } catch { }
            }
            Hw.BiosSet<BiosData.FanMode>(Hw.Bios.SetFanMode, mode);
            // Note: WMI BIOS call preferred over this.Mode.SetValue((byte) mode);
        }

        // Implementation of handover to Windows control (Auto) via EC flags clear and BIOS defaults
        public void ReleaseControlToWindows(BiosData.FanMode mode = BiosData.FanMode.Default) {
            try {
                // Disable overrides first
                this.SetMax(false);
                this.SetManual(false);

                // Victus: mark non-Eco via HPCM and restore Auto PWM via XSS
                try { Hw.EcSetByte((byte) EmbeddedControllerData.Register.HPCM, 0xFF); } catch { }
                // For HP Victus ECs, setting XSS registers to 0xFF restores Auto PWM control
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
