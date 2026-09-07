  //\\   OmenMon: Hardware Monitoring & Control Utility
 //  \\  Copyright © 2023 Piotr Szczepański * License: GPL3
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
        public void RestoreAutomatic(BiosData.FanMode mode);

        // Retrieves the fan off switch status
        // or switches the fan off
        public bool GetOff();
        public void SetOff(bool flag);


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

        // Product-specific behavior and system design data
        protected PlatformProfile Profile;
        protected ISettings System;

        // Caches the last fan mode set by the application
        // Used as a fallback when the EC mode register returns invalid values
        protected BiosData.FanMode? LastSetMode = null;

        // Caches the fan off state set by the application
        // Used when the EC fan switch register returns unreliable values
        protected bool LastSetOff = false;

        // Caches the fan max state set by the application
        // Used when BIOS/EC reports instantaneous RPM rather than latch state
        protected bool LastSetMax = false;

        // Constructs a fan array instance
        public FanArray(
            IFan[] fan,
            IPlatformReadWriteComponent fanCountdown,
            IPlatformReadWriteComponent fanManual,
            IPlatformReadWriteComponent fanMode,
            IPlatformReadWriteComponent fanSwitch,
            PlatformProfile profile,
            ISettings system) {

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

            this.Profile = profile;
            this.System = system;

        }

        // Retrieves the countdown value [s]
        // until automatic settings are restored
        public int GetCountdown() {
            if(Profile.UsesBiosFanControl)
                return 0; // No verified writable countdown register on this board.
            this.Countdown.Update();
            return this.Countdown.GetValue();
        }

        // Sets the countdown value [s]
        public void SetCountdown(int countdown) {
            if(!Profile.UsesBiosFanControl)
                this.Countdown.SetValue(countdown);
        }

        // Retrieves the levels of all fans at the same time
        public byte[] GetLevels() {
            return Hw.BiosGet(Hw.Bios.GetFanLevel);
        }

        // Sets the levels of all fans at the same time
        public void SetLevels(byte[] levels) {
            if(Profile.UsesBiosFanControl) {
                // Use HP's WMI payload regardless of legacy XML flags.
                // Report failures instead of claiming the target was accepted.
                Hw.Bios.SetFanLevel(levels);
                LastSetOff = levels[0] == 0 && levels[1] == 0;
                LastSetMax = false;
                return;
            }

            // Set manual fan mode, if needed
            if(Config.FanLevelNeedManual && !Profile.UsesBiosFanControl)
                this.SetManual(true);

            // Depending on the configuration setting,
            // use either the BIOS or the EC to set levels
            if(Config.FanLevelUseEc && !Profile.UsesBiosFanControl) {

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
            return !Profile.UsesBiosFanControl &&
                this.Manual.GetValue() == (byte) PlatformData.FanManual.On;
        }

        // Sets the manual fan speed toggle status
        public void SetManual(bool flag) {
            if(Profile.UsesBiosFanControl)
                return;
            this.Manual.SetValue(flag ?
                (byte) PlatformData.FanManual.On : (byte) PlatformData.FanManual.Off);
        }

        // Retrieves the maximum fan speed status
        public bool GetMax() {
            if(Profile.UsesBiosFanControl)
                return LastSetMax;
            return Hw.BiosGet<bool>(Hw.Bios.GetMaxFan);
        }

        // Sets the maximum fan speed status
        public void SetMax(bool flag) {
            LastSetMax = flag;
            if(!flag) {
                RestoreAutomatic(LastSetMode ?? BiosData.FanMode.Default);
                return;
            }
            Hw.BiosSet(Hw.Bios.SetMaxFan, true);
            LastSetOff = false;
        }

        // Retrieves the current fan mode
        public BiosData.FanMode GetMode() {

            // When using EC for fan control, read the mode from the EC register
            if(Config.FanLevelUseEc && !Profile.UsesBiosFanControl) {
                this.Mode.Update();
                byte ecValue = (byte) this.Mode.GetValue();

                // Check if the EC value maps to a recognized fan mode
                if(Enum.IsDefined(typeof(BiosData.FanMode), ecValue)) {
                    BiosData.FanMode ecMode = (BiosData.FanMode) ecValue;
                    LastSetMode = ecMode;
                    return ecMode;
                }
            }

            // When not using EC, or when EC returns invalid data,
            // return the last mode set by the application (or Default)
            return LastSetMode ?? BiosData.FanMode.Default;
        }

        // Sets the current fan mode
        public void SetMode(BiosData.FanMode mode) {
            SetModeInternal(mode, false);
        }

        // Restores firmware-controlled fan behavior after Max, Off, a fixed
        // level, or a fan program.
        public void RestoreAutomatic(BiosData.FanMode mode) {
            LastSetOff = false;
            LastSetMax = false;

            if(Config.FanLevelNeedManual && !Profile.UsesBiosFanControl)
                SetManual(false);
            SetModeInternal(mode, true);
            Hw.BiosSet(Hw.Bios.SetMaxFan, false);
        }

        // Applies the mode through WMI.  Raw EC mode writes are deliberately
        // excluded: their address and encoding differ between product lines.
        protected void SetModeInternal(BiosData.FanMode mode, bool fanControlByBios) {

            BiosData.FanMode requestedMode = mode;
            try {
                BiosData.ThermalPolicyVersion policy =
                    this.System.GetSystemData().ThermalPolicy;
                mode = this.Profile.ResolveFanMode(mode, policy);
            } catch {
                // Keep the requested value if system-design data is absent.
            }

            Hw.BiosExec(
                bios => bios.SetFanMode(mode, fanControlByBios),
                Hw.Bios);
            LastSetMode = requestedMode;
        }

        // Retrieves the fan off switch status
        public bool GetOff() {
            if(Config.FanLevelUseEc && !Profile.UsesBiosFanControl) {
                this.Switch.Update();
                return ((PlatformData.FanSwitch) this.Switch.GetValue()) == PlatformData.FanSwitch.Off;
            }
            // When EC registers are unreliable, use cached state
            return LastSetOff;
        }

        // Switches the fan off or back on
        public void SetOff(bool flag) {
            LastSetOff = flag;
            if(flag)
                LastSetMax = false;
            if(Config.FanLevelUseEc && !Profile.UsesBiosFanControl) {
                // Use EC register to switch fans off/on
                this.Switch.SetValue(flag ?
                    (int) PlatformData.FanSwitch.Off : (int) PlatformData.FanSwitch.On);
            } else {
                if(flag) {
                    // Turn fans off by setting levels to zero via BIOS WMI
                    try {
                        Hw.BiosSet(Hw.Bios.SetFanLevel, new byte[]{0x00, 0x00});
                    } catch {}
                } else {
                    // Turn fans back on by restoring automatic mode via BIOS WMI
                    RestoreAutomatic(LastSetMode ?? BiosData.FanMode.Default);
                }
            }
        }
#endregion

    }

}
