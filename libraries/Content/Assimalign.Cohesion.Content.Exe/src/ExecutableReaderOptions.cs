using System;

namespace Assimalign.Cohesion.Content.Exe;

/// <summary>Limits for executable metadata inspection; source payloads are never buffered.</summary>
public sealed class ExecutableReaderOptions
{
    /// <summary>Creates options with bounded defaults.</summary>
    public ExecutableReaderOptions() { }
    /// <summary>Gets the maximum entries in any table; defaults to 65,536.</summary>
    public int MaxTableEntries { get; init; } = 65_536;
    /// <summary>Gets the cumulative metadata byte budget, including tables and decoded names; defaults to 16 MiB.</summary>
    public int MaxBufferedBytes { get; init; } = 16 * 1024 * 1024;

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxTableEntries);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxBufferedBytes);
    }
}
