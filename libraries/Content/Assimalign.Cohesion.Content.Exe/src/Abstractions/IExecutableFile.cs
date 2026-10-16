namespace Assimalign.Cohesion.Content.Exe;

/// <summary>A detached executable inspection snapshot; reading metadata never launches a process.</summary>
public interface IExecutableFile
{
    /// <summary>Gets the detected executable format.</summary>
    ContentFormat Format { get; }
    /// <summary>Gets the format's raw machine or CPU identifier; zero for a universal file.</summary>
    uint Machine { get; }
    /// <summary>Gets 32 or 64, or zero when a universal file has no single bitness.</summary>
    int Bitness { get; }
    /// <summary>Gets the byte order of the outer header.</summary>
    ExecutableByteOrder ByteOrder { get; }
    /// <summary>Gets the file's broad role; ELF shared objects include position-independent executables.</summary>
    ExecutableKind Kind { get; }
    /// <summary>Gets the PE entry-point RVA or ELF entry-point address; null when not decoded.</summary>
    ulong? EntryPoint { get; }
    /// <summary>Gets PE metadata, or null for other formats.</summary>
    PeMetadata? Pe { get; }
    /// <summary>Gets ELF metadata, or null for other formats.</summary>
    ElfMetadata? Elf { get; }
    /// <summary>Gets Mach-O metadata, or null for other formats.</summary>
    MachOMetadata? MachO { get; }
}
