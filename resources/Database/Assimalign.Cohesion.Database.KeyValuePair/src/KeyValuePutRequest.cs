using System;

namespace Assimalign.Cohesion.Database.KeyValuePair;

/// <summary>
/// An insert-or-replace write, optionally conditional. The result is a one-row
/// result set with the columns <c>applied</c> (boolean) and <c>etag</c> (int64):
/// when the write applied, <c>etag</c> is the entry's new etag; when a condition
/// held it back, <c>etag</c> is the key's current etag (null when the key has no
/// visible entry). A conditional miss is a first-class outcome, never an
/// exception — see <c>docs/DESIGN.md</c> ("Etags and compare-and-swap").
/// </summary>
public sealed class KeyValuePutRequest : KeyValueRequest
{
    /// <summary>
    /// Initializes a new <see cref="KeyValuePutRequest"/>.
    /// </summary>
    /// <param name="key">The key to write.</param>
    /// <param name="value">The value to store.</param>
    /// <param name="options">Write conditions, or null for an unconditional upsert.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="key"/> is empty, or when <paramref name="options"/> combines <see cref="KeyValuePutOptions.OnlyIfAbsent"/> with <see cref="KeyValuePutOptions.ExpectedETag"/>.</exception>
    public KeyValuePutRequest(ReadOnlyMemory<byte> key, ReadOnlyMemory<byte> value, KeyValuePutOptions? options = null)
        : base(new KeyValueStatement(KeyValueOperation.Put))
    {
        if (key.IsEmpty)
        {
            throw new ArgumentException("The key cannot be empty.", nameof(key));
        }

        if (options is { OnlyIfAbsent: true, ExpectedETag: not null })
        {
            throw new ArgumentException(
                "OnlyIfAbsent and ExpectedETag are contradictory conditions: one expects no entry, the other expects a specific one.",
                nameof(options));
        }

        Key = key;
        Value = value;
        OnlyIfAbsent = options?.OnlyIfAbsent ?? false;
        ExpectedETag = options?.ExpectedETag;
    }

    /// <summary>
    /// Gets the key to write.
    /// </summary>
    public ReadOnlyMemory<byte> Key { get; }

    /// <summary>
    /// Gets the value to store.
    /// </summary>
    public ReadOnlyMemory<byte> Value { get; }

    /// <summary>
    /// Gets a value indicating whether the write applies only when the key has no
    /// visible entry.
    /// </summary>
    public bool OnlyIfAbsent { get; }

    /// <summary>
    /// Gets the etag the key's current entry must carry for the write to apply
    /// (compare-and-swap), or null for an unconditional write.
    /// </summary>
    public long? ExpectedETag { get; }
}
