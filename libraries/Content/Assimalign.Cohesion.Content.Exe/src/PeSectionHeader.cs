namespace Assimalign.Cohesion.Content.Exe;

/// <summary>A PE section header; section contents remain unread.</summary>
public sealed class PeSectionHeader
{
    internal PeSectionHeader() { }

    /// <summary>Gets the eight-byte inline section name decoded as ASCII.</summary>
    public string Name { get; internal set; } = string.Empty;

    /// <summary>Gets the section size in memory.</summary>
    public uint VirtualSize { get; internal set; }

    /// <summary>Gets the section RVA.</summary>
    public uint VirtualAddress { get; internal set; }

    /// <summary>Gets the initialized byte size in the file.</summary>
    public uint SizeOfRawData { get; internal set; }

    /// <summary>Gets the file-relative offset of raw data.</summary>
    public uint PointerToRawData { get; internal set; }

    /// <summary>Gets the file-relative relocation offset.</summary>
    public uint PointerToRelocations { get; internal set; }

    /// <summary>Gets the file-relative line-number offset.</summary>
    public uint PointerToLineNumbers { get; internal set; }

    /// <summary>Gets the relocation count.</summary>
    public ushort NumberOfRelocations { get; internal set; }

    /// <summary>Gets the line-number count.</summary>
    public ushort NumberOfLineNumbers { get; internal set; }

    /// <summary>Gets section flags.</summary>
    public uint Characteristics { get; internal set; }
}
