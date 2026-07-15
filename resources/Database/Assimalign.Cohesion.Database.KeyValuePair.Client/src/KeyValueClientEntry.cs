using System;

namespace Assimalign.Cohesion.Database.KeyValuePair.Client;

/// <summary>
/// A key-value entry as read by the client: the key, its value, and the entry's
/// etag — the concurrency token conditional writes compare against
/// (<see cref="KeyValueWriteCondition.IfETagMatches"/>,
/// <see cref="IKeyValueConnection.TryDeleteAsync(ReadOnlyMemory{byte}, long, System.Threading.CancellationToken)"/>).
/// </summary>
/// <param name="Key">The entry's key.</param>
/// <param name="Value">The entry's value.</param>
/// <param name="ETag">The entry's etag; every applied write produces a new one.</param>
public readonly record struct KeyValueClientEntry(
    ReadOnlyMemory<byte> Key,
    ReadOnlyMemory<byte> Value,
    long ETag);
