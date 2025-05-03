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

        // Sets the fan mode with special handling for proper Auto mode switching
        // Ensures EC registers are properly reset for HP Victus compatibility
        public void SetAutoMode(BiosData.FanMode mode);

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
            return this.Manual.GetValue() == (byte) PlatformData.FanManual.On;
        }

        // Sets the manual fan speed toggle status
        public void SetManual(bool flag) {
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
            Hw.BiosSet<BiosData.FanMode>(Hw.Bios.SetFanMode, mode);
            // Note: WMI BIOS call preferred over this.Mode.SetValue((byte) mode);
        }

        // Special method for properly switching to Auto mode
        // Based on OMEN Gaming Hub behavior and EC register analysis
        public void SetAutoMode(BiosData.FanMode mode) {
            try {
                // First, explicitly disable manual control
                this.SetManual(false);
                
                // Reset fan speed registers to default values
                if (Config.FanLevelUseEc) {
                    // Get the EC singleton instance
                    IEmbeddedController ec = EmbeddedController.Instance;
                    
                    // Initialize EC access
                    ec.Initialize();
                    
                    // Try to lock the EC
                    if (ec.Request(1000)) {
                        try {
                            // Based on EC dumps comparison between OmenMon and OMEN Hub
                            // Reset all the important fan control registers in the correct sequence
                            ec.WriteByte(0x08, 0x0F); // Set fan control mode to auto (from OMEN Hub EC dump)
                            ec.WriteByte(0x0F, 0x00); // Reset secondary fan control register
                            ec.WriteByte(0x11, 0x00); // Disable manual fan control
                            ec.WriteByte(0x12, 0x00); // Reset CPU fan speed register
                            ec.WriteByte(0x14, 0x00); // Reset GPU fan speed register
                            ec.WriteByte(0x15, 0x00); // Reset additional fan control register
                            
                            // Additional registers that appear to be modified by OMEN Hub
                            ec.WriteByte(0x18, mode == BiosData.FanMode.Performance ? (byte)0x01 : (byte)0x00); // Set cooling mode based on fan mode
                        }
                        finally {
                            // Always release the EC lock
                            ec.Release();
                        }
                    }
                }
                
                // Turn off max fan mode if it was on
                this.SetMax(false);
                
                // Make multiple BIOS calls to ensure proper handover to Auto
                this.SetLevels(new byte[] {0, 0});
                
                // Wait briefly between operations
                System.Threading.Thread.Sleep(100);
                
                // Make the actual BIOS call to switch to Auto mode
                // This will set the correct fan mode based on power mode (Default=48, Performance=49, etc.)
                Hw.BiosSet<BiosData.FanMode>(Hw.Bios.SetFanMode, mode);
                
                // Wait briefly to allow settings to take effect
                System.Threading.Thread.Sleep(100);
                
                // Make a second BIOS call to ensure mode is set
                Hw.BiosSet<BiosData.FanMode>(Hw.Bios.SetFanMode, mode);
                
                Log.Info($"Fan mode set to Auto ({mode})");
            }
            catch (Exception ex) {
                Log.Error($"Failed to set Auto fan mode: {ex.Message}");
                // If anything fails, fall back to the standard fan mode setting
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
