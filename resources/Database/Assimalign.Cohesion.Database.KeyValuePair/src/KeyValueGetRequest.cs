using System;

namespace Assimalign.Cohesion.Database.KeyValuePair;

/// <summary>
/// A point read: returns the visible, live entry for a key, as a one-row result
/// set with the columns <c>key</c> (binary), <c>value</c> (binary), and
/// <c>etag</c> (int64) — or zero rows when the key has no visible entry.
/// </summary>
public sealed class KeyValueGetRequest : KeyValueRequest
{
    /// <summary>
    /// Initializes a new <see cref="KeyValueGetRequest"/>.
    /// </summary>
    /// <param name="key">The key to read.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="key"/> is empty.</exception>
    public KeyValueGetRequest(ReadOnlyMemory<byte> key)
        : base(new KeyValueStatement(KeyValueOperation.Get))
    {
        if (key.IsEmpty)
        {
            throw new ArgumentException("The key cannot be empty.", nameof(key));
        }

        Key = key;
    }

    /// <summary>
    /// Gets the key to read.
    /// </summary>
    public ReadOnlyMemory<byte> Key { get; }
}
