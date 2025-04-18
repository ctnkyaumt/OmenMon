  //\\   OmenMon: Hardware Monitoring & Control Utility
 //  \\  Copyright © 2023 Piotr Szczepański * License: GPL3
     //  https://omenmon.github.io/

using System;
using OmenMon.Hardware.Bios;
using OmenMon.Hardware.Ec;
using OmenMon.Library;

namespace OmenMon.Hardware.Platform {

    // Manages the hardware sensors
    public class Platform {

#region Data
        // Last maximum temperature reading
        public byte LastMaxTemperature { get; private set; }

        // System information
        public ISettings System { get; private set; }

        // Fan sensors and controls
        public IFanArray Fans { get; private set; }

        // Temperature sensor array and which of these values are used
        public IPlatformReadComponent[] Temperature { get; private set; }
        public bool[] TemperatureUse { get; private set; }
#endregion

#region Initialization
        // Initializes the class
        public Platform() {

            // Initialize the system settings
            InitSystem();

            // Initialize the fan controls
            InitFans();

            // Initialize the temperature controls
            InitTemperature();

        }

        // Initializes the fan controls
        private void InitFans() {

            // Fan array can be product-specific
            switch(this.System.GetProduct()) {

                case "?": // Default
                case "8A13":
                case "8A14":
                default:

                    this.Fans = new FanArray(
                        new IFan[] {

                            // Define the CPU fan
                            new Fan(
                                BiosData.FanType.Cpu,
                                new EcComponent(
                                    (byte) EmbeddedControllerData.Register.SRP1,
                                    PlatformData.AccessType.Read | PlatformData.AccessType.Write),
                                new EcComponent(
                                    (byte) EmbeddedControllerData.Register.XGS1,
                                    PlatformData.AccessType.Read),
                                new EcComponent(
                                    (byte) EmbeddedControllerData.Register.XSS1,
                                    PlatformData.AccessType.Write),
                                new EcComponent(
                                    (byte) EmbeddedControllerData.Register.RPM1,
                                    PlatformData.AccessType.Read,
                                    PlatformData.DataSize.Word)),

                            // Define the GPU fan
                            new Fan(
                                BiosData.FanType.Gpu,
                                new EcComponent(
                                    (byte) EmbeddedControllerData.Register.SRP2,
                                    PlatformData.AccessType.Read | PlatformData.AccessType.Write),
                                new EcComponent(
                                    (byte) EmbeddedControllerData.Register.XGS2,
                                    PlatformData.AccessType.Read),
                                new EcComponent(
                                    (byte) EmbeddedControllerData.Register.XSS2,
                                    PlatformData.AccessType.Write),
                                new EcComponent(
                                    (byte) EmbeddedControllerData.Register.RPM3, // Not a mistake, RPM2 is fan #0
                                    PlatformData.AccessType.Read,
                                    PlatformData.DataSize.Word)) },

                        // Define the countdown component
                        new EcComponent(
                            (byte) EmbeddedControllerData.Register.XFCD,
                            PlatformData.AccessType.Read | PlatformData.AccessType.Write),

                        // Define the manual toggle component
                        new EcComponent(
                            (byte) EmbeddedControllerData.Register.OMCC,
                            PlatformData.AccessType.Read | PlatformData.AccessType.Write), 

                        // Define the mode component
                        new EcComponent(
                            (byte) EmbeddedControllerData.Register.HPCM,
                            PlatformData.AccessType.Read | PlatformData.AccessType.Write), 

                        // Define the switch component
                        new EcComponent(
                            (byte) EmbeddedControllerData.Register.SFAN,
                            PlatformData.AccessType.Read | PlatformData.AccessType.Write));

                    break;

            }

        }

        // Initializes the system settings
        private void InitSystem() {
            this.System = new Settings();
        }

        // Initializes the temperature controls (EC-based CPU/GPU sensors)
        private void InitTemperature() {
            // Only CPU and GPU sensors
            this.Temperature = new IPlatformReadComponent[2];
            this.TemperatureUse = new bool[2] { true, true };

            // CPU temperature from EC CPUT register
            this.Temperature[0] = new CpuTemperatureComponent(Config.MaxBelievableTemperature);
            // GPU temperature from EC GPUT register
            this.Temperature[1] = new GpuTemperatureComponent(Config.MaxBelievableTemperature);
        }
#endregion

#region Information Retrieval
        // Obtains the maximum value from the platform temperature array
        public byte GetMaxTemperature(bool forceUpdate = false) {

            // Update the platform temperature readings first
            // if forced to do so
            if(forceUpdate)
                UpdateTemperature(true);

            // Reset the state
            this.LastMaxTemperature = 0;
            byte value;

            // Iterate through the platform temperature array
            for(int i = 0; i < this.Temperature.Length; i++)

                // Obtain the reading from each temperature sensor
                // If the value is higher than the current candidate
                if(this.TemperatureUse[i] // Ignore certain sensors
                    && (value = (byte) this.Temperature[i].GetValue())
                        > this.LastMaxTemperature)

                    // Update the candidate
                    this.LastMaxTemperature = value;

            // Return the result
            return this.LastMaxTemperature;

        }
#endregion

#region Updates
        // Updates everything
        public void UpdateAll() {
            UpdateFans();
            UpdateSystem();
            UpdateTemperature();
        }

        // Updates the fan readings
        public void UpdateFans() {
            // Fan readings updated at retrieval time
        }

        // Updates the system settings
        public void UpdateSystem() {
            // System settings updated either only once
            // during initialization, or at retrieval time
        }

        // Updates the temperature readings
        public void UpdateTemperature(bool onlyUsed = false) {
            for(int i = 0; i < Temperature.Length; i++)
                if(!onlyUsed || this.TemperatureUse[i])
                    this.Temperature[i].Update();
        }
#endregion

    }

}
