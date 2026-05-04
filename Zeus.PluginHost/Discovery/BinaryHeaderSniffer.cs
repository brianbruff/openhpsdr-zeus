// BinaryHeaderSniffer.cs — recognises PE/COFF, ELF, and Mach-O headers.
//
// The sniffer reads at most the first 4 KiB of a candidate file and decodes
// just enough of the header to pin down (Platform, Bitness). It deliberately
// does NOT validate optional headers, section tables, or signatures — those
// belong to the C++ sidecar at load time. Goals:
//
//   1. Cheap: one open, one Read, no FileShare contention.
//   2. Cross-platform: a Linux dev box can sniff a Windows .dll without
//      ever invoking the loader.
//   3. Defensive: any I/O or parse error returns Unknown plus a Notes string.
//      Never throw for malformed input.
//
// Format references (used informally, not load-bearing):
//   - PE/COFF: Microsoft "PE Format" specification.
//   - ELF:     System V ABI Chapter 4.
//   - Mach-O:  Apple "Mach-O Programming Topics" header definitions.
//
// Endianness assumption: all three formats we care about today are
// little-endian on the architectures Zeus targets. ELF EI_DATA is consulted
// so a hypothetical big-endian binary would surface in Notes rather than be
// silently misread.

using System;
using System.Buffers.Binary;
using System.IO;

namespace Zeus.PluginHost.Discovery;

public sealed class BinaryHeaderSniffer : IBinaryHeaderSniffer
{
    private const int ReadBudgetBytes = 4096;
    private const int MinFileBytes = 64;

    // PE/COFF Machine field constants.
    private const ushort ImageFileMachineI386  = 0x014C;
    private const ushort ImageFileMachineAmd64 = 0x8664;
    private const ushort ImageFileMachineArm64 = 0xAA64;
    private const ushort ImageFileMachineArm32 = 0x01C4; // ARMNT (Thumb-2)

    // ELF e_machine constants.
    private const ushort ElfMachine386    = 0x03;
    private const ushort ElfMachineX8664  = 0x3E;
    private const ushort ElfMachineArm32  = 0x28;
    private const ushort ElfMachineAarch64 = 0xB7;

    // Mach-O magic constants. Native-endian flavours match a host with the
    // same byte order as the binary; the byte-swapped flavours show up when
    // a tool is asked to read a binary built for the other endianness.
    private const uint MachOMagic32     = 0xFEEDFACE;
    private const uint MachOMagic64     = 0xFEEDFACF;
    private const uint MachOCigam32     = 0xCEFAEDFE;
    private const uint MachOCigam64     = 0xCFFAEDFE;
    private const uint MachOFatMagic    = 0xCAFEBABE;
    private const uint MachOFatCigam    = 0xBEBAFECA;

    // Mach-O cputype values (CPU_ARCH_ABI64 = 0x01000000).
    private const uint CpuTypeI386     = 0x07;
    private const uint CpuTypeX8664    = 0x01000007;
    private const uint CpuTypeArm      = 0x0C;
    private const uint CpuTypeArm64    = 0x0100000C;

    public SniffResult Sniff(string filePath)
    {
        byte[] buf;
        long fileLength;
        try
        {
            // FileShare.ReadWrite | Delete keeps us from blocking IDEs or
            // antivirus that may already have the file open.
            using var fs = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: ReadBudgetBytes,
                FileOptions.SequentialScan);

            fileLength = fs.Length;
            if (fileLength < MinFileBytes)
            {
                return Unknown($"file too small to be a plugin binary ({fileLength} bytes)");
            }

            var toRead = (int)Math.Min(fileLength, ReadBudgetBytes);
            buf = new byte[toRead];
            int total = 0;
            while (total < toRead)
            {
                var n = fs.Read(buf, total, toRead - total);
                if (n <= 0) break;
                total += n;
            }
            if (total < MinFileBytes)
            {
                return Unknown($"short read ({total} bytes)");
            }
            if (total < toRead)
            {
                // Resize buf to actual bytes read so Span lengths are honest.
                Array.Resize(ref buf, total);
            }
        }
        catch (Exception ex)
        {
            return Unknown($"i/o error: {ex.Message}");
        }

        // PE/COFF — must check before generic byte tests because 'M','Z' is
        // also valid ASCII text but only meaningful here at the very start.
        if (buf.Length >= 2 && buf[0] == 0x4D && buf[1] == 0x5A)
        {
            return SniffPe(buf);
        }

        // ELF: 0x7F 'E' 'L' 'F'.
        if (buf.Length >= 4 && buf[0] == 0x7F && buf[1] == 0x45 && buf[2] == 0x4C && buf[3] == 0x46)
        {
            return SniffElf(buf);
        }

        // Mach-O thin or fat. Magic at offset 0 is a uint32; both endian
        // flavours encode the same logical value, so test all four bytes.
        if (buf.Length >= 4)
        {
            var magic = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(0, 4));
            if (magic is MachOMagic32 or MachOMagic64 or MachOCigam32 or MachOCigam64
                or MachOFatMagic or MachOFatCigam)
            {
                return SniffMachO(buf, magic);
            }
        }

        return Unknown("not a recognized binary format");
    }

    private static SniffResult SniffPe(byte[] buf)
    {
        // e_lfanew: uint32 LE at offset 0x3C in DOS stub points at PE header.
        if (buf.Length < 0x40)
        {
            return new SniffResult(PluginPlatform.Windows, PluginBitness.Unknown,
                "PE: header too short to contain e_lfanew");
        }
        var peOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(0x3C, 4));

        // Clamp: the spec puts no formal upper bound, but a sane plugin DLL
        // keeps the PE header within the first few KiB. A wildly large
        // offset means the file is corrupt or not actually a PE.
        if (peOffset < 0 || peOffset > ReadBudgetBytes - 6)
        {
            return new SniffResult(PluginPlatform.Windows, PluginBitness.Unknown,
                $"PE: e_lfanew={peOffset} outside read budget");
        }
        if (buf.Length < peOffset + 6)
        {
            return new SniffResult(PluginPlatform.Windows, PluginBitness.Unknown,
                "PE: header truncated before COFF signature");
        }

        // PE signature: 'P','E',0,0
        if (buf[peOffset] != 0x50 || buf[peOffset + 1] != 0x45
            || buf[peOffset + 2] != 0x00 || buf[peOffset + 3] != 0x00)
        {
            return new SniffResult(PluginPlatform.Windows, PluginBitness.Unknown,
                "PE: signature 'PE\\0\\0' missing");
        }

        // COFF Machine field is the next uint16 after the 4-byte PE sig.
        var machine = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(peOffset + 4, 2));
        var bitness = machine switch
        {
            ImageFileMachineI386  => PluginBitness.X86,
            ImageFileMachineAmd64 => PluginBitness.X64,
            ImageFileMachineArm64 => PluginBitness.Arm64,
            ImageFileMachineArm32 => PluginBitness.Arm32,
            _ => PluginBitness.Unknown,
        };
        var notes = bitness == PluginBitness.Unknown
            ? $"PE: unknown machine 0x{machine:X4}"
            : null;
        return new SniffResult(PluginPlatform.Windows, bitness, notes);
    }

    private static SniffResult SniffElf(byte[] buf)
    {
        // EI_CLASS at offset 4: 1 = ELF32, 2 = ELF64.
        // EI_DATA at offset 5: 1 = LSB, 2 = MSB.
        if (buf.Length < 0x14)
        {
            return new SniffResult(PluginPlatform.Linux, PluginBitness.Unknown,
                "ELF: header truncated before e_machine");
        }
        var eiClass = buf[4];
        var eiData = buf[5];

        // e_machine is at offset 0x12, uint16 in the file's endianness.
        var eMachine = eiData == 2
            ? BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(0x12, 2))
            : BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(0x12, 2));

        var bitness = (eiClass, eMachine) switch
        {
            (1, ElfMachine386)    => PluginBitness.X86,
            (2, ElfMachineX8664)  => PluginBitness.X64,
            (1, ElfMachineArm32)  => PluginBitness.Arm32,
            (2, ElfMachineAarch64) => PluginBitness.Arm64,
            _ => PluginBitness.Unknown,
        };
        string? notes = null;
        if (bitness == PluginBitness.Unknown)
        {
            notes = $"ELF: unrecognised class={eiClass} machine=0x{eMachine:X4}";
        }
        else if (eiData == 2)
        {
            notes = "ELF: big-endian binary (rare for plugin targets)";
        }
        return new SniffResult(PluginPlatform.Linux, bitness, notes);
    }

    private static SniffResult SniffMachO(byte[] buf, uint magic)
    {
        switch (magic)
        {
            case MachOFatMagic:
            case MachOFatCigam:
                return new SniffResult(
                    PluginPlatform.MacOS,
                    PluginBitness.Unknown,
                    "Mach-O fat universal, sub-architecture not enumerated yet");

            case MachOMagic32:
            case MachOMagic64:
            {
                // cputype is uint32 LE at offset 4 for native-endian magic.
                if (buf.Length < 8)
                {
                    return new SniffResult(PluginPlatform.MacOS, PluginBitness.Unknown,
                        "Mach-O: header truncated before cputype");
                }
                var cpuType = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(4, 4));
                var bitness = cpuType switch
                {
                    CpuTypeI386   => PluginBitness.X86,
                    CpuTypeX8664  => PluginBitness.X64,
                    CpuTypeArm    => PluginBitness.Arm32,
                    CpuTypeArm64  => PluginBitness.Arm64,
                    _ => PluginBitness.Unknown,
                };
                string? notes = bitness == PluginBitness.Unknown
                    ? $"Mach-O: unknown cputype 0x{cpuType:X8}"
                    : null;
                return new SniffResult(PluginPlatform.MacOS, bitness, notes);
            }

            case MachOCigam32:
            case MachOCigam64:
            {
                // Byte-swapped Mach-O is unusual on the platforms we support
                // (would imply running PPC tooling). Surface it but don't try
                // to decode further.
                return new SniffResult(
                    PluginPlatform.MacOS,
                    PluginBitness.Unknown,
                    $"Mach-O: byte-swapped magic 0x{magic:X8}, not parsed");
            }

            default:
                // Unreachable: caller already gated on the magic constants.
                return Unknown($"Mach-O dispatch fell through, magic=0x{magic:X8}");
        }
    }

    private static SniffResult Unknown(string note)
        => new(PluginPlatform.Unknown, PluginBitness.Unknown, note);
}
