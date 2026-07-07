using System;

namespace Assimalign.Cohesion.Database.KeyValuePair;

/// <summary>
/// A stored key-value entry.
/// </summary>
/// <param name="Key">The entry's key.</param>
/// <param name="Value">The entry's value.</param>
/// <param name="ExpiresAt">When the entry expires, or null when it does not expire.</param>
public readonly record struct KeyValueEntry(
    ReadOnlyMemory<byte> Key,
    ReadOnlyMemory<byte> Value,
    DateTimeOffset? ExpiresAt = null);
