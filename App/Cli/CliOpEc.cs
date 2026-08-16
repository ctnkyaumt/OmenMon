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

            // Save the console color to be restored later
            ConsoleColor originalColor = Console.ForegroundColor;
            bool interactive = !Console.IsInputRedirected && !Console.IsOutputRedirected;

            // Set up the data array
            var data = new EcMonData[256];

            // Generate a writable, predictable default filename.
            if(string.IsNullOrWhiteSpace(filename)) {
                string logDirectory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "OmenMon Logs");
                try {
                    Directory.CreateDirectory(logDirectory);
                } catch {
                    logDirectory = Path.GetTempPath();
                }
                filename = Path.Combine(logDirectory,
                    "ecmon_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".log");
            }
            filename = Path.GetFullPath(filename);

            // Create an event handler to break out of the perpetual loop
            Console.CancelKeyPress += (sender, eventArgs) => {
                IsStop = true;
                eventArgs.Cancel = true;
            };

            // Populate the data array with initial readings
            for(int register = 0; register < data.Length; register++) {

                data[register].Values = new List<byte>();
                data[register].UserChangeIndex = new List<int>();
                data[register].Values.Add(Hw.EcGetByte((byte) register));

            }

            // Display instructions
            Console.WriteLine("Monitoring Embedded Controller...");
            Console.WriteLine(interactive ?
                "Press Ctrl+C, Enter, or Esc to stop and save log." :
                "Press Ctrl+C to stop and save log.");
            Console.WriteLine("Log file: " + filename);
            Console.WriteLine();
            if(interactive)
                Thread.Sleep(1000);

            // (Key checking is done inline in the main loop to avoid
            // Console threading conflicts with Console.Clear)

            // Track how many registers changed in this reading cycle
            int readingIndex = 0;

            // Path for the user change marker file
            string markerFile = Path.Combine(Path.GetTempPath(), "OmenMon_UserChange.tmp");
            // Path for a stop marker file (external way to stop)
            string stopFile = Path.Combine(Path.GetTempPath(), "OmenMon_EcMon_Stop.tmp");
            DateTime lastMarkerCheck = File.Exists(markerFile) ?
                File.GetLastWriteTimeUtc(markerFile) : DateTime.MinValue;
            try { if(File.Exists(stopFile)) File.Delete(stopFile); } catch { }

            try {
                // Create the log immediately, then checkpoint it periodically.
                SaveEcReport(data, filename);

                while(!IsStop) { // Continually keep adding new data

                    readingIndex++;

                    // Check for key presses (Esc or Enter to stop)
                    if(interactive) try {
                        while(Console.KeyAvailable) {
                            var key = Console.ReadKey(true);
                            if(key.Key == ConsoleKey.Escape || key.Key == ConsoleKey.Enter) {
                                IsStop = true;
                                break;
                            }
                        }
                    } catch { }

                    if(IsStop)
                        break;

                    // Stop via external marker
                    if(File.Exists(stopFile)) {
                        try { File.Delete(stopFile); } catch { }
                        IsStop = true;
                        break;
                    }
                    bool userChangedSettings = false;

                    // Check if GUI/tray signaled a user change (check marker file)
                    if(File.Exists(markerFile)) {
                        try {
                            DateTime markerTime = File.GetLastWriteTimeUtc(markerFile);
                            // If marker was updated since last check, user made a change
                            if(markerTime > lastMarkerCheck) {
                                userChangedSettings = true;
                                lastMarkerCheck = markerTime;
                            }
                        } catch { }
                    }

                    for(int register = 0; register < data.Length; register++) {
                        if(IsStop)
                            break;

                        byte value = Hw.EcGetByte((byte) register);
                        byte previousValue = data[register].Values[data[register].Values.Count - 1];
                    
                        data[register].Values.Add(value);

                        if(value != previousValue && userChangedSettings) {
                            data[register].UserChangeIndex.Add(readingIndex);
                        }

                        if(value != data[register].Values[0])
                            data[register].Show = true; // Note the values that have changed
                    }

                    if(interactive)
                        Cli.PrintEcReport(data); // Update the report

                    if(readingIndex % 10 == 0)
                        SaveEcReport(data, filename);

                    Thread.Sleep(Config.EcMonInterval); // at specified intervals

                }
            } finally {
                if(interactive)
                    try { Console.Clear(); } catch { }

                Console.WriteLine("Stopping monitor...");
                Console.WriteLine("Saved log file to: " + filename);
                Console.WriteLine();
                SaveEcReport(data, filename);

                // Restore the console color to the original
                Console.ForegroundColor = originalColor;

                // Close the Embedded Controller
                Hw.Ec.Close();
            }

        }

        // Saves the embedded controller monitoring report to a file
        // Format matches documentation (#\Reg header, columns: registers; rows: time steps)
        private static void SaveEcReport(EcMonData[] data, string filename) {
            try {
                var report = new StringBuilder();

                // Header
                report.Append("#\\Reg  ");
                for(int register = 0; register < data.Length; register++) {
                    if(!data[register].Show)
                        continue;
                    report.Append(Conv.GetString((byte) register, 2, 16));
                    report.Append(" ");
                }
                if(report[report.Length - 1] == ' ')
                    report.Remove(report.Length - 1, 1);
                report.AppendLine();

                // Rows: nnnnn (time step) followed by values for shown registers
                int rows = data[0].Values.Count;
                for(int row = 0; row < rows; row++) {
                    report.Append(Conv.GetString((uint) row, 5, 10));
                    report.Append("  ");
                    var userChanges = new List<string>();
                    for(int register = 0; register < data.Length; register++) {
                        if(!data[register].Show)
                            continue;
                        report.Append(Conv.GetString(data[register].Values[row], 2, 16));
                        // Record user-initiated change on this time step for later
                        if(data[register].UserChangeIndex != null && data[register].UserChangeIndex.Contains(row))
                            userChanges.Add(Conv.GetString((byte)register, 2, 16));
                        report.Append(" ");
                    }
                    if(report[report.Length - 1] == ' ')
                        report.Remove(report.Length - 1, 1);

                    // Append user changes as a comment to preserve column alignment
                    if(userChanges.Count > 0) {
                        report.Append("  // User change: ");
                        report.Append(string.Join(", ", userChanges));
                    }

                    report.AppendLine();
                }

                // Write file (relative paths resolve to current working directory)
                File.WriteAllText(filename, report.ToString());
            } catch {
                App.Error("ErrFileSave");
            }
        }
#endregion

    }

}
