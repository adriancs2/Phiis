using System;
using System.IO;

namespace Phiis.Core
{
    /// <summary>
    /// Minimal reader for the PE header of a Windows executable: CPU architecture and linker version.
    /// The linker version tells which Visual C++ toolset built the binary (14.2x = VS2019, 14.3x+ = VS2022).
    /// </summary>
    public sealed class PeImageInfo
    {
        public string Architecture { get; private set; }
        public int LinkerMajor { get; private set; }
        public int LinkerMinor { get; private set; }

        public static PeImageInfo Read(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new BinaryReader(stream))
            {
                if (reader.ReadUInt16() != 0x5A4D) // "MZ"
                {
                    throw new InvalidDataException("Not a Windows executable: " + path);
                }

                stream.Position = 0x3C;
                uint peOffset = reader.ReadUInt32();
                stream.Position = peOffset;
                if (reader.ReadUInt32() != 0x00004550) // "PE\0\0"
                {
                    throw new InvalidDataException("Invalid PE signature: " + path);
                }

                ushort machine = reader.ReadUInt16();
                stream.Position = peOffset + 4 + 20; // skip signature + COFF header
                reader.ReadUInt16(); // optional header magic
                byte linkerMajor = reader.ReadByte();
                byte linkerMinor = reader.ReadByte();

                return new PeImageInfo
                {
                    Architecture = MachineToName(machine),
                    LinkerMajor = linkerMajor,
                    LinkerMinor = linkerMinor
                };
            }
        }

        private static string MachineToName(ushort machine)
        {
            switch (machine)
            {
                case 0x014C: return "x86";
                case 0x8664: return "x64";
                case 0xAA64: return "arm64";
                default: return "unknown(0x" + machine.ToString("X4") + ")";
            }
        }

        /// <summary>Human-readable toolset name derived from the linker version.</summary>
        public string Toolset
        {
            get
            {
                if (LinkerMajor == 14)
                {
                    if (LinkerMinor >= 30) return "VS17 (Visual Studio 2022+)";
                    if (LinkerMinor >= 20) return "VS16 (Visual Studio 2019)";
                    if (LinkerMinor >= 10) return "VC15 (Visual Studio 2017)";
                    return "VC14 (Visual Studio 2015)";
                }
                if (LinkerMajor == 11) return "VC11 (Visual Studio 2012)";
                if (LinkerMajor == 9) return "VC9 (Visual Studio 2008)";
                if (LinkerMajor == 6) return "VC6 (Visual C++ 6.0)";
                return "Linker " + LinkerMajor + "." + LinkerMinor;
            }
        }
    }
}
