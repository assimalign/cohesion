namespace Assimalign.Cohesion.Content.Exe;

/// <summary>A PE optional-header data-directory entry.</summary>
public sealed class PeDataDirectory
{
    internal PeDataDirectory() { }

    /// <summary>Gets the RVA, except directory 4 uses a file offset.</summary>
    public uint VirtualAddress { get; internal set; }

    /// <summary>Gets the declared byte size.</summary>
    public uint Size { get; internal set; }
}
