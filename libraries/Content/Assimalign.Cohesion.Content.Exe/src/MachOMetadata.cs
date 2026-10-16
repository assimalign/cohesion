using System.Collections.Generic;

namespace Assimalign.Cohesion.Content.Exe;

/// <summary>Thin or universal Mach-O header metadata.</summary>
public sealed class MachOMetadata
{
    internal MachOMetadata() { }

    /// <summary>Gets whether the outer header is universal/fat.</summary>
    public bool IsUniversal { get; internal set; }

    /// <summary>Gets cputype, preserving its raw bits; zero for universal headers.</summary>
    public uint CpuType { get; internal set; }

    /// <summary>Gets cpusubtype; zero for universal headers.</summary>
    public uint CpuSubtype { get; internal set; }

    /// <summary>Gets filetype; zero for universal headers.</summary>
    public uint FileType { get; internal set; }

    /// <summary>Gets header flags; zero for universal headers.</summary>
    public uint Flags { get; internal set; }

    /// <summary>Gets sizeofcmds for thin images.</summary>
    public uint SizeOfCommands { get; internal set; }

    /// <summary>Gets command identifiers and sizes without decoding payloads.</summary>
    public IReadOnlyList<MachOLoadCommand> LoadCommands { get; internal set; } = [];

    /// <summary>Gets the universal architecture table, empty for thin images.</summary>
    public IReadOnlyList<MachOArchitecture> Architectures { get; internal set; } = [];
}
