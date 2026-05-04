// BinaryHeaderSnifferTests.cs — synthetic-header round-trip for every
// (platform, bitness) combination we claim to recognise.
//
// Each test builds a tiny byte[] that mimics the start of a real PE / ELF /
// Mach-O file, writes it to a temp path, runs the sniffer, and asserts the
// classification. Synthetic-only — no real binaries on disk for the unit
// suite, that's what RealWorldScanTests is for.

using System;
using System.Buffers.Binary;
using System.IO;
using Xunit;
using Zeus.PluginHost.Discovery;

namespace Zeus.PluginHost.Tests.Discovery;

public sealed class BinaryHeaderSnifferTests : IDisposable
{
    private readonly BinaryHeaderSniffer _sniffer = new();
    private readonly string _scratchDir;

    public BinaryHeaderSnifferTests()
    {
        _scratchDir = Path.Combine(Path.GetTempPath(), "zeus-sniffer-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_scratchDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_scratchDir))
                Directory.Delete(_scratchDir, recursive: true);
        }
        catch { /* best-effort */ }
    }

    private string WriteScratch(string suffix, byte[] bytes)
    {
        var path = Path.Combine(_scratchDir, Guid.NewGuid().ToString("N") + suffix);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    // ----- PE / COFF -----

    private static byte[] BuildPe(ushort machine, int peOffset = 0x80, int totalSize = 0x100)
    {
        if (peOffset + 6 > totalSize) totalSize = peOffset + 6;
        var buf = new byte[totalSize];
        // DOS stub: 'M','Z'
        buf[0] = 0x4D; buf[1] = 0x5A;
        // e_lfanew at offset 0x3C
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0x3C, 4), (uint)peOffset);
        // PE signature 'P','E',0,0
        buf[peOffset + 0] = 0x50;
        buf[peOffset + 1] = 0x45;
        buf[peOffset + 2] = 0x00;
        buf[peOffset + 3] = 0x00;
        // COFF Machine
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(peOffset + 4, 2), machine);
        return buf;
    }

    [Fact]
    public void Pe32_I386_ClassifiesAsWindowsX86()
    {
        var path = WriteScratch(".dll", BuildPe(0x014C));
        var r = _sniffer.Sniff(path);
        Assert.Equal(PluginPlatform.Windows, r.Platform);
        Assert.Equal(PluginBitness.X86, r.Bitness);
    }

    [Fact]
    public void Pe32Plus_X64_ClassifiesAsWindowsX64()
    {
        var path = WriteScratch(".dll", BuildPe(0x8664));
        var r = _sniffer.Sniff(path);
        Assert.Equal(PluginPlatform.Windows, r.Platform);
        Assert.Equal(PluginBitness.X64, r.Bitness);
    }

    [Fact]
    public void Pe_Arm64_ClassifiesAsWindowsArm64()
    {
        var path = WriteScratch(".dll", BuildPe(0xAA64));
        var r = _sniffer.Sniff(path);
        Assert.Equal(PluginPlatform.Windows, r.Platform);
        Assert.Equal(PluginBitness.Arm64, r.Bitness);
    }

    [Fact]
    public void Pe_Arm32_ClassifiesAsWindowsArm32()
    {
        var path = WriteScratch(".dll", BuildPe(0x01C4));
        var r = _sniffer.Sniff(path);
        Assert.Equal(PluginPlatform.Windows, r.Platform);
        Assert.Equal(PluginBitness.Arm32, r.Bitness);
    }

    // ----- ELF -----

    private static byte[] BuildElf(byte eiClass, ushort eMachine, byte eiData = 1)
    {
        // 64 bytes is enough to clear the e_machine field at offset 0x12.
        var buf = new byte[0x80];
        buf[0] = 0x7F;
        buf[1] = 0x45; // 'E'
        buf[2] = 0x4C; // 'L'
        buf[3] = 0x46; // 'F'
        buf[4] = eiClass;
        buf[5] = eiData;
        buf[6] = 1; // EI_VERSION
        // e_type at offset 0x10 — irrelevant, leave zero.
        if (eiData == 2)
            BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(0x12, 2), eMachine);
        else
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(0x12, 2), eMachine);
        return buf;
    }

    [Fact]
    public void Elf64_X86_64_ClassifiesAsLinuxX64()
    {
        var path = WriteScratch(".so", BuildElf(2, 0x3E));
        var r = _sniffer.Sniff(path);
        Assert.Equal(PluginPlatform.Linux, r.Platform);
        Assert.Equal(PluginBitness.X64, r.Bitness);
    }

    [Fact]
    public void Elf32_I386_ClassifiesAsLinuxX86()
    {
        var path = WriteScratch(".so", BuildElf(1, 0x03));
        var r = _sniffer.Sniff(path);
        Assert.Equal(PluginPlatform.Linux, r.Platform);
        Assert.Equal(PluginBitness.X86, r.Bitness);
    }

    [Fact]
    public void Elf64_Aarch64_ClassifiesAsLinuxArm64()
    {
        var path = WriteScratch(".so", BuildElf(2, 0xB7));
        var r = _sniffer.Sniff(path);
        Assert.Equal(PluginPlatform.Linux, r.Platform);
        Assert.Equal(PluginBitness.Arm64, r.Bitness);
    }

    // ----- Mach-O -----

    private static byte[] BuildMachO(uint magic, uint cpuType)
    {
        var buf = new byte[0x40];
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0, 4), magic);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(4, 4), cpuType);
        return buf;
    }

    [Fact]
    public void MachO32_I386_ClassifiesAsMacX86()
    {
        var path = WriteScratch(".dylib", BuildMachO(0xFEEDFACE, 0x07));
        var r = _sniffer.Sniff(path);
        Assert.Equal(PluginPlatform.MacOS, r.Platform);
        Assert.Equal(PluginBitness.X86, r.Bitness);
    }

    [Fact]
    public void MachO64_X86_64_ClassifiesAsMacX64()
    {
        var path = WriteScratch(".dylib", BuildMachO(0xFEEDFACF, 0x01000007));
        var r = _sniffer.Sniff(path);
        Assert.Equal(PluginPlatform.MacOS, r.Platform);
        Assert.Equal(PluginBitness.X64, r.Bitness);
    }

    [Fact]
    public void MachO64_Arm64_ClassifiesAsMacArm64()
    {
        var path = WriteScratch(".dylib", BuildMachO(0xFEEDFACF, 0x0100000C));
        var r = _sniffer.Sniff(path);
        Assert.Equal(PluginPlatform.MacOS, r.Platform);
        Assert.Equal(PluginBitness.Arm64, r.Bitness);
    }

    [Fact]
    public void MachO_FatUniversal_NotesItButLeavesBitnessUnknown()
    {
        // Fat magic 0xCAFEBABE — sub-architecture enumeration is Phase B.
        var buf = new byte[0x40];
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0, 4), 0xCAFEBABE);
        var path = WriteScratch(".dylib", buf);
        var r = _sniffer.Sniff(path);
        Assert.Equal(PluginPlatform.MacOS, r.Platform);
        Assert.Equal(PluginBitness.Unknown, r.Bitness);
        Assert.NotNull(r.Notes);
        Assert.Contains("fat universal", r.Notes!, StringComparison.OrdinalIgnoreCase);
    }

    // ----- Unknown / hostile inputs -----

    [Fact]
    public void RandomBytes_ClassifiesAsUnknown()
    {
        var rng = new Random(1234);
        var buf = new byte[256];
        rng.NextBytes(buf);
        // Stamp the first two bytes to something definitely-not-magic.
        buf[0] = 0x00; buf[1] = 0x11; buf[2] = 0x22; buf[3] = 0x33;
        var path = WriteScratch(".bin", buf);
        var r = _sniffer.Sniff(path);
        Assert.Equal(PluginPlatform.Unknown, r.Platform);
        Assert.Equal(PluginBitness.Unknown, r.Bitness);
        Assert.NotNull(r.Notes);
    }

    [Fact]
    public void TooShortFile_ClassifiesAsUnknown()
    {
        var path = WriteScratch(".bin", new byte[] { 0x4D, 0x5A, 0x00 });
        var r = _sniffer.Sniff(path);
        Assert.Equal(PluginPlatform.Unknown, r.Platform);
        Assert.Equal(PluginBitness.Unknown, r.Bitness);
        Assert.NotNull(r.Notes);
        Assert.Contains("too small", r.Notes!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EmptyFile_ClassifiesAsUnknown()
    {
        var path = WriteScratch(".bin", Array.Empty<byte>());
        var r = _sniffer.Sniff(path);
        Assert.Equal(PluginPlatform.Unknown, r.Platform);
        Assert.Equal(PluginBitness.Unknown, r.Bitness);
        Assert.NotNull(r.Notes);
    }

    [Fact]
    public void Pe_WithCorruptELfanew_NotesUnknownMachine()
    {
        // 'MZ' present, but e_lfanew points way past our read budget.
        var buf = new byte[0x100];
        buf[0] = 0x4D; buf[1] = 0x5A;
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0x3C, 4), 0xDEADBEEF);
        var path = WriteScratch(".dll", buf);
        var r = _sniffer.Sniff(path);
        Assert.Equal(PluginPlatform.Windows, r.Platform);
        Assert.Equal(PluginBitness.Unknown, r.Bitness);
        Assert.NotNull(r.Notes);
    }
}
