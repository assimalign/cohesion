namespace Assimalign.Cohesion.Content.Exe;

/// <summary>An ELF section header.</summary>
public sealed class ElfSectionHeader
{
    internal ElfSectionHeader() { }

    /// <summary>Gets the section name from the section-name string table.</summary>
    public string Name { get; internal set; } = string.Empty;

    /// <summary>Gets sh_name.</summary>
    public uint NameOffset { get; internal set; }

    /// <summary>Gets sh_type.</summary>
    public uint Type { get; internal set; }

    /// <summary>Gets sh_flags.</summary>
    public ulong Flags { get; internal set; }

    /// <summary>Gets sh_addr.</summary>
    public ulong Address { get; internal set; }

    /// <summary>Gets sh_offset relative to the file start.</summary>
    public ulong Offset { get; internal set; }

    /// <summary>Gets sh_size; SHT_NOBITS occupies no file bytes.</summary>
    public ulong Size { get; internal set; }

    /// <summary>Gets sh_link.</summary>
    public uint Link { get; internal set; }

    /// <summary>Gets sh_info.</summary>
    public uint Info { get; internal set; }

    /// <summary>Gets sh_addralign.</summary>
    public ulong AddressAlignment { get; internal set; }

    /// <summary>Gets sh_entsize.</summary>
    public ulong EntrySize { get; internal set; }
}
