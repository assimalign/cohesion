using System;

namespace Assimalign.Cohesion.Content.Bmff;

/// <summary>Immutable limits and unknown-box policy for a BMFF reader.</summary>
public sealed class BmffReaderOptions
{
    /// <summary>Gets the maximum materialized leaf payload size; defaults to 16 MiB.</summary>
    public int MaxBufferedPayloadSize { get; init; } = 16 * 1024 * 1024;
    /// <summary>Gets the maximum nesting depth, with top-level boxes at depth zero; defaults to 64.</summary>
    public int MaxDepth { get; init; } = 64;
    /// <summary>Gets whether unregistered or unsupported boxes fail instead of being preserved.</summary>
    public bool Strict { get; init; }

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegative(MaxBufferedPayloadSize);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxBufferedPayloadSize, Array.MaxLength);
        ArgumentOutOfRangeException.ThrowIfNegative(MaxDepth);
    }
}
