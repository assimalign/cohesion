using System;

namespace Assimalign.Cohesion.Database.KeyValuePair;

/// <summary>
/// Bounds and limits for a key-order scan. A prefix scan and an explicit
/// start/end range are mutually exclusive shapes.
/// </summary>
public sealed class KeyValueScanOptions
{
    /// <summary>
    /// Gets or sets the key prefix entries must match. Cannot combine with
    /// <see cref="Start"/> or <see cref="End"/>.
    /// </summary>
    public ReadOnlyMemory<byte>? Prefix { get; set; }

    /// <summary>
    /// Gets or sets the inclusive lower key bound, or null to start at the first key.
    /// </summary>
    public ReadOnlyMemory<byte>? Start { get; set; }

    /// <summary>
    /// Gets or sets the exclusive upper key bound, or null to scan to the last key.
    /// </summary>
    public ReadOnlyMemory<byte>? End { get; set; }

    /// <summary>
    /// Gets or sets the maximum number of entries to return, or null for no limit.
    /// </summary>
    public int? Limit { get; set; }
}
