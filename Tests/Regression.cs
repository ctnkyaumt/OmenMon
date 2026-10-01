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
using System.Windows.Forms;
using System.Threading;
using OmenMon.AppCli;
using OmenMon.AppGui;
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
    static bool failMode, failLevels, failMax, failHeartbeat, forcedGpuRead;
    static ISettings FakeSettings() {
        return new Proxy<ISettings>((name, args) => {
            if(name == "IsFullPower") return false;
            if(name == "GetSystemData") return new BiosData.SystemData { ThermalPolicy = BiosData.ThermalPolicyVersion.V1 };
            if(name == "GetGpuPower") {
                forcedGpuRead |= (bool)args[0];
                return new BiosData.GpuPowerData(new byte[] { 0, 1, 1, 75 });
            }
            if(name == "GetGpuCustomTgp") return BiosData.GpuCustomTgp.Off;
            if(name == "GetGpuPpab") return BiosData.GpuPpab.Off;
            if(name == "SetGpuPower") { calls.Add("gpu:" + ((BiosData.GpuPowerData)args[0]).PeakTemperature); return null; }
            throw new Exception("Unexpected settings call: " + name);
        }).Value;
    }
    static void InstallBios() {
        failMode = failLevels = failMax = failHeartbeat = forcedGpuRead = false;
        typeof(Hw).GetField("LastBiosHeartbeat", BindingFlags.NonPublic | BindingFlags.Static).SetValue(null, 0L);
        calls.Clear();
        Hw.Bios = new Proxy<IBiosCtl>((name, args) => {
            if(name == "GetFanCount") {
                if(failHeartbeat) throw new IOException("simulated rejected heartbeat");
                calls.Add("heartbeat"); return (byte)2;
            }
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
            if(name == "SetMaxFan") {
                if(failMax) throw new IOException("simulated rejected Max");
                calls.Add("max:" + args[0]); return null;
            }
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
        Throws(() => fans.SetLevels(null), "null levels rejected");
        Throws(() => fans.SetLevels(new byte[] {35}), "incomplete levels rejected");
        Equal(0, calls.Count, "invalid levels do not acquire OEM control");
        fans.SetLevels(new byte[] { 35, 36 });
        Equal("heartbeat|levels:35,36", string.Join("|", calls), "Victus ignores legacy EC flag");
        calls.Clear();
        fans.RestoreAutomatic(BiosData.FanMode.Default);
        Equal("mode:48:True", string.Join("|", calls), "release before Auto");
        calls.Clear(); fans.SetMax(false);
        Equal("mode:48:True", string.Join("|", calls), "Max off restores Auto");
        calls.Clear(); fans.SetMax(true);
        Equal("heartbeat|max:True", string.Join("|", calls), "Max on sets WMI MaxFan");
        Check(fans.GetMax(), "Victus Max cached true");
        calls.Clear(); fans.RestoreAutomatic(BiosData.FanMode.Default);
        Check(!fans.GetMax(), "Victus Max cached false after Auto");
        failMode = true;
        Throws(() => fans.SetMode(BiosData.FanMode.Performance), "failed mode propagates");
        Equal(BiosData.FanMode.Default, fans.GetMode(), "failed mode does not update cache");
        failMode = false; failLevels = true; calls.Clear();
        fans.RestoreAutomatic(BiosData.FanMode.Performance);
        Equal("mode:49:True", calls.Single(), "Auto never attempts a fixed target write");
        failLevels = false;
        failLevels = true;
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
        var settings = (Settings)FormatterServices.GetUninitializedObject(typeof(Settings));
        Set(settings, "GpuPower", new BiosData.GpuPowerData(new byte[] {0,1,1,75}));
        Hw.Bios = new Proxy<IBiosCtl>((name, args) => {
            if(name == "SetGpuPower") throw new IOException("simulated partially applied GPU write");
            if(name == "GetGpuPower") return new BiosData.GpuPowerData(new byte[] {1,0,1,87});
            throw new Exception("Unexpected GPU call: " + name);
        }).Value;
        Throws(() => settings.SetGpuPower(new BiosData.GpuPowerData(BiosData.GpuPowerLevel.Maximum)), "failed GPU write reported");
        Check(settings.GpuPower == null, "failed GPU write invalidates cached state");
        Equal((byte)87, settings.GetGpuPower().PeakTemperature, "query after failed write reads firmware");
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
        Set(platform, "Profile", victus);
        int temperature = 50;
        bool fresh = true;
        Set(platform, "Temperature", new IPlatformReadComponent[] {
            new Proxy<IPlatformReadComponent>((name, args) => {
                if(name == "Update") return fresh;
                if(name == "GetValue") return temperature;
                throw new Exception("Unexpected sensor call: " + name);
            }).Value
        });
        Set(platform, "TemperatureUse", new[] { true });
        Config.FanProgram["Regression"] = new FanProgramData("Regression", BiosData.FanMode.Performance,
            BiosData.GpuPowerLevel.Maximum, new SortedDictionary<byte, byte[]> { { 0, new byte[] { 35, 36 } } });
        var program = new FanProgram(platform, (severity, message) => { });
        Check(program.Run("Regression"), "program starts");
        Check(forcedGpuRead, "program snapshots fresh GPU settings");
        Equal("mode:49:False|gpu:87|heartbeat|levels:35,36", string.Join("|", calls), "policy before fixed speed");
        Check(program.Run("Regression", true), "program switches without replacing original state");
        calls.Clear(); Check(program.Suspend(), "program suspends");
        Equal("mode:48:True|gpu:75", string.Join("|", calls), "suspend restores exact state");
        program.Resume(); calls.Clear(); Check(program.Terminate(), "program terminates");
        Equal("mode:48:True|gpu:75", string.Join("|", calls), "terminate restores Auto");
        program.Run("Regression"); failMode = true; calls.Clear();
        Throws(() => program.Terminate(), "failed Auto restoration is reported");
        Check(!program.IsEnabled && !program.IsSuspended && !program.IsAlternate, "failed termination stops program");
        Equal("gpu:75", string.Join("|", calls), "GPU restoration attempted after fan failure");
        calls.Clear();
        Check(!program.Update(), "failed termination cannot reapply targets");
        platform.Fans.MaintainControl();
        Equal(0, calls.Count, "failed termination cannot renew heartbeat");
        failMode = false;

        program.Run("Regression");
        Config.FanProgram["Empty"] = new FanProgramData("Empty", BiosData.FanMode.Default,
            BiosData.GpuPowerLevel.Minimum, new SortedDictionary<byte,byte[]>());
        Config.FanProgram["Malformed"] = new FanProgramData("Malformed", BiosData.FanMode.Default,
            BiosData.GpuPowerLevel.Minimum, new SortedDictionary<byte,byte[]> {{0,new byte[] {35}}});
        calls.Clear();
        Check(!program.Run("Empty") && !program.Run("Malformed") && !program.Run(null), "invalid programs rejected");
        Equal("Regression", program.GetName(), "rejected program preserves active program");
        Check(program.IsEnabled && calls.Count == 0, "rejected program leaves hardware untouched");
        program.Terminate();

        Config.FanProgram["PositiveThreshold"] = new FanProgramData("PositiveThreshold", BiosData.FanMode.Default,
            BiosData.GpuPowerLevel.Minimum, new SortedDictionary<byte,byte[]> {
                {60,new byte[] {30,31}}, {80,new byte[] {40,41}}});
        calls.Clear(); program.Run("PositiveThreshold");
        Check(calls.Contains("levels:30,31"), "below first threshold uses first level");
        temperature = 70; calls.Clear(); program.Update();
        Check(calls.Contains("levels:30,31"), "between thresholds uses lower level");
        temperature = 80; calls.Clear(); program.Update();
        Check(calls.Contains("levels:40,41"), "exact upper threshold selects upper level");
        program.Terminate(); temperature = 50;

        program.Run("Regression"); calls.Clear(); failLevels = true;
        Throws(() => program.Update(), "failed program target reported");
        Check(!program.IsEnabled, "failed update stops program");
        Check(calls.Contains("gpu:75"), "failed update restores original GPU state");
        calls.Clear(); Check(!program.Update(), "failed update cannot retry target");
        platform.Fans.MaintainControl();
        Equal(0, calls.Count, "failed update cannot keep manual control alive");
        failLevels = false;

        foreach(int invalid in new[] {0, Config.MaxBelievableTemperature + 1}) {
            program.Run("Regression"); temperature = invalid; calls.Clear();
            Throws(() => program.Update(), "invalid temperature stops fan program");
            Check(!program.IsEnabled && calls.Contains("max:True"), "invalid sensor requests cooling before handback");
            Check(!calls.Any(c => c.StartsWith("levels:")), "invalid sensor cannot select low fan target");
            calls.Clear(); platform.Fans.MaintainControl();
            Equal(0, calls.Count, "sensor failure stops heartbeat renewal");
            temperature = 50;
        }
        program.Run("Regression"); fresh = false; calls.Clear();
        Throws(() => program.Update(), "stale cached temperature rejected");
        Check(!program.IsEnabled && calls.Contains("max:True"), "stale sensor stops program with cooling");
        fresh = true;
        program.Run("Regression"); failMode = true; calls.Clear();
        var operation = (GuiOp)FormatterServices.GetUninitializedObject(typeof(GuiOp));
        typeof(GuiOp).GetField("Program", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(operation, program);
        typeof(GuiOp).GetField("Platform", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(operation, platform);
        using(var tray = (GuiTray)FormatterServices.GetUninitializedObject(typeof(GuiTray)))
        using(var monitor = new System.Windows.Forms.Timer())
        using(var heartbeat = new System.Windows.Forms.Timer()) {
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(GuiTray).GetField("Op", flags).SetValue(tray, operation);
            typeof(GuiTray).GetField("TrayUpdateTimer", flags).SetValue(tray, monitor);
            typeof(GuiTray).GetField("HeartbeatTimer", flags).SetValue(tray, heartbeat);
            monitor.Start(); heartbeat.Start();
            bool exited = false;
            tray.ThreadExit += (sender, args) => exited = true;
            Exception failure = null;
            try { tray.ExitThread(); } catch(Exception e) { failure = e; }
            Check(failure is IOException && failure.Message == "simulated rejected mode", "GUI exit preserves hardware failure");
            Check(!monitor.Enabled && !heartbeat.Enabled, "failed GUI exit stops both timers");
            Check(exited && !program.IsEnabled, "failed GUI exit completes shutdown and stops program");
            Check(calls.Contains("gpu:75"), "failed GUI exit still restores GPU state");
        }
        failMode = false;
        Set(platform, "TemperatureUse", new[] {false});
        Throws(() => program.Run("Regression"), "no enabled sensors rejected");
        Check(!program.IsEnabled, "no-sensor start leaves program stopped");
    }
    static void ProgramTransactions() {
        InstallBios(); Config.FanLevelNeedManual = false;
        var platform = (Platform)FormatterServices.GetUninitializedObject(typeof(Platform));
        Set(platform, "Fans", Fans(victus)); Set(platform, "System", FakeSettings());
        Set(platform, "Profile", victus);
        Set(platform, "TemperatureUse", new[] {true});
        Set(platform, "Temperature", new IPlatformReadComponent[] {
            new Proxy<IPlatformReadComponent>((name, args) => {
                if(name == "Update") return true;
                if(name == "GetValue") return 50;
                throw new Exception("Unexpected transaction sensor call: " + name);
            }).Value
        });
        Config.FanProgram["Transactions"] = new FanProgramData("Transactions", BiosData.FanMode.Performance,
            BiosData.GpuPowerLevel.Maximum, new SortedDictionary<byte,byte[]> {{0,new byte[] {35,36}}});
        using(var entered = new ManualResetEventSlim())
        using(var release = new ManualResetEventSlim()) {
            bool block = false;
            Exception updateError = null, terminateError = null;
            var program = new FanProgram(platform, (severity, message) => {
                if(block && severity == FanProgram.Severity.Notice) {
                    entered.Set();
                    if(!release.Wait(10000)) throw new Exception("Update release timed out");
                }
            });
            program.Run("Transactions"); calls.Clear(); block = true;
            var update = new Thread(() => { try { program.Update(); } catch(Exception e) { updateError = e; } }) {IsBackground=true};
            var terminate = new Thread(() => { try { program.Terminate(); } catch(Exception e) { terminateError = e; } }) {IsBackground=true};
            try {
                update.Start();
                Check(entered.Wait(5000), "background update reached resolved target");
                object controlLock = typeof(Hw).GetField("BiosControlLock", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
                bool acquired = Monitor.TryEnter(controlLock);
                if(acquired) Monitor.Exit(controlLock);
                Check(!acquired, "program update holds shared control transaction lock");
                terminate.Start();
            } finally {
                block = false; release.Set();
                if((update.ThreadState & System.Threading.ThreadState.Unstarted) == 0) Check(update.Join(5000), "update worker exits");
                if((terminate.ThreadState & System.Threading.ThreadState.Unstarted) == 0) Check(terminate.Join(5000), "termination worker exits");
            }
            Check(updateError == null && terminateError == null, "serialized update and termination succeed");
            Check(!program.IsEnabled, "background update cannot revive terminated program");
            Check(calls.FindLastIndex(c => c.StartsWith("levels:")) < calls.FindLastIndex(c => c == "mode:48:True"), "no target follows Auto restoration");
            calls.Clear(); platform.Fans.MaintainControl();
            Equal(0, calls.Count, "concurrent Auto cannot restart heartbeat renewal");
        }
        // A pending startup must not override a later explicit user choice.
        var operation = (GuiOp)FormatterServices.GetUninitializedObject(typeof(GuiOp));
        typeof(GuiOp).GetMethod("CancelAutoConfig", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(operation, null);
        operation.AutoConfig(); // Other fields are unset: any hardware access fails.
        Equal(0, calls.Count, "cancelled startup makes no hardware call");
        Set(operation, "FullPower", true);
        typeof(GuiOp).GetField("Platform", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(operation, platform);
        typeof(GuiOp).GetField("Program", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(operation, new FanProgram(platform, (severity,message) => {}));
        var context = (GuiTray)FormatterServices.GetUninitializedObject(typeof(GuiTray));
        typeof(GuiOp).GetField("Context", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(operation, context);
        operation.PowerChange();
        Check(!operation.FullPower, "power-source cache updates while no program runs");
    }
    static void ControlHeartbeat() {
        InstallBios();
        var timeout = typeof(Hw).GetMethod("BiosControlTimeout", BindingFlags.NonPublic | BindingFlags.Static);
        Equal(120, (int)timeout.Invoke(null, new object[] { PowerLineStatus.Online }), "AC OEM control timeout");
        Equal(600, (int)timeout.Invoke(null, new object[] { PowerLineStatus.Offline }), "battery OEM control timeout");
        var fans = Fans(victus);
        fans.MaintainControl();
        Equal(0, calls.Count, "startup Auto does not acquire OEM fan control");
        fans.SetLevels(new byte[] {35,36}); calls.Clear();
        fans.MaintainControl();
        Equal("heartbeat", calls.Single(), "fixed control renews watchdog");
        calls.Clear(); fans.RestoreAutomatic(BiosData.FanMode.Performance);
        Equal("mode:49:True", calls.Single(), "Auto changes mode without renewing control or writing RPM");
        Check(fans.GetCountdown() > 0 && fans.GetCountdown() <= 600, "Auto displays estimated handback countdown");
        typeof(Hw).GetField("LastBiosHeartbeatTimeout", BindingFlags.NonPublic | BindingFlags.Static).SetValue(null, 600);
        typeof(Hw).GetField("LastBiosHeartbeat", BindingFlags.NonPublic | BindingFlags.Static).SetValue(null,
            Stopwatch.GetTimestamp() - 121L * Stopwatch.Frequency);
        Check(fans.GetCountdown() > 0, "battery countdown persists beyond AC timeout");
        calls.Clear(); fans.MaintainControl(); fans.MaintainControl();
        Equal(0, calls.Count, "Auto refresh never extends watchdog");
        typeof(Hw).GetField("LastBiosHeartbeat", BindingFlags.NonPublic | BindingFlags.Static).SetValue(null,
            Stopwatch.GetTimestamp() - 601L * Stopwatch.Frequency);
        Equal(0, fans.GetCountdown(), "expired watchdog hides countdown");
        fans.SetOff(true); calls.Clear(); fans.RestoreAutomatic(BiosData.FanMode.Default);
        Equal("max:True|mode:48:True", string.Join("|", calls), "Off handback restores cooling without renewing watchdog");
        calls.Clear(); fans.MaintainControl();
        Equal(0, calls.Count, "Off-to-Auto stops renewal");
        foreach(byte[] levels in new[] {new byte[] {0,35}, new byte[] {35,0}}) {
            fans.SetLevels(levels);
            Check(!fans.GetOff(), "one stopped fan is not the both-off UI state");
            calls.Clear(); fans.RestoreAutomatic(BiosData.FanMode.Default);
            Equal("max:True|mode:48:True", string.Join("|", calls), "Auto restores cooling for either stopped fan");
        }
        calls.Clear(); failLevels = true;
        Throws(() => fans.SetLevels(new byte[] {0,35}), "partial zero-target failure reported");
        Equal("heartbeat|max:True|mode:48:True", string.Join("|", calls), "failed zero-target write immediately restores cooling");
        failLevels = false; calls.Clear(); fans.MaintainControl();
        Equal(0, calls.Count, "failed zero-target write cannot renew manual control");
        fans.SetLevels(new byte[] {35,35}); failHeartbeat = true;
        Throws(() => fans.SetLevels(new byte[] {40,40}), "failed heartbeat reported");
        failHeartbeat = false; calls.Clear(); fans.MaintainControl();
        Equal(0, calls.Count, "failed acquisition clears previous manual renewal");
        fans.SetLevels(new byte[] {35,35}); failMax = true;
        Throws(() => fans.SetMax(true), "failed Max reported");
        failMax = false; calls.Clear(); fans.MaintainControl();
        Equal(0, calls.Count, "failed Max clears previous manual renewal");
        fans.SetLevels(new byte[] {0,35}); fans.SetLevels(new byte[] {35,35});
        calls.Clear(); fans.RestoreAutomatic(BiosData.FanMode.Default);
        Equal("mode:48:True", string.Join("|", calls), "successful nonzero targets clear stopped-fan state");
        fans.SetMax(true); calls.Clear(); fans.MaintainControl();
        Equal("heartbeat", calls.Single(), "Max renews watchdog");
        fans.SetMax(false); calls.Clear(); fans.MaintainControl();
        Equal(0, calls.Count, "Max off stops renewal");
        Fans(PlatformProfile.ForProduct("8A14")).MaintainControl();
        Equal("heartbeat", calls.Single(), "legacy heartbeat behavior preserved");

        // Exercise the real Settings path without running its WMI constructor.
        var settings = (Settings)FormatterServices.GetUninitializedObject(typeof(Settings));
        Set(settings, "BaseBoard", new Dictionary<string,string> {{"Product","8BD4"}});
        calls.Clear();
        Hw.Bios = new Proxy<IBiosCtl>((name,args) => {
            if(name == "GetFanCount") {calls.Add("heartbeat"); return (byte)2;}
            if(name == "SetBacklight") {calls.Add("keyboard:" + (byte)(BiosData.Backlight)args[0]); return null;}
            if(name == "SetColorTable") {calls.Add("color"); return null;}
            throw new Exception("Unexpected keyboard call: " + name);
        }).Value;
        settings.SetKbdBacklight(true);
        Equal("heartbeat|keyboard:228", string.Join("|",calls), "first keyboard click acquires write access");
        calls.Clear(); settings.SetKbdColor(new BiosData.ColorTable(new[] {0,0,0,0}));
        Equal("heartbeat|color", string.Join("|",calls), "first color change acquires write access");
        calls.Clear(); fans.MaintainControl();
        Equal(0,calls.Count,"keyboard access does not enable continuous fan heartbeat");
    }
    static void ModeNames() {
        foreach(BiosData.FanMode mode in new[] {BiosData.FanMode.Performance, BiosData.FanMode.Turbo, BiosData.FanMode.L7})
            Equal("Performance", victus.GetFanModeName(mode), "canonical performance label");
        foreach(BiosData.FanMode mode in new[] {BiosData.FanMode.Default, BiosData.FanMode.Balance, BiosData.FanMode.L2})
            Equal("Default", victus.GetFanModeName(mode), "canonical default label");
        Equal<string>(null, victus.GetFanModeName((BiosData.FanMode)254), "unknown mode has no false label");
        using(var form = new System.Windows.Forms.Form())
        using(var combo = new System.Windows.Forms.ComboBox()) {
            form.Controls.Add(combo);
            combo.BindingContext = new System.Windows.Forms.BindingContext();
            combo.DisplayMember = "Text"; combo.ValueMember = "Value";
            combo.DataSource = new[] {new {Text="Default",Value="Default"}, new {Text="Performance",Value="Performance"}};
            combo.SelectedValue = victus.GetFanModeName(BiosData.FanMode.Turbo);
            Equal("Performance", combo.Text, "WinForms refresh retains Performance text");
        }
    }
    static void Keyboard() {
        var retry = typeof(BiosCtl).GetMethod("SetBacklightVerified", BindingFlags.Static | BindingFlags.NonPublic);
        int writes = 0, waits = 0;
        Action<BiosData.Backlight> write = value => writes++;
        Func<BiosData.Backlight> read = () => writes < 2 ? (BiosData.Backlight)0 : BiosData.Backlight.On;
        Action wait = () => waits++;
        retry.Invoke(null, new object[] {BiosData.Backlight.On, write, read, wait});
        Equal(2, writes, "dropped first keyboard command retried");
        Equal(2, waits, "readback waits for EC completion");
        writes = 0; read = () => (BiosData.Backlight)0;
        retry.Invoke(null, new object[] {BiosData.Backlight.Off, write, read, wait});
        Equal(1, writes, "zero off readback normalized without extra writes");
        writes = 0;
        Throws(() => retry.Invoke(null, new object[] {BiosData.Backlight.On, write, read, wait}), "permanent keyboard mismatch reported");
        Equal(3, writes, "retry bounded");
        writes = 0;
        write = value => { if(++writes == 1) throw new BiosException("temporary WMI failure"); };
        read = () => BiosData.Backlight.On;
        retry.Invoke(null, new object[] {BiosData.Backlight.On, write, read, wait});
        Equal(2, writes, "transient keyboard WMI failure retried");
        var payloadMethod = typeof(BiosCtl).GetMethod("CreateColorTablePayload", BindingFlags.Static | BindingFlags.NonPublic);
        byte[] original = Enumerable.Repeat((byte)0x5A, 128).ToArray(); original[0] = 3;
        var table = new BiosData.ColorTable(new[] {0x112233,0x112233,0x112233,0x112233});
        var payload = (byte[])payloadMethod.Invoke(null, new object[] {original, table, BiosData.KbdType.Standard});
        Check(payload.Take(25).SequenceEqual(original.Take(25)) && payload.Skip(37).SequenceEqual(original.Skip(37)), "keyboard metadata preserved");
        Check(!payload.Skip(25).Take(12).SequenceEqual(original.Skip(25).Take(12)), "all color slots updated");
        Equal((byte)0x5A, original[25], "source buffer unmodified");
        foreach(var type in new[] {BiosData.KbdType.OneZoneWithNumPad, BiosData.KbdType.OneZoneWithoutNumPad}) {
            var single = (byte[])payloadMethod.Invoke(null, new object[] {original, table, type});
            Check(single.Take(25).SequenceEqual(original.Take(25)) && single.Skip(28).SequenceEqual(original.Skip(28)), "single-zone write preserves other slots and metadata");
            Check(single.Skip(25).Take(3).SequenceEqual(payload.Skip(25).Take(3)), "single-zone write changes first RGB slot");
        }
        Throws(() => payloadMethod.Invoke(null, new object[] {new byte[4], table, BiosData.KbdType.Standard}), "truncated keyboard buffer rejected");
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
        info.FileName = Assembly.GetExecutingAssembly().Location;
        info.Arguments = "--progress";
        using(var process = Process.Start(info)) {
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if(!process.WaitForExit(15000)) { process.Kill(); throw new Exception("Redirected progress hung"); }
            Equal(0, process.ExitCode, "redirected fan-program progress exit");
            Equal("", stderr.Result, "redirected progress has no invalid-handle error");
        }
    }
    [STAThread]
    public static int Main(string[] args) {
        try {
            typeof(Cli).GetProperty("IsInitialized").GetSetMethod(true).Invoke(null, new object[] { true });
            Config.LocaleInit("Fallback");
            if(args.Length == 1 && args[0] == "--progress") {
                typeof(CliOp).GetMethod("PrintProgTick", BindingFlags.Static | BindingFlags.NonPublic)
                    .Invoke(null, new object[] {1});
                return 0;
            }
            if(args.Length == 2 && args[0] == "--reload") {
                Config.FilePath = args[1]; Config.Load(); Config.ResolveTemperatureSensors(victus);
                Equal((byte)0xB2, Sensor("GPU").Register, "fresh GPU register");
                return 0;
            }
            work = Path.Combine(Path.GetTempPath(), "OmenMonRegression-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work); Config.FilePath = Path.Combine(work, "sensors.xml");
            foreach(Action test in new Action[] { FanControl, ControlHeartbeat, ModeNames, Keyboard, GpuPower, SensorPersistence, FanPrograms, ProgramTransactions, EcReports }) {
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
