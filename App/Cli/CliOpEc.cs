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
            // Save the console color to be restored later
            ConsoleColor originalColor = Console.ForegroundColor;

            // Set up the data array
            var data = new EcMonData[byte.MaxValue];

            // Generate default filename if not provided
            if(filename == null) {
                filename = "ecmon_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".log";
            }
            // Use current working directory for relative paths (do not rewrite)

            // Create an event handler to break out of the perpetual loop
            Console.CancelKeyPress += (sender, eventArgs) => {
                IsStop = true;
                eventArgs.Cancel = true;
            };

            // Failsafe: on process exit, try to save if not already saved
            bool saved = false;
            AppDomain.CurrentDomain.ProcessExit += (s, e) => {
                if(saved) return;
                try { if(filename != null) SaveEcReport(data, filename); } catch { }
            };

            // Populate the data array with initial readings
            for(int register = 0; register < data.Length; register++) {

                data[register].Values = new List<byte>();
                data[register].UserChangeIndex = new List<int>();
                data[register].Values.Add(Hw.EcGetByte((byte) register));

            }

            // Display instructions
            Console.WriteLine("Monitoring Embedded Controller...");
            Console.WriteLine("Press Ctrl+C, Enter, or Esc to stop and save log.");
            Console.WriteLine();
            Thread.Sleep(2000); // Give user time to read

            // Start a background thread that waits for Enter (robust across consoles)
            bool inputThreadRunning = true;
            Thread inputThread = new Thread(() => {
                try {
                    Console.ReadLine(); // Blocks until Enter is pressed
                } catch { }
                if(inputThreadRunning)
                    IsStop = true;
            });
            inputThread.IsBackground = true;
            inputThread.Start();

            // Start a secondary key polling thread to capture Esc/Enter
            bool keyThreadRunning = true;
            Thread keyThread = new Thread(() => {
                while(keyThreadRunning && !IsStop) {
                    try {
                        if(Console.KeyAvailable) {
                            var key = Console.ReadKey(true);
                            if(key.Key == ConsoleKey.Escape || key.Key == ConsoleKey.Enter) {
                                IsStop = true;
                                break;
                            }
                        }
                    } catch { }
                    Thread.Sleep(50);
                }
            });
            keyThread.IsBackground = true;
            keyThread.Start();

            // Track how many registers changed in this reading cycle
            int readingIndex = 0;

            // Path for the user change marker file
            string markerFile = Path.Combine(Path.GetTempPath(), "OmenMon_UserChange.tmp");
            // Path for a stop marker file (external way to stop)
            string stopFile = Path.Combine(Path.GetTempPath(), "OmenMon_EcMon_Stop.tmp");
            DateTime lastMarkerCheck = DateTime.MinValue;

            while(!IsStop) { // Continually keep adding new data

                readingIndex++;

                // Stop via external marker
                if(File.Exists(stopFile)) {
                    try { File.Delete(stopFile); } catch { }
                    IsStop = true;
                    break;
                }
                int changesInThisCycle = 0;
                bool userChangedSettings = false;

                // Check if GUI/tray signaled a user change (check marker file)
                if(File.Exists(markerFile)) {
                    try {
                        DateTime markerTime = File.GetLastWriteTime(markerFile);
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

                    if(value != previousValue) {
                        changesInThisCycle++;
                        // Mark this as a user change if flag is set
                        if(userChangedSettings) {
                            data[register].UserChangeIndex.Add(readingIndex);
                        }
                    }

                    if(value != data[register].Values[0])
                        data[register].Show = true; // Note the values that have changed
                }

                Cli.PrintEcReport(data); // Update the report
                Thread.Sleep(Config.EcMonInterval); // at specified intervals

            }

            // Stop the input thread
            inputThreadRunning = false;
            try { inputThread.Join(500); } catch { }
            // Stop the key polling thread
            keyThreadRunning = false;
            try { keyThread.Join(200); } catch { }

            // Clear screen one last time before showing save message
            Console.Clear();

            // Show that we're stopping
            Console.WriteLine("Stopping monitor...");
            if(filename != null) {
                Console.WriteLine("Saving log file to: " + filename);
                Console.WriteLine();
                // Save the report when exiting only if a filename was specified
                SaveEcReport(data, filename);
                saved = true;
            } else {
                Console.WriteLine("No filename specified. Skipping save as per documentation.");
            }

            // Restore the console color to the original
            Console.ForegroundColor = originalColor;

            // Close the Embedded Controller
            Hw.Ec.Close();

            // Return to caller

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
                    report.Append(Conv.GetString((ushort) row, 5, 10));
                    report.Append("  ");
                    for(int register = 0; register < data.Length; register++) {
                        if(!data[register].Show)
                            continue;
                        report.Append(Conv.GetString(data[register].Values[row], 2, 16));
                        // Mark user-initiated change on this time step
                        if(data[register].UserChangeIndex != null && data[register].UserChangeIndex.Contains(row))
                            report.Append("(C)");
                        report.Append(" ");
                    }
                    if(report[report.Length - 1] == ' ')
                        report.Remove(report.Length - 1, 1);
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
