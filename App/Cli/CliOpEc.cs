  //\\   OmenMon: Hardware Monitoring & Control Utility
 //  \\  Copyright © 2023 Piotr Szczepański * License: GPL3
     //  https://omenmon.github.io/

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using OmenMon.Hardware.Ec;
using OmenMon.Library;

namespace OmenMon.AppCli {

    // Implements the main operation loop in the application's CLI mode
    // This part covers Embedded Controller-specific routines
    public static partial class CliOp {

        // Structure to collect Embedded Controller monitoring data
        public struct EcMonData {
            public bool Show;
            public List<byte> Values;
            public List<int> UserChangeIndex; // Track indices where user likely changed settings
        }

#region Embedded Controller Information Retrieval
        // Prints out the value of a specific register parsed from the command-line
        private static void EcGet(string registerString) {
            bool isWord = registerString.EndsWith("(2)") ? true : false;
            string registerStringParsed = isWord ? registerString.Split('(')[0] : registerString;
            byte register = 0xFF; // Cannot leave unassigned
            bool registerSet = false;

            // Try to parse the value as a string identifier first
            // This may easily fail if there is no such identifier
            try {
                register = (byte) Enum.Parse(typeof(EmbeddedControllerData.Register), registerStringParsed);
                registerSet = true;
            } catch { }

            // Try to parse the register value from the argument
            if(!registerSet && !Conv.GetByte(registerStringParsed, out register))

                // Could not parse the register value to read from
                App.Error("ErrNeedRegisterRead|DataSyntaxReg|DataSyntaxOrTwo");

            else {

                if(isWord)
                    EcGetWord(register); // Get a word
                else
                    EcGetByte(register); // Get a byte

            }

        }

        // Prints out the value of a specific byte-sized register
        private static void EcGetByte(byte register) {
            byte b = Hw.EcGetByte(register);
            Cli.PrintEcResult(false, false, register, b);
        }

        // Prints out the value of a little-endian word stored in two consecutive registers
        private static void EcGetWord(byte register) {
            ushort w = Hw.EcGetWord(register);
            Cli.PrintEcResult(false, true, register, w);
        }

        // Prints out the values of all Embedded Controller registers in a table format
        private static void EcGetTable() {
            Cli.PrintColor((ConsoleColor) Cli.Color.TableHeader, "0x _0 _1 _2 _3 _4 _5 _6 _7 _8 _9 _a _b _c _d _e _f" + Environment.NewLine);
            for(int high = 0; high <= 0xF0; high += 0x10) {
                Cli.PrintColor((ConsoleColor) Cli.Color.TableHeader, Convert.ToString(high >> 4, 16) + "_ ");
                for(int low = 0; low <= 0xF; low++) {
                    byte b = Hw.EcGetByte((byte) (high | low));
                    Cli.PrintValueHexColor(b);
                    Console.Write(" ");
                }
                Console.WriteLine();
            }
        }
#endregion

#region Embedded Controller Assignment Operations
        // Sets the value of a specific register parsed from the command-line
        private static void EcSet(string registerString, string valueString) {
            bool isWord = registerString.EndsWith("(2)") ? true : false;
            string registerStringParsed = isWord ? registerString.Split('(')[0] : registerString;
            byte register = 0xFF; // Cannot leave unassigned
            bool registerSet = false;

            // Try to parse the value as a string identifier first
            // This may easily fail if there is no such identifier
            try {
                register = (byte) Enum.Parse(typeof(EmbeddedControllerData.Register), registerStringParsed);
                registerSet = true;
            } catch { }

            // Try to parse the register value from the argument
            if(!registerSet && !Conv.GetByte(registerStringParsed, out register))

                // Could not parse the register value to read from
                App.Error("ErrNeedRegisterWrite|DataSyntaxReg|DataSyntaxOrTwo");

            else {

                // Asked to set a word value (two consecutive registers)
                if(isWord) {

                    // Try to parse the word to be written from the argument
                    ushort value;
                    if(Conv.GetWord(valueString, out value))
                        EcSetWord(register, value); // Set a word
                    else
                        // Could not parse the word value to be written
                        App.Error("ErrNeedValueWord|DataSyntaxWord");

                // Asked to set a byte value (single register, default case)
                } else {

                    // Try to parse the byte to be written from the argument
                    byte value;
                    if(Conv.GetByte(valueString, out value))
                        EcSetByte(register, value); // Set a byte
                    else
                        // Could not parse the byte value to be written
                        App.Error("ErrNeedValueByte|DataSyntaxByte");

                }

            }

        }

        // Sets the value of a specific byte-sized register
        private static void EcSetByte(byte register, byte value) {
            Cli.PrintEcResult(true, false, register, value);
            Hw.EcSetByte(register, value);
        }

        // Sets the value of a little-endian word stored in two consecutive registers
        private static void EcSetWord(byte register, ushort value) {
            Cli.PrintEcResult(true, true, register, value);
            Hw.EcSetWord(register, value);
        }
#endregion

#region Embedded Controller Monitoring
        // Monitors the Embedded Controller registers for changes and reports,
        // optionally saving to a file as well
        private static void EcMon(string filename = null) {
            IsStop = false;
            bool interactive = !Console.IsInputRedirected && !Console.IsOutputRedirected;
            ConsoleColor originalColor = ConsoleColor.Gray;
            if(interactive)
                try { originalColor = Console.ForegroundColor; } catch { interactive = false; }
            var data = new EcMonData[256];
            ConsoleCancelEventHandler cancel = (sender, args) => {
                IsStop = true;
                args.Cancel = true;
            };
            Console.CancelKeyPress += cancel;
            try {
                if(string.IsNullOrWhiteSpace(filename)) {
                    string directory = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "OmenMon Logs");
                    try { Directory.CreateDirectory(directory); }
                    catch { directory = Path.GetTempPath(); }
                    filename = Path.Combine(directory, "ecmon_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + ".log");
                }
                filename = Path.GetFullPath(filename);
                string markerFile = Path.Combine(Path.GetTempPath(), "OmenMon_UserChange.tmp");
                string stopFile = Path.Combine(Path.GetTempPath(), "OmenMon_EcMon_Stop.tmp");
                DateTime lastMarker = File.Exists(markerFile) ?
                    File.GetLastWriteTimeUtc(markerFile) : DateTime.MinValue;
                try { if(File.Exists(stopFile)) File.Delete(stopFile); } catch { }

                for(int register = 0; register < data.Length; register++) {
                    data[register].Values = new List<byte>();
                    data[register].UserChangeIndex = new List<int>();
                    data[register].Values.Add(Hw.EcGetByte((byte) register));
                }

                // Refuse to monitor without a usable output file.
                if(!SaveEcReport(data, filename))
                    return;
                Console.WriteLine("Monitoring Embedded Controller...");
                Console.WriteLine(interactive ? "Press Ctrl+C, Enter, or Esc to stop." : "Press Ctrl+C to stop.");
                Console.WriteLine("Log file: " + filename);
                int readingIndex = 0;
                try {
                    while(!IsStop) {
                        if(interactive) {
                            try {
                                while(Console.KeyAvailable) {
                                    ConsoleKey key = Console.ReadKey(true).Key;
                                    if(key == ConsoleKey.Escape || key == ConsoleKey.Enter)
                                        IsStop = true;
                                }
                            } catch { interactive = false; }
                        }
                        if(File.Exists(stopFile)) {
                            try { File.Delete(stopFile); } catch { }
                            IsStop = true;
                        }
                        if(IsStop)
                            break;

                        bool userChanged = false;
                        try {
                            if(File.Exists(markerFile)) {
                                DateTime changed = File.GetLastWriteTimeUtc(markerFile);
                                userChanged = changed > lastMarker;
                                lastMarker = changed;
                            }
                        } catch { }
                        readingIndex++;
                        // Finish this sample even if cancellation arrives mid-scan.
                        for(int register = 0; register < data.Length; register++) {
                            byte value = Hw.EcGetByte((byte) register);
                            byte previous = data[register].Values[data[register].Values.Count - 1];
                            data[register].Values.Add(value);
                            if(value != previous && userChanged)
                                data[register].UserChangeIndex.Add(readingIndex);
                            if(value != data[register].Values[0])
                                data[register].Show = true;
                        }
                        if(interactive)
                            Cli.PrintEcReport(data);
                        if(readingIndex % 10 == 0 && !SaveEcReport(data, filename))
                            break;
                        Thread.Sleep(Config.EcMonInterval);
                    }
                } finally {
                    if(interactive)
                        try { Console.Clear(); } catch { }
                    Console.WriteLine("Stopping monitor...");
                    if(SaveEcReport(data, filename))
                        Console.WriteLine("Saved log file to: " + filename);
                }
            } finally {
                Console.CancelKeyPress -= cancel;
                if(interactive)
                    try { Console.ForegroundColor = originalColor; } catch { }
                Hw.Ec.Close();
            }
        }

        // Each checkpoint contains the baseline and all complete samples. A temporary
        // file is flushed before atomically replacing the previous checkpoint.
        private static bool SaveEcReport(EcMonData[] data, string filename) {
            string temporary = null;
            try {
                int rows = data.Length == 0 ? 0 : int.MaxValue;
                foreach(EcMonData register in data)
                    rows = Math.Min(rows, register.Values?.Count ?? 0);
                if(rows == 0)
                    throw new InvalidDataException("No complete EC sample.");

                var report = new StringBuilder("#\\Reg");
                for(int register = 0; register < data.Length; register++)
                    report.Append(" ").Append(Conv.GetString((byte) register, 2, 16));
                report.AppendLine();
                for(int row = 0; row < rows; row++) {
                    report.Append(Conv.GetString((uint) row, 5, 10)).Append(" ");
                    var userChanges = new List<string>();
                    for(int register = 0; register < data.Length; register++) {
                        report.Append(" ").Append(Conv.GetString(data[register].Values[row], 2, 16));
                        if(data[register].UserChangeIndex?.Contains(row) == true)
                            userChanges.Add(Conv.GetString((byte) register, 2, 16));
                    }
                    if(userChanges.Count > 0)
                        report.Append("  // User change: ").Append(string.Join(", ", userChanges));
                    report.AppendLine();
                }

                filename = Path.GetFullPath(filename);
                temporary = filename + "." + Guid.NewGuid().ToString("N") + ".tmp";
                byte[] bytes = new UTF8Encoding(false).GetBytes(report.ToString());
                using(var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                    FileShare.None, 4096, FileOptions.WriteThrough)) {
                    file.Write(bytes, 0, bytes.Length);
                    file.Flush(true);
                }
                if(File.Exists(filename))
                    File.Replace(temporary, filename, null);
                else
                    File.Move(temporary, filename);
                return true;
            } catch(Exception e) {
                Environment.ExitCode = 1;
                App.Error("ErrFileSave", e);
                return false;
            } finally {
                if(temporary != null)
                    try { if(File.Exists(temporary)) File.Delete(temporary); } catch { }
            }
        }
#endregion

    }

}
