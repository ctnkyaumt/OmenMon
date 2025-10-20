  //\\   OmenMon: Hardware Monitoring & Control Utility
 //  \\  Copyright © 2023 Piotr Szczepański * License: GPL3
     //  https://omenmon.github.io/

using System;
using OmenMon.Library;

namespace OmenMon.Hardware.Ec {

    // Defines Embedded Controller constants, variables, and structures
    // for subsequent use by the Embedded Controller-handling routines
    public abstract class EmbeddedControllerData {

#region Embedded Controller Access Data
        // Commands sent to the Embedded Controller
        protected enum Command : byte {
            Read     = 0x80,  // RD_EC
            Write    = 0x81,  // WR_EC
            BurstOn  = 0x82,  // BE_EC
            BurstOff = 0x83,  // BD_EC
            Query    = 0x84   // QR_EC
        }

        // Port numbers to send commands and data to
        protected enum Port : byte {
            Command  = 0x66,  // EC_SC
            Data     = 0x62   // EC_DATA
        }

        // Status values returned by the Embedded Controller
        [Flags]
        protected enum Status : byte {
            OutFull  = 0x01,  // Bit #0: EC_OBF
            InFull   = 0x02,  // Bit #1: EC_IBF
                              // Bit #2: n/a
            Command  = 0x08,  // Bit #3: CMD
            Burst    = 0x10,  // Bit #4: BURST
            SciEvt   = 0x20,  // Bit #5: SCI_EVT
            SmiEvt   = 0x40   // Bit #6: SMI_EVT
                              // Bit #7: n/a
        }
#endregion

#region Embedded Controller Register Information
        // Embedded Controller register identifiers
        // Labels based on ACPI DSDT for HP 08A14 (Ralph 21C2) except:
        // SMxx - Single 32-bit register starting with SMD0 (until SMEF)
        // BFCD, BADD, MCUS, MBRN, MBCW - Second byte for word-sized registers BFCC, BADC, MCUR, MBRM, MBCV
        // BXXX, GSXX, SHXX, SXXX - Composite register where all identifiers start with the same letter
        // RXnc - Composite registers with varying identifiers, where <n> - # of bits, <c> - sequential count
        // Xxxx - Registers with no DSDT label where purpose identified, and <xxx> is a descriptive string

        public enum Register : byte {
            // R0x02: Unknown
            VOFS = 0x02,  // Version Offset?

            // R0x10: Unknown
            PRTS = 0x10,  // (Value 0x9)

            // R0x18: Flag Byte for Current Settings
            // Note: HPCM moved to 0x95 on newer systems

            // R0x20: Main Embedded Controller Flags
            AUDI = 0x20,  // Bit #2: MUTE, #6: OMEN Audio?
            
            // R0x28-R0x36: Temperature-Related Flags
            OMCC = 0x28,  // Bit #0: Manual Fan Control
            
            // R0x57-R0x68: Standard Temperature Sensors
            CPUT = 0x57,  // Temperature: CPU [°C]
            SKIN = 0x58,  // Temperature: SKIN [°C]
            RTMP = 0x59,  // Temperature: Other [°C] (PCH?)
            TMP1 = 0x5A,  // Temperature: Other [°C]
            DGTS = 0x5B,  // Temperature: Unknown [°C]
            
            // R0xB0-R0xB9: More Temperature Sensors
            TEMP_CPU = 0xB0,  // Temperature: CPU [°C] (Duplicate)
            TEMP_GPU = 0xB2,  // Temperature: GPU [°C]
            TEMP_SSD = 0xB7,  // Temperature: SSD [°C]
            
            // R0x5C-R0x5F: TNT# Temperature Sensors
            TNT1 = 0x5C,  // Temperature: TNT1 (unused?)
            TNT2 = 0x5D,  // Temperature: TNT2 Unknown
            TNT3 = 0x5E,  // Temperature: TNT3 Storage
            TNT4 = 0x5F,  // Temperature: TNT4 Storage
            TNT5 = 0x60,  // Temperature: TNT5 Unknown
            TNT6 = 0x61,  // Temperature: TNT6 (unused?)
            TNT7 = 0x62,  // Temperature: TNT7 (unused?)
            TNT8 = 0x63,  // Temperature: TNT8 (unused?)
            
            // R0x64-R0x68: More Temperature Sensors
            SKNM = 0x64,  // Temperature: SKNM Unknown
            CHTP = 0x65,  // Temperature: CHTP Unknown
            VRMP = 0x66,  // Temperature: VRMP Unknown
            TCHG = 0x67,  // Temperature: TCHG Unknown
            TGPU = 0x68,  // Temperature: GPU [°C]

            // R0x6C-R0x6F: Fan Speeds (Word)
            RPM1 = 0x6C,  // Fan Speed: 1 (CPU) [RPM]
            RPM2 = 0x6E,  // Fan Speed: 0 (possibly unused)
            RPM3 = 0x70,  // Fan Speed: 2 (GPU) [RPM]
            RPM4 = 0x72,  // Fan Speed: 3 (possibly unused)

            // R0x80-R0x82: Fan Control Bytes
            SRP1 = 0x80,  // Fan Speed Rate Percent: CPU [%]
            SRP2 = 0x81,  // Fan Speed Rate Percent: GPU [%]
            SRP3 = 0x82,  // Fan Speed Rate Percent: 3 (possibly unused)

            // R0x90-0x92: Fan Speed Bytes (Indirect)
            XSS1 = 0x90,  // Fan Speed Setting: 1 (CPU)
            XSS2 = 0x91,  // Fan Speed Setting: 2 (GPU)
            XSS3 = 0x92,  // Fan Speed Setting: 3 (possibly unused)

            // R0x95: HP Cooling Mode (Performance Mode)
            HPCM = 0x95,  // HP Cooling Mode register (modern systems)

            // R0x98-0x9A: Fan Rate Bytes (Current)
            XGS1 = 0x98,  // Fan Speed Global: 1 (CPU) [%]
            XGS2 = 0x99,  // Fan Speed Global: 2 (GPU) [%]
            XGS3 = 0x9A,  // Fan Speed Global: 3 (possibly unused)

            // R0xA8: Countdown Timer
            XFCD = 0xA8,  // Fan Countdown [s]

            // R0xD0: Fan Control Flag
            SFAN = 0xD0,  // Bit #0: Off (silent), #1: Manual (constant), #2: Maximum

            // R0xF0-R0xF7: Graphics-related Flags
            ZMB0 = 0xF0,  // User Lid Close
            ZMB1 = 0xF1,  // Bit #3: _HID?
            RX2E = 0xF2,  // Bit #0: ZPDD, #7: ENPA
            HDMI = 0xF7,
            NVDS = 0xF8,
        }
#endregion

    }

}
