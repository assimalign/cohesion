namespace Assimalign.Cohesion.Content.Exe.Internal;

internal sealed class ExecutableFile : IExecutableFile
{
    public ContentFormat Format { get; init; } = ContentFormat.Unknown;
    public uint Machine { get; init; }
    public int Bitness { get; init; }
    public ExecutableByteOrder ByteOrder { get; init; }
    public ExecutableKind Kind { get; init; }
    public ulong? EntryPoint { get; init; }
    public PeMetadata? Pe { get; init; }
    public ElfMetadata? Elf { get; init; }
    public MachOMetadata? MachO { get; init; }
}
