using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Content.Exe;

/// <summary>PE image headers and section metadata.</summary>
public sealed class PeMetadata
{
    internal PeMetadata() { }

    /// <summary>Gets the original 64-byte DOS header, including e_lfanew.</summary>
    public ReadOnlyMemory<byte> DosHeader { get; internal set; }

    /// <summary>Gets e_lfanew, relative to the image start.</summary>
    public uint PeHeaderOffset { get; internal set; }

    /// <summary>Gets the COFF machine identifier.</summary>
    public ushort Machine { get; internal set; }

    /// <summary>Gets the COFF characteristics flags.</summary>
    public ushort Characteristics { get; internal set; }

    /// <summary>Gets the raw COFF timestamp (not necessarily a meaningful date).</summary>
    public uint TimeDateStamp { get; internal set; }

    /// <summary>Gets 0x10B (PE32) or 0x20B (PE32+).</summary>
    public ushort OptionalHeaderMagic { get; internal set; }

    /// <summary>Gets the Windows subsystem identifier.</summary>
    public ushort Subsystem { get; internal set; }

    /// <summary>Gets the entry-point RVA.</summary>
    public uint AddressOfEntryPoint { get; internal set; }

    /// <summary>Gets the preferred image base.</summary>
    public ulong ImageBase { get; internal set; }

    /// <summary>Gets DLL characteristics flags.</summary>
    public ushort DllCharacteristics { get; internal set; }

    /// <summary>Gets the section alignment in memory.</summary>
    public uint SectionAlignment { get; internal set; }

    /// <summary>Gets raw-data alignment in the file.</summary>
    public uint FileAlignment { get; internal set; }

    /// <summary>Gets the total code size.</summary>
    public uint SizeOfCode { get; internal set; }

    /// <summary>Gets the total initialized-data size.</summary>
    public uint SizeOfInitializedData { get; internal set; }

    /// <summary>Gets the total uninitialized-data size.</summary>
    public uint SizeOfUninitializedData { get; internal set; }

    /// <summary>Gets the image size in memory.</summary>
    public uint SizeOfImage { get; internal set; }

    /// <summary>Gets the header size in the file.</summary>
    public uint SizeOfHeaders { get; internal set; }

    /// <summary>Gets the reserved stack size.</summary>
    public ulong SizeOfStackReserve { get; internal set; }

    /// <summary>Gets the committed stack size.</summary>
    public ulong SizeOfStackCommit { get; internal set; }

    /// <summary>Gets the reserved heap size.</summary>
    public ulong SizeOfHeapReserve { get; internal set; }

    /// <summary>Gets the committed heap size.</summary>
    public ulong SizeOfHeapCommit { get; internal set; }

    /// <summary>Gets whether CLI data directory 14 has a nonzero address and size; this does not validate CLI metadata.</summary>
    public bool IsManaged { get; internal set; }

    /// <summary>Gets directories in their original index order.</summary>
    public IReadOnlyList<PeDataDirectory> DataDirectories { get; internal set; } = [];

    /// <summary>Gets section headers in file order.</summary>
    public IReadOnlyList<PeSectionHeader> Sections { get; internal set; } = [];
}
