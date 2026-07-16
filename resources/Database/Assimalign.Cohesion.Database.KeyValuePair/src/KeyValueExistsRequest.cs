using System;

namespace Assimalign.Cohesion.Database.KeyValuePair;

/// <summary>
/// A visibility probe: returns whether a key has a visible, live entry, as a
/// one-row result set with the column <c>exists</c> (boolean).
/// </summary>
public sealed class KeyValueExistsRequest : KeyValueRequest
{
    /// <summary>
    /// Initializes a new <see cref="KeyValueExistsRequest"/>.
    /// </summary>
    /// <param name="key">The key to probe.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="key"/> is empty.</exception>
    public KeyValueExistsRequest(ReadOnlyMemory<byte> key)
        : base(new KeyValueStatement(KeyValueOperation.Exists))
    {
        if (key.IsEmpty)
        {
            throw new ArgumentException("The key cannot be empty.", nameof(key));
        }

        Key = key;
    }

    /// <summary>
    /// Gets the key to probe.
    /// </summary>
    public ReadOnlyMemory<byte> Key { get; }
}
