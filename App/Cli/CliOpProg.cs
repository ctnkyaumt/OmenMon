  //\\   OmenMon: Hardware Monitoring & Control Utility
 //  \\  Copyright © 2023 Piotr Szczepański * License: GPL3
     //  https://omenmon.github.io/

using System;
using System.Diagnostics;
using System.Threading;
using OmenMon.AppGui;
using OmenMon.Hardware.Platform;
using OmenMon.Library;

namespace OmenMon.AppCli {

    // Implements the main operation loop in the application's CLI mode
    // This part covers fan control program-specific routines
    public static partial class CliOp {

#region Program List
        // Lists all available fan control programs
        private static void ProgList() {

                // Report if no programs available
                if(Config.FanProgram.Count == 0)
                    App.Error("ErrProgNone");

            else

                // Iterate through each program and list it
                foreach(string name in Config.FanProgram.Keys)
                    Cli.PrintProgList(name);

        }
#endregion

#region Program Run
        // Run a specified fan control program
        public static void ProgRun(string name) {

            // Make sure the specified program exists
            string program = name;
            if(!Config.FanProgram.ContainsKey(program)) {

                // Try to substitute spaces for underscores if not
                program = program.Replace('_', ' ');
                if(!Config.FanProgram.ContainsKey(program)) {

                    // Give up if still not found
                    App.Error("ErrProgName");
                    return;

                    }

            }

            // List the program that is about to be run
            Cli.PrintProgList(program, true);

            // Save the console color to be restored later
            ConsoleColor originalColor = Console.ForegroundColor;

            // Initialize the BIOS and the Embedded Controller
            Hw.BiosInit();
            Hw.EcInit();

            // Initialize the fan program with the hardware platform
            FanProgram Program = new FanProgram(new Platform(), Cli.PrintProgMessage);

            // The loop owns hardware cleanup; cancellation only requests it.
            ConsoleCancelEventHandler cancel = (sender, eventArgs) => {
                eventArgs.Cancel = true;
                IsStop = true;
            };
            IsStop = false;
            Console.CancelKeyPress += cancel;

            try {
                if(!Program.Run(program)) {
                    App.Error("ErrProgName");
                    return;
                }
                while(!IsStop) {
                    int tick = -1;
                    while(!IsStop && ++tick < Config.UpdateProgramInterval) {
                        PrintProgTick(tick);
                        Thread.Sleep(Config.GuiTimerInterval);
                    }
                    if(!IsStop)
                        Program.Update();
                }
            } finally {
                Console.CancelKeyPress -= cancel;
                try {
                    Program.Terminate();
                } finally {
                    try { Console.ForegroundColor = originalColor; } catch { }
                }
            }

        }

        internal static void PrintProgTick(int tick) {
            if(Console.IsOutputRedirected) return;
            try {
                Cli.PrintColor((ConsoleColor) Cli.Color.Deemphasis, "# ");
                Cli.PrintColor((ConsoleColor) Cli.Color.Emphasis, Conv.GetString((uint) tick, 2, 10));
                Cli.PrintColor((ConsoleColor) Cli.Color.Deemphasis, " / " + Conv.GetString((uint) Config.UpdateProgramInterval, 2, 10));
                Console.SetCursorPosition(0, Console.CursorTop);
            } catch(System.IO.IOException) { }
        }
#endregion

    }

}
