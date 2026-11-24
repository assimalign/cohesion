using System;

namespace Assimalign.Cohesion.IdentityHub.Internal;

/// <summary>
/// The instant a record of the in-memory store expires, with the record's key. It orders by
/// instant and then by key, so the earliest expiry of a record set is found first.
/// </summary>
/// <param name="ExpiresAt">The instant from which the record is absent.</param>
/// <param name="Key">The record's key.</param>
internal readonly record struct InMemoryExpiry(DateTimeOffset ExpiresAt, string Key) : IComparable<InMemoryExpiry>
{
    /// <inheritdoc />
    public int CompareTo(InMemoryExpiry other)
    {
        int byInstant = ExpiresAt.CompareTo(other.ExpiresAt);
        return byInstant is not 0 ? byInstant : string.CompareOrdinal(Key, other.Key);
    }
}
