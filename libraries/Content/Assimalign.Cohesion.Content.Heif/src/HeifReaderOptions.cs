using System;

namespace Assimalign.Cohesion.Content.Heif;

/// <summary>Immutable limits and structural validation policy for HEIF still images.</summary>
public sealed class HeifReaderOptions
{
    /// <summary>Gets whether structural diagnostics fail immediately; default false.</summary>
    public bool Strict { get; init; }
    /// <summary>Gets the maximum buffered metadata bytes per box or derived item; default 16 MiB.</summary>
    public int MaxBufferedPayloadSize { get; init; } = 16 * 1024 * 1024;
    /// <summary>Gets the maximum item count; default 100,000.</summary>
    public int MaxItemCount { get; init; } = 100_000;
    /// <summary>Gets the maximum property count; default 32,767.</summary>
    public int MaxPropertyCount { get; init; } = 32_767;
    /// <summary>Gets the maximum combined extent or reference count; default one million.</summary>
    public int MaxExtentCount { get; init; } = 1_000_000;
    /// <summary>Gets the maximum grid or overlay inputs; default 65,536.</summary>
    public int MaxGridTileCount { get; init; } = 65_536;
    /// <summary>Gets the maximum derived output dimension; default 1,048,576.</summary>
    public uint MaxImageDimension { get; init; } = 1_048_576;
    /// <summary>Gets the maximum nested box depth; default 64.</summary>
    public int MaxDepth { get; init; } = 64;

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegative(MaxBufferedPayloadSize);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxBufferedPayloadSize, Array.MaxLength);
        ArgumentOutOfRangeException.ThrowIfNegative(MaxItemCount);
        ArgumentOutOfRangeException.ThrowIfNegative(MaxPropertyCount);
        ArgumentOutOfRangeException.ThrowIfNegative(MaxExtentCount);
        ArgumentOutOfRangeException.ThrowIfNegative(MaxGridTileCount);
        ArgumentOutOfRangeException.ThrowIfNegative(MaxDepth);
    }
}
