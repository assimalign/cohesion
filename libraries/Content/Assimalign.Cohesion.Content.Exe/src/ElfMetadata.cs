using System.Collections.Generic;

namespace Assimalign.Cohesion.Content.Exe;

/// <summary>ELF identification, header and table metadata.</summary>
public sealed class ElfMetadata
{
    internal ElfMetadata() { }

    /// <summary>Gets EI_CLASS (1 or 2).</summary>
    public byte Class { get; internal set; }

    /// <summary>Gets EI_DATA (1 or 2).</summary>
    public byte DataEncoding { get; internal set; }

    /// <summary>Gets EI_OSABI.</summary>
    public byte OsAbi { get; internal set; }

    /// <summary>Gets EI_ABIVERSION.</summary>
    public byte AbiVersion { get; internal set; }

    /// <summary>Gets the raw e_type.</summary>
    public ushort Type { get; internal set; }

    /// <summary>Gets the raw e_machine.</summary>
    public ushort Machine { get; internal set; }

    /// <summary>Gets e_entry.</summary>
    public ulong EntryPoint { get; internal set; }

    /// <summary>Gets e_flags.</summary>
    public uint Flags { get; internal set; }

    /// <summary>Gets e_phoff.</summary>
    public ulong ProgramHeaderOffset { get; internal set; }

    /// <summary>Gets e_shoff.</summary>
    public ulong SectionHeaderOffset { get; internal set; }

    /// <summary>Gets the resolved section-name table index.</summary>
    public uint SectionNameTableIndex { get; internal set; }

    /// <summary>Gets the PT_INTERP path, when present.</summary>
    public string? Interpreter { get; internal set; }

    /// <summary>Gets program headers in file order.</summary>
    public IReadOnlyList<ElfProgramHeader> ProgramHeaders { get; internal set; } = [];

    /// <summary>Gets named section headers in file order.</summary>
    public IReadOnlyList<ElfSectionHeader> Sections { get; internal set; } = [];
}
