using System;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using OmenMon.Library;
using OmenMon.Hardware.Platform;
using OmenMon.Hardware.Ec;

namespace OmenMon.Hardware.Platform {
    /// <summary>
    /// CPU temperature via WMI (ACPI Thermal Zone).
    /// </summary>
    public class CpuTemperatureComponent : PlatformComponentAbstract, IPlatformReadComponent {
        private readonly ManagementObjectSearcher _searcher;

        public CpuTemperatureComponent(int constraint = int.MaxValue)
            : base(PlatformData.AccessType.Read) {
            this.Constraint = constraint;
            this.LinkType = PlatformData.LinkType.WmiBios;
            this.Size = PlatformData.DataSize.Byte;
            _searcher = new ManagementObjectSearcher("root\\WMI",
                "SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature");
            SetName();
        }

        public override void SetName(string name = "") => this.Name = "CPU";

        protected override int Read() {
            try {
                foreach(ManagementObject mo in _searcher.Get()) {
                    uint kelvin10 = (uint) mo["CurrentTemperature"];
                    double c = (kelvin10/10.0) - 273.15;
                    int temp = (int)Math.Round(c);
                    if(temp <= this.Constraint)
                        return temp;
                }
            } catch {
                // fallback to EC register CPUT if WMI fails
                try {
                    byte val = Hw.EcGetByte((byte)EmbeddedControllerData.Register.CPUT);
                    return val <= this.Constraint ? val : 0;
                } catch { }
            }
            return 0;
        }
    }

    /// <summary>
    /// GPU temperature via Nvidia NVAPI shared library.
    /// </summary>
    public class GpuTemperatureComponent : PlatformComponentAbstract, IPlatformReadComponent {
        public GpuTemperatureComponent(int constraint = int.MaxValue)
            : base(PlatformData.AccessType.Read) {
            this.Constraint = constraint;
            this.LinkType = PlatformData.LinkType.EmbeddedController;
            this.Size = PlatformData.DataSize.Byte;
            SetName();
        }

        public override void SetName(string name = "") => this.Name = "GPU";

        protected override int Read() {
            // Try NVML first (more reliable)
            try {
                if (NvmlNativeMethods.Init() == 0
                    && NvmlNativeMethods.GetHandle(0, out IntPtr handle) == 0
                    && NvmlNativeMethods.GetTemperature(handle, 0, out uint temp) == 0
                    && temp <= this.Constraint)
                    return (int)temp;
            } catch {
                // ignore NVML errors
            }
            // Fallback to EC GPU registers
            try {
                byte gput = Hw.EcGetByte((byte)EmbeddedControllerData.Register.GPUT);
                if (gput > 0 && gput <= this.Constraint)
                    return gput;
                byte gptm = Hw.EcGetByte((byte)EmbeddedControllerData.Register.GPTM);
                if (gptm > 0 && gptm <= this.Constraint)
                    return gptm;
            } catch {
                // ignore EC errors
            }
            return 0;
        }
    }
}

namespace OmenMon.Hardware.Platform.NvApi {
    internal static class NativeMethods {
        [DllImport("nvapi64.dll", EntryPoint = "nvapi_QueryInterface", CallingConvention = CallingConvention.Cdecl)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern IntPtr NvAPI_QueryInterface(uint id);

        private delegate int InitializeDelegate();
        private static InitializeDelegate _init;
        public static int Initialize() {
            if(_init == null) {
                IntPtr p = NvAPI_QueryInterface(0x0150E828);
                if(p == IntPtr.Zero) return -1;
                _init = Marshal.GetDelegateForFunctionPointer<InitializeDelegate>(p);
            }
            return _init();
        }

        private delegate int EnumPhysicalGPUsDelegate([Out] IntPtr[] handles, out int count);
        private static EnumPhysicalGPUsDelegate _enum;
        public static int EnumPhysicalGPUs(IntPtr[] handles, out int count) {
            if(_enum == null) {
                IntPtr p = NvAPI_QueryInterface(0xE5AC921F);
                if(p == IntPtr.Zero) { count = 0; return -1; }
                _enum = Marshal.GetDelegateForFunctionPointer<EnumPhysicalGPUsDelegate>(p);
            }
            return _enum(handles, out count);
        }

        private delegate int GPU_GetThermalSettingsDelegate(IntPtr hGpu, int sensorIndex, ref NV_GPU_THERMAL_SETTINGS settings);
        private static GPU_GetThermalSettingsDelegate _getThermal;
        public static int GPU_GetThermalSettings(IntPtr hGpu, int sensorIndex, ref NV_GPU_THERMAL_SETTINGS settings) {
            if(_getThermal == null) {
                IntPtr p = NvAPI_QueryInterface(0xE3640A56);
                if(p == IntPtr.Zero) return -1;
                _getThermal = Marshal.GetDelegateForFunctionPointer<GPU_GetThermalSettingsDelegate>(p);
            }
            return _getThermal(hGpu, sensorIndex, ref settings);
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct NV_GPU_THERMAL_SETTINGS {
            public uint version;
            public uint count;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)]
            public NV_GPU_THERMAL_SENSOR[] sensor;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct NV_GPU_THERMAL_SENSOR {
            public uint controller;
            public uint defaultMinTemp;
            public uint defaultMaxTemp;
            public uint currentTemp;
        }
    }
}

// NVML P/Invoke for GPU temperature
internal static class NvmlNativeMethods {
    [DllImport("nvml.dll", EntryPoint = "nvmlInit_v2", CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern int Init();
    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetHandleByIndex_v2", CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern int GetHandle(uint index, out IntPtr handle);
    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetTemperature", CallingConvention = CallingConvention.Cdecl)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern int GetTemperature(IntPtr device, int sensorType, out uint temp);
} 