using System;

namespace Assimalign.Cohesion.Database.KeyValuePair;

/// <summary>
/// A stored key-value entry: the key, its value, and the entry's etag.
/// </summary>
/// <param name="Key">The entry's key.</param>
/// <param name="Value">The entry's value.</param>
/// <param name="ETag">
/// The entry's etag — the sequence of the transaction that wrote the visible
/// version. Every successful write produces a new etag, so it is the natural
/// concurrency token for conditional writes
/// (<see cref="KeyValuePutOptions.ExpectedETag"/>,
/// <see cref="KeyValueDeleteRequest.ExpectedETag"/>).
/// </param>
public readonly record struct KeyValueEntry(
    ReadOnlyMemory<byte> Key,
    ReadOnlyMemory<byte> Value,
    long ETag);
