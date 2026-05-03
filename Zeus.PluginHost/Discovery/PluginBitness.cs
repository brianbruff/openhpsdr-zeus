// PluginBitness.cs — CPU word size + arch family decoded from binary headers.
//
// Sourced from PE/COFF Machine, ELF EI_CLASS+e_machine, or Mach-O magic+cputype.

namespace Zeus.PluginHost.Discovery;

public enum PluginBitness
{
    Unknown,
    X86,
    X64,
    Arm64,
    Arm32,
}
