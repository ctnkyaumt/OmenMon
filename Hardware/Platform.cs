  //\\   OmenMon: Hardware Monitoring & Control Utility
 //  \\  Copyright © 2023 Piotr Szczepański * License: GPL3
     //  https://omenmon.github.io/

using System;
using System.Collections.Specialized;
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

        // Product-specific capabilities and EC layout
        public PlatformProfile Profile { get; private set; }

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

            // Select a non-invasive profile from the stable baseboard ID
            this.Profile = PlatformProfile.ForProduct(this.System.GetProduct());

            // Initialize the fan controls
            InitFans();

            // Initialize the temperature controls
            InitTemperature();

        }

        // Initializes the fan controls
        private void InitFans() {
            EcRegisterProfile ec = this.Profile.Ec;
            this.Fans = new FanArray(
                        new IFan[] {

                            // Define the CPU fan
                            new Fan(
                                BiosData.FanType.Cpu,
                                new EcComponent(
                                    ec.Fan0Level,
                                    PlatformData.AccessType.Read | PlatformData.AccessType.Write),
                                new EcComponent(
                                    ec.Fan0Rate,
                                    PlatformData.AccessType.Read),
                                new EcComponent(
                                    ec.Fan0RateSet,
                                    PlatformData.AccessType.Write),
                                new EcComponent(
                                    ec.Fan0Rpm,
                                    PlatformData.AccessType.Read,
                                    PlatformData.DataSize.Word)),

                            // Define the GPU fan
                            new Fan(
                                BiosData.FanType.Gpu,
                                new EcComponent(
                                    ec.Fan1Level,
                                    PlatformData.AccessType.Read | PlatformData.AccessType.Write),
                                new EcComponent(
                                    ec.Fan1Rate,
                                    PlatformData.AccessType.Read),
                                new EcComponent(
                                    ec.Fan1RateSet,
                                    PlatformData.AccessType.Write),
                                new EcComponent(
                                    ec.Fan1Rpm,
                                    PlatformData.AccessType.Read,
                                    PlatformData.DataSize.Word)) },

                        // Define the countdown component
                        new EcComponent(
                            ec.FanCountdown,
                            PlatformData.AccessType.Read | PlatformData.AccessType.Write),

                        // Define the manual toggle component
                        new EcComponent(
                            ec.FanManual,
                            PlatformData.AccessType.Read | PlatformData.AccessType.Write), 

                        // Define the mode component
                        new EcComponent(
                            ec.FanMode,
                            PlatformData.AccessType.Read | PlatformData.AccessType.Write), 

                        // Define the switch component
                        new EcComponent(
                            ec.FanSwitch,
                            PlatformData.AccessType.Read | PlatformData.AccessType.Write),
                        this.Profile,
                        this.System);

        }

        // Initializes the system settings
        private void InitSystem() {
            this.System = new Settings();
        }

        // Initializes the temperature controls
        private void InitTemperature() {

            // Use per-product defaults unless the XML supplied a usable list.
            if(!Config.TemperatureSensorCustomized) {
                Config.TemperatureSensor = new OrderedDictionary();
                foreach(TemperatureProfile sensor in this.Profile.Temperature)
                    Config.TemperatureSensor[sensor.Name] =
                        new Config.TemperatureSensorData(
                            PlatformData.LinkType.EmbeddedController,
                            sensor.Register,
                            sensor.Use);
            }

            // Set up the temperature sensor array based on the configuration data
            this.Temperature = new IPlatformReadComponent[Config.TemperatureSensor.Count];
            this.TemperatureUse = new bool[Config.TemperatureSensor.Count];

            // Populate the temperature sensor array
            int i = 0;
            foreach(System.Collections.DictionaryEntry entry in Config.TemperatureSensor) {
                string name = (string)entry.Key;
                Library.Config.TemperatureSensorData sensorData = (Library.Config.TemperatureSensorData)entry.Value;

                // Set whether the sensor can be used for maximum temperature
                this.TemperatureUse[i] = sensorData.Use;

                // Process each sensor loaded from the configuration
                switch(sensorData.Source) {

                    // Add an Embedded Controller sensor
                    case PlatformData.LinkType.EmbeddedController:
                        // Create EC sensor and set its display name from config key (e.g. "GPU")
                        var comp = new EcComponent(
                            sensorData.Register,
                            (int)Config.MaxBelievableTemperature);
                        comp.SetName(name);
                        this.Temperature[i++] = comp;
                        global::System.Diagnostics.Debug.WriteLine(
                            $"Temp sensor {i} → key='{name}', component name='{comp.GetName()}', register=0x{comp.GetLinkType()}" );
                        break;

                    // Add a WMI BIOS sensor
                    case PlatformData.LinkType.WmiBios:
                        this.Temperature[i++] =
                            new WmiBiosTemperatureComponent(Config.MaxBelievableTemperature);
                        break;

                }
            }

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
