using System;

namespace Assimalign.Cohesion.Content.Ebml;

/// <summary>Bounds parsing and document materialization. Values are snapshotted at creation.</summary>
public sealed class EbmlReaderOptions
{
    /// <summary>Gets or sets the maximum buffered leaf length, initially 16 MiB.</summary>
    public int MaxBufferedValueLength { get; set; } = 16 * 1024 * 1024;
    /// <summary>Gets or sets the maximum entered master depth, initially 64 (at most 256).</summary>
    public int MaxDepth { get; set; } = 64;
    /// <summary>Gets or sets the maximum ID width, initially four octets; at most eight.</summary>
    public int MaxIdLength { get; set; } = 4;
    /// <summary>Gets or sets the maximum size width, initially eight octets.</summary>
    public int MaxSizeLength { get; set; } = 8;
    /// <summary>Gets or sets the maximum number of tree nodes, initially one million.</summary>
    public int MaxElementCount { get; set; } = 1_000_000;

    internal EbmlReaderOptions Snapshot()
    {
        ArgumentOutOfRangeException.ThrowIfNegative(MaxBufferedValueLength);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxBufferedValueLength, Array.MaxLength);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxDepth, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxDepth, 256);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxIdLength, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxIdLength, 8);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxSizeLength, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxSizeLength, 8);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxElementCount, 1);
        return (EbmlReaderOptions)MemberwiseClone();
    }
}
