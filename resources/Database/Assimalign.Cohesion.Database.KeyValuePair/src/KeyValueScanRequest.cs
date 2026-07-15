using System;

namespace Assimalign.Cohesion.Database.KeyValuePair;

/// <summary>
/// An ordered range scan over the key space: returns the visible, live entries
/// in ascending key order (unsigned lexicographic byte comparison — the tuple
/// codec's order-preserving contract is what makes range scans meaningful), as a
/// result set with the columns <c>key</c> (binary), <c>value</c> (binary), and
/// <c>etag</c> (int64).
/// </summary>
public sealed class KeyValueScanRequest : KeyValueRequest
{
    /// <summary>
    /// Initializes a new <see cref="KeyValueScanRequest"/>.
    /// </summary>
    /// <param name="options">Scan bounds and limits, or null to scan the whole key space.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="options"/> combines a prefix with explicit bounds, or carries a negative limit.</exception>
    public KeyValueScanRequest(KeyValueScanOptions? options = null)
        : base(new KeyValueStatement(KeyValueOperation.Scan))
    {
        if (options is not null)
        {
            if (options.Prefix is not null && (options.Start is not null || options.End is not null))
            {
                throw new ArgumentException("A prefix scan cannot combine with explicit start/end bounds.", nameof(options));
            }

            if (options.Limit is < 0)
            {
                throw new ArgumentException("The scan limit cannot be negative.", nameof(options));
            }

            Prefix = options.Prefix;
            Start = options.Start;
            End = options.End;
            Limit = options.Limit;
        }
    }

    /// <summary>
    /// Gets the key prefix entries must match, or null for a bounded/unbounded scan.
    /// </summary>
    public ReadOnlyMemory<byte>? Prefix { get; }

    /// <summary>
    /// Gets the inclusive lower key bound, or null to start at the first key.
    /// </summary>
    public ReadOnlyMemory<byte>? Start { get; }

    /// <summary>
    /// Gets the exclusive upper key bound, or null to scan to the last key.
    /// </summary>
    public ReadOnlyMemory<byte>? End { get; }

    /// <summary>
    /// Gets the maximum number of entries to return, or null for no limit.
    /// </summary>
    public int? Limit { get; }
}
