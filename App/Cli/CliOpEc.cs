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

            // Ensure filename has proper path (same directory as executable if no path specified)
            if(!Path.IsPathRooted(filename)) {
                filename = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, filename);
            }

            // Create an event handler to break out of the perpetual loop
            Console.CancelKeyPress += (sender, eventArgs) => {
                IsStop = true;
                eventArgs.Cancel = true; // Prevent immediate termination
            };

            // Populate the data array with initial readings
            for(int register = 0; register < data.Length; register++) {

                data[register].Values = new List<byte>();
                data[register].UserChangeIndex = new List<int>();
                data[register].Values.Add(Hw.EcGetByte((byte) register));

            }

            // Start a background thread to check for keyboard input
            bool keyboardThreadRunning = true;
            Thread keyboardThread = new Thread(() => {
                while(keyboardThreadRunning && !IsStop) {
                    if(Console.KeyAvailable) {
                        var key = Console.ReadKey(true);
                        if(key.Key == ConsoleKey.Enter || key.Key == ConsoleKey.Escape) {
                            IsStop = true;
                            break;
                        }
                    }
                    Thread.Sleep(50); // Check every 50ms
                }
            });
            keyboardThread.Start();

            // Track how many registers changed in this reading cycle
            int readingIndex = 0;

            // Path for the user change marker file
            string markerFile = Path.Combine(Path.GetTempPath(), "OmenMon_UserChange.tmp");
            DateTime lastMarkerCheck = DateTime.MinValue;

            while(!IsStop) { // Continually keep adding new data

                readingIndex++;
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

                for(int register = 0; register < data.Length; register++) 
                if(!IsStop) {

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

            // Stop the keyboard thread
            keyboardThreadRunning = false;
            keyboardThread.Join(500);

            // Clear screen one last time before showing save message
            Console.Clear();

            // Always save the report when exiting
            SaveEcReport(data, filename);

            // Restore the console color to the original
            Console.ForegroundColor = originalColor;

            // Close the Embedded Controller
            Hw.Ec.Close();

            // Wait for user to see the save message
            Console.WriteLine("Press any key to exit...");
            Console.ReadKey(true);

        }

        // Saves the embedded controller monitoring report to a file
        // Format matches CLI display: register names on left, values in rows
        private static void SaveEcReport(EcMonData[] data, string filename) {
            try {
                var report = new StringBuilder();

                // Add header with timestamp
                report.AppendLine("OmenMon Embedded Controller Monitor Log");
                report.AppendLine("Generated: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                report.AppendLine();

                // Iterate through all the registers
                for(int register = 0; register < data.Length; register++) {

                    // Skip if set not to be shown
                    if(!data[register].Show)
                        continue;

                    // Output the register name, if available
                    string registerName = Enum.GetName(typeof(EmbeddedControllerData.Register), register);
                    if(registerName != null)
                        report.Append(registerName.PadRight(4));
                    else
                        report.Append("    ");
                    
                    report.Append(" ");

                    // Output the register number
                    report.Append(Conv.GetString((byte) register, 2));
                    report.Append(" ");

                    // Used for comparison, since we only output the differences
                    byte? lastValue = null;

                    // Iterate through the readouts for each register
                    for(int readNow = 0; readNow < data[register].Values.Count; readNow++) {
                        // Do not output the value if it is the same as before
                        if(data[register].Values[readNow] == lastValue) {
                            report.Append(" ..");
                            continue;
                        }

                        // Update the last value to current value
                        lastValue = data[register].Values[readNow];

                        // Output a separator between values
                        if(readNow > 0)
                            report.Append(" ");

                        // Print out the current value
                        report.Append(Conv.GetString(data[register].Values[readNow], 2, 16));

                        // Add change indicator (C) if this reading was marked as a user change
                        if(data[register].UserChangeIndex.Contains(readNow)) {
                            report.Append("(C)");
                        }
                    }
                    report.AppendLine();
                }

                // Save the report to a file
                File.WriteAllText(filename, report.ToString());

                // Notify user of successful save
                Console.WriteLine();
                Console.WriteLine("Log saved to: " + filename);

            } catch(Exception ex) {

                // Report an error if the file could not be saved
                Console.WriteLine();
                Console.WriteLine("Error saving log file: " + ex.Message);
            }
        }
#endregion

    }

}
