// Hardware-free regression runner. Build and execute on Windows CI only.
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Remoting.Messaging;
using System.Runtime.Remoting.Proxies;
using System.Runtime.Serialization;
using OmenMon.AppCli;
using OmenMon.Hardware.Bios;
using OmenMon.Hardware.Platform;
using OmenMon.Library;

internal static class Regression {
    static string work;
    static int assertions;
    static void Check(bool condition, string message) {
        assertions++;
        if(!condition) throw new Exception(message);
    }
    static void Equal<T>(T expected, T actual, string message) {
        Check(EqualityComparer<T>.Default.Equals(expected, actual),
            message + ": expected " + expected + ", got " + actual);
    }
    static void Throws(Action action, string message) {
        bool threw = false;
        try { action(); } catch { threw = true; }
        Check(threw, message);
    }
    static void Set(object target, string property, object value) {
        target.GetType().GetProperty(property).GetSetMethod(true).Invoke(target, new[] { value });
    }
    // Unexpected calls fail; tests cannot fall through to real hardware.
    sealed class Proxy<T> : RealProxy {
        readonly Func<string, object[], object> handler;
        public Proxy(Func<string, object[], object> handler) : base(typeof(T)) { this.handler = handler; }
        public override IMessage Invoke(IMessage message) {
            var call = (IMethodCallMessage)message;
            try { return new ReturnMessage(handler(call.MethodName, call.Args), null, 0, call.LogicalCallContext, call); }
            catch(Exception e) { return new ReturnMessage(e, call); }
        }
        public T Value { get { return (T)GetTransparentProxy(); } }
    }
    static readonly PlatformProfile victus = PlatformProfile.ForProduct("8bd4");
    static readonly List<string> calls = new List<string>();
    static bool failMode, failLevels;
    static ISettings FakeSettings() {
        return new Proxy<ISettings>((name, args) => {
            if(name == "GetSystemData") return new BiosData.SystemData { ThermalPolicy = BiosData.ThermalPolicyVersion.V1 };
            if(name == "GetGpuPower") return new BiosData.GpuPowerData(new byte[] { 0, 1, 1, 75 });
            if(name == "GetGpuCustomTgp") return BiosData.GpuCustomTgp.Off;
            if(name == "GetGpuPpab") return BiosData.GpuPpab.Off;
            if(name == "SetGpuPower") { calls.Add("gpu:" + ((BiosData.GpuPowerData)args[0]).PeakTemperature); return null; }
            throw new Exception("Unexpected settings call: " + name);
        }).Value;
    }
    static void InstallBios() {
        failMode = failLevels = false;
        calls.Clear();
        Hw.Bios = new Proxy<IBiosCtl>((name, args) => {
            if(name == "SetFanLevel") {
                if(failLevels) throw new IOException("simulated rejected level");
                calls.Add("levels:" + string.Join(",", (byte[])args[0]));
                return null;
            }
            if(name == "SetFanMode") {
                if(failMode) throw new IOException("simulated rejected mode");
                calls.Add("mode:" + (byte)(BiosData.FanMode)args[0] + ":" + args[1]);
                return null;
            }
            if(name == "SetMaxFan") { calls.Add("max:" + args[0]); return null; }
            if(name == "GetFanLevel") return new byte[] { 58, 61 };
            throw new Exception("Unexpected BIOS call: " + name);
        }).Value;
    }
    static FanArray Fans(PlatformProfile profile, IPlatformReadWriteComponent manual = null) {
        return new FanArray(new IFan[2], null, manual, null, null, profile, FakeSettings());
    }
    static void FanControl() {
        InstallBios();
        Config.FanLevelUseEc = Config.FanLevelNeedManual = true;
        var fans = Fans(victus);
        Equal("Default,Performance", string.Join(",", victus.FanModeNames), "supported modes");
        Equal(0, fans.GetCountdown(), "no unverified countdown read");
        Check(!fans.GetManual(), "no unverified manual read");
        fans.SetManual(true); fans.SetCountdown(120);
        Equal(0, calls.Count, "no unverified EC writes");
        fans.SetLevels(new byte[] { 35, 36 });
        Equal("levels:35,36", calls.Single(), "Victus ignores legacy EC flag");
        calls.Clear();
        fans.RestoreAutomatic(BiosData.FanMode.Default);
        Equal("levels:255,255|mode:48:True|max:False", string.Join("|", calls), "release before Auto");
        calls.Clear(); fans.SetMax(false);
        Equal("levels:255,255|mode:48:True|max:False", string.Join("|", calls), "Max off restores Auto");
        failMode = true;
        Throws(() => fans.SetMode(BiosData.FanMode.Performance), "failed mode propagates");
        Equal(BiosData.FanMode.Default, fans.GetMode(), "failed mode does not update cache");
        failMode = false; failLevels = true; calls.Clear();
        Throws(() => fans.RestoreAutomatic(BiosData.FanMode.Performance), "failed release propagates");
        Equal(0, calls.Count, "failed release cannot report Auto success");
        Throws(() => fans.SetLevels(new byte[] { 35, 35 }), "failed level propagates");
        failLevels = false; Config.FanLevelUseEc = false;
        int manualState = -1;
        var manual = new Proxy<IPlatformReadWriteComponent>((name, args) => {
            if(name == "SetValue") { manualState = (int)args[0]; return null; }
            throw new Exception("Unexpected component call: " + name);
        }).Value;
        Fans(PlatformProfile.ForProduct("8A14"), manual).RestoreAutomatic(BiosData.FanMode.Default);
        Equal((int)PlatformData.FanManual.Off, manualState, "legacy Auto leaves manual off");
        var method = typeof(BiosCtl).GetMethod("CreateFanLevelPayload", BindingFlags.Static | BindingFlags.NonPublic);
        byte[] input = { 255, 35 };
        var payload = (byte[])method.Invoke(null, new object[] { input });
        Equal(128, payload.Length, "HP fan payload size");
        Check(payload[0] == 255 && payload[1] == 35 && payload.Skip(2).All(b => b == 0), "payload content");
        Throws(() => method.Invoke(null, new object[] { new byte[1] }), "reject incomplete targets");
        var component = new Proxy<IPlatformReadComponent>((name, args) => {
            if(name == "SetConstraint") return null;
            throw new Exception("Unexpected EC RPM read: " + name);
        }).Value;
        var fan = new Fan(BiosData.FanType.Cpu, null, component, null, component, true);
        Equal(5800, fan.GetSpeed(), "Victus WMI RPM");
        Equal(100, fan.GetRate(), "percent clamped above configured maximum");
    }
    static void GpuPower() {
        foreach(BiosData.GpuPowerLevel level in Enum.GetValues(typeof(BiosData.GpuPowerLevel))) {
            var power = new BiosData.GpuPowerData(level);
            Equal((byte)87, power.PeakTemperature, "HP threshold for " + level);
            Equal(level == BiosData.GpuPowerLevel.Minimum ? BiosData.GpuCustomTgp.Off : BiosData.GpuCustomTgp.On, power.CustomTgp, "TGP preset");
            Equal(level == BiosData.GpuPowerLevel.Maximum ? BiosData.GpuPpab.On : BiosData.GpuPpab.Off, power.Ppab, "PPAB preset");
        }
        Equal((byte)75, new BiosData.GpuPowerData(new byte[] { 0, 1, 1, 75 }).PeakTemperature, "readback retains original threshold");
    }
    static void LoadSensors(string sensors, PlatformProfile profile) {
        File.WriteAllText(Config.FilePath, "<OmenMon><Config><Temperature>" + sensors + "</Temperature></Config></OmenMon>");
        Config.TemperatureSensor = new OrderedDictionary();
        Config.TemperatureSensorCustomized = false;
        Config.Load(); Config.ResolveTemperatureSensors(profile);
    }
    static Config.TemperatureSensorData Sensor(string name) {
        return (Config.TemperatureSensorData)Config.TemperatureSensor[name];
    }
    static void SensorPersistence() {
        LoadSensors("", victus);
        Equal((byte)0xB0, Sensor("CPU").Register, "Victus CPU");
        Equal((byte)0xB2, Sensor("GPU").Register, "Victus GPU");
        Check(!Sensor("SYS").Use, "SYS display only");
        Config.Save();
        Check(File.ReadAllText(Config.FilePath).Contains("Register=\"0xB2\""), "save GPU address");
        var child = new ProcessStartInfo(Assembly.GetExecutingAssembly().Location, "--reload \"" + Config.FilePath + "\"") {
            UseShellExecute = false, CreateNoWindow = true
        };
        using(var process = Process.Start(child)) {
            if(!process.WaitForExit(15000)) { process.Kill(); throw new Exception("Sensor restart timed out"); }
            Equal(0, process.ExitCode, "fresh process sensor reload");
        }
        LoadSensors("<Sensor Name=\"CPU\" Source=\"EC\"/><Sensor Name=\"GPU\" Source=\"EC\"/><Sensor Name=\"SSD\" Source=\"EC\" Use=\"false\"/>", victus);
        Equal((byte)0xB2, Sensor("GPU").Register, "symbolic GPU resolved by profile");
        Check(Config.TemperatureSensor.Contains("SYS") && !Config.TemperatureSensor.Contains("SSD"), "migrate unproven SSD label");
        LoadSensors("<Sensor Name=\"Custom\" Source=\"EC\" Register=\"0x73\"/><Sensor Name=\"Firmware\" Source=\"BIOS\"/>", victus);
        Config.Save(); Config.TemperatureSensor.Clear(); Config.Load(); Config.ResolveTemperatureSensors(victus);
        Equal((byte)0x73, Sensor("Custom").Register, "custom EC register survives");
        Equal(PlatformData.LinkType.WmiBios, Sensor("Firmware").Source, "BIOS source survives");
        var legacy = PlatformProfile.ForProduct("8A14");
        LoadSensors("", legacy);
        Equal((byte)0x57, Sensor("CPUT").Register, "legacy CPU preserved");
        Equal((byte)0xB7, Sensor("GPTM").Register, "legacy GPU preserved");
        Config.Save(); Config.TemperatureSensor.Clear(); Config.Load(); Config.ResolveTemperatureSensors(legacy);
        Equal(8, Config.TemperatureSensor.Count, "all legacy sensors survive");
    }
    static void FanPrograms() {
        InstallBios(); Config.FanLevelNeedManual = false; Config.FanProgramModeCheckFirst = false;
        var platform = (Platform)FormatterServices.GetUninitializedObject(typeof(Platform));
        Set(platform, "Fans", Fans(victus)); Set(platform, "System", FakeSettings());
        Set(platform, "Temperature", new IPlatformReadComponent[] {
            new Proxy<IPlatformReadComponent>((name, args) => {
                if(name == "Update") return true;
                if(name == "GetValue") return 50;
                throw new Exception("Unexpected sensor call: " + name);
            }).Value
        });
        Set(platform, "TemperatureUse", new[] { true });
        Config.FanProgram["Regression"] = new FanProgramData("Regression", BiosData.FanMode.Performance,
            BiosData.GpuPowerLevel.Maximum, new SortedDictionary<byte, byte[]> { { 0, new byte[] { 35, 36 } } });
        var program = new FanProgram(platform, (severity, message) => { });
        Check(program.Run("Regression"), "program starts");
        Equal("mode:49:False|gpu:87|levels:35,36", string.Join("|", calls), "policy before fixed speed");
        calls.Clear(); Check(program.Suspend(), "program suspends");
        Equal("levels:255,255|mode:48:True|max:False|gpu:75", string.Join("|", calls), "suspend restores exact state");
        program.Resume(); calls.Clear(); Check(program.Terminate(), "program terminates");
        Equal("levels:255,255|mode:48:True|max:False|gpu:75", string.Join("|", calls), "terminate restores Auto");
    }
    static void EcReports() {
        var save = typeof(CliOp).GetMethod("SaveEcReport", BindingFlags.NonPublic | BindingFlags.Static);
        var data = new CliOp.EcMonData[256];
        for(int i = 0; i < data.Length; i++) data[i].Values = new List<byte> { (byte)i };
        data[0].Values.Add(42); // Cancelled/incomplete second sample.
        string path = Path.Combine(work, "ec.log");
        Func<bool> persist = () => (bool)save.Invoke(null, new object[] { data, path });
        Check(persist(), "partial sample saves complete baseline");
        Equal(2, File.ReadAllLines(path).Length, "only complete samples written");
        Check(File.ReadAllText(path).Contains("fe ff"), "unchanged registers retained");
        for(int i = 1; i < data.Length; i++) data[i].Values.Add((byte)i);
        data[0].UserChangeIndex = new List<int> { 1 };
        Check(persist(), "checkpoint replaced");
        Equal(3, File.ReadAllLines(path).Length, "second sample written");
        Check(File.ReadAllText(path).Contains("User change: 00"), "user marker retained");
        string before = File.ReadAllText(path);
        using(var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            Check(!persist(), "failed replacement reported");
        Equal(before, File.ReadAllText(path), "failed save retains checkpoint");
        Equal(0, Directory.GetFiles(work, "ec.log.*.tmp").Length, "failed-save temporary cleaned");
        data[255].Values.Clear();
        Check(!persist(), "empty baseline rejected");
        Equal(before, File.ReadAllText(path), "invalid report retains checkpoint");
        Environment.ExitCode = 0; // The preceding failures were intentional.
    }
    static void RedirectedCli(string exe) {
        var info = new ProcessStartInfo(exe, "-help") {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
        };
        using(var process = Process.Start(info)) {
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if(!process.WaitForExit(15000)) { process.Kill(); throw new Exception("Redirected CLI hung or opened a dialog"); }
            Equal(0, process.ExitCode, "redirected CLI exit");
            Check(stdout.Result.Contains("OmenMon") && stdout.Result.Contains("-EcMon"), "readable redirected help");
            Equal("", stderr.Result, "no console-handle exception");
        }
    }
    public static int Main(string[] args) {
        try {
            typeof(Cli).GetProperty("IsInitialized").GetSetMethod(true).Invoke(null, new object[] { true });
            Config.LocaleInit("Fallback");
            if(args.Length == 2 && args[0] == "--reload") {
                Config.FilePath = args[1]; Config.Load(); Config.ResolveTemperatureSensors(victus);
                Equal((byte)0xB2, Sensor("GPU").Register, "fresh GPU register");
                return 0;
            }
            work = Path.Combine(Path.GetTempPath(), "OmenMonRegression-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work); Config.FilePath = Path.Combine(work, "sensors.xml");
            foreach(Action test in new Action[] { FanControl, GpuPower, SensorPersistence, FanPrograms, EcReports }) {
                test(); Console.WriteLine("PASS " + test.Method.Name);
            }
            RedirectedCli(Path.GetFullPath(args[0])); Console.WriteLine("PASS RedirectedCli");
            Console.WriteLine("PASS " + assertions + " assertions; no hardware interfaces opened.");
            return 0;
        } catch(Exception e) {
            Console.Error.WriteLine(e); return 1;
        } finally {
            if(work != null && Directory.Exists(work)) Directory.Delete(work, true);
        }
    }
}
