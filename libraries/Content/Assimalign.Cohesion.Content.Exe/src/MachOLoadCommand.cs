namespace Assimalign.Cohesion.Content.Exe;

/// <summary>A Mach-O load-command header.</summary>
public sealed class MachOLoadCommand
{
    internal MachOLoadCommand() { }

    /// <summary>Gets cmd including required-command bits.</summary>
    public uint Command { get; internal set; }

    /// <summary>Gets cmdsize including its header.</summary>
    public uint Size { get; internal set; }

    /// <summary>Gets the command offset relative to the file start.</summary>
    public ulong Offset { get; internal set; }
}
