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
    /// <param name="options">Scan bounds and limits, or null to scan the whole key space. Null and zero-length bounds mean absent.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="options"/> combines a non-empty prefix with non-empty explicit bounds, or carries a negative limit.</exception>
    public KeyValueScanRequest(KeyValueScanOptions? options = null)
        : base(KeyValueOperation.Scan)
    {
        if (options is not null)
        {
            // A null byte[] converts to present empty memory. Keep the nullable operand in
            // these conditionals so their null branch cannot itself convert to empty memory.
            Prefix = options.Prefix is { IsEmpty: true } ? null : options.Prefix;
            Start = options.Start is { IsEmpty: true } ? null : options.Start;
            End = options.End is { IsEmpty: true } ? null : options.End;
            if (Prefix is not null && (Start is not null || End is not null))
            {
                throw new ArgumentException(ErrorMessages.KeyValueScanRequestArgument, nameof(options));
            }

            if (options.Limit is < 0)
            {
                throw new ArgumentException(ErrorMessages.KeyValueScanRequestArgument2, nameof(options));
            }

            Limit = options.Limit;
        }
    }

    /// <summary>
    /// Gets the key prefix entries must match, or null for a bounded/unbounded scan.
    /// Null and zero-length inputs are normalized to null.
    /// </summary>
    public ReadOnlyMemory<byte>? Prefix { get; }

    /// <summary>
    /// Gets the inclusive lower key bound, or null to start at the first key.
    /// Null and zero-length inputs are normalized to null.
    /// </summary>
    public ReadOnlyMemory<byte>? Start { get; }

    /// <summary>
    /// Gets the exclusive upper key bound, or null to scan to the last key.
    /// Null and zero-length inputs are normalized to null.
    /// </summary>
    public ReadOnlyMemory<byte>? End { get; }

    /// <summary>
    /// Gets the maximum number of entries to return, or null for no limit.
    /// </summary>
    public int? Limit { get; }
}
