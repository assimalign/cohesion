namespace Assimalign.Cohesion.Content.Exe;

/// <summary>A universal Mach-O architecture-table entry.</summary>
public sealed class MachOArchitecture
{
    internal MachOArchitecture() { }

    /// <summary>Gets cputype, preserving raw bits.</summary>
    public uint CpuType { get; internal set; }

    /// <summary>Gets cpusubtype.</summary>
    public uint CpuSubtype { get; internal set; }

    /// <summary>Gets the architecture offset relative to the universal file.</summary>
    public ulong Offset { get; internal set; }

    /// <summary>Gets the architecture byte size.</summary>
    public ulong Size { get; internal set; }

    /// <summary>Gets the base-two alignment exponent.</summary>
    public uint Alignment { get; internal set; }
}
