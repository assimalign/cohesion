using System;

namespace Assimalign.Cohesion.IdentityHub;

/// <summary>
/// A hub value as an <see cref="IdentityHubRecordSet{TRecord}"/> reads it: the value, the key
/// of the record that stores it, and the version of the stored state it was read from.
/// </summary>
/// <typeparam name="TRecord">The hub value.</typeparam>
public sealed class IdentityHubEntry<TRecord>
    where TRecord : notnull
{
    /// <summary>
    /// Initializes an entry.
    /// </summary>
    /// <param name="key">The key of the record that stores the value.</param>
    /// <param name="value">The value.</param>
    /// <param name="version">The version of the stored state.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="key"/> or <paramref name="version"/> is <see langword="default"/>.
    /// </exception>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    public IdentityHubEntry(IdentityHubRecordKey key, TRecord value, IdentityHubRecordVersion version)
    {
        if (key.Value is null)
        {
            throw new ArgumentException(ErrorMessages.RecordKeyRequired, nameof(key));
        }

        ArgumentNullException.ThrowIfNull(value);
        if (version.Value is null)
        {
            throw new ArgumentException(ErrorMessages.RecordVersionRequired, nameof(version));
        }

        Key = key;
        Value = value;
        Version = version;
    }

    /// <summary>
    /// Gets the key of the record that stores the value.
    /// </summary>
    public IdentityHubRecordKey Key { get; }

    /// <summary>
    /// Gets the value.
    /// </summary>
    public TRecord Value { get; }

    /// <summary>
    /// Gets the version of the stored state, which a conditional replace or remove names.
    /// </summary>
    public IdentityHubRecordVersion Version { get; }
}
