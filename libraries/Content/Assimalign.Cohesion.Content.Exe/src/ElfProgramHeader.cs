namespace Assimalign.Cohesion.Content.Exe;

/// <summary>An ELF program header.</summary>
public sealed class ElfProgramHeader
{
    internal ElfProgramHeader() { }

    /// <summary>Gets p_type.</summary>
    public uint Type { get; internal set; }

    /// <summary>Gets p_flags.</summary>
    public uint Flags { get; internal set; }

    /// <summary>Gets p_offset relative to the file start.</summary>
    public ulong Offset { get; internal set; }

    /// <summary>Gets p_vaddr.</summary>
    public ulong VirtualAddress { get; internal set; }

    /// <summary>Gets p_paddr.</summary>
    public ulong PhysicalAddress { get; internal set; }

    /// <summary>Gets p_filesz.</summary>
    public ulong FileSize { get; internal set; }

    /// <summary>Gets p_memsz.</summary>
    public ulong MemorySize { get; internal set; }

    /// <summary>Gets p_align.</summary>
    public ulong Alignment { get; internal set; }
}
