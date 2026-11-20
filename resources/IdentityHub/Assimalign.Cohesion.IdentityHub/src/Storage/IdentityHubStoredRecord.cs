using System;

namespace Assimalign.Cohesion.IdentityHub;

/// <summary>
/// A record as an <see cref="IIdentityHubStore"/> returns it: the record, and the version of
/// the stored state it was read from.
/// </summary>
public sealed class IdentityHubStoredRecord
{
    /// <summary>
    /// Initializes a stored record.
    /// </summary>
    /// <param name="record">The record as it is stored.</param>
    /// <param name="version">The version of the stored state.</param>
    /// <exception cref="ArgumentNullException"><paramref name="record"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="version"/> is <see langword="default"/>.</exception>
    public IdentityHubStoredRecord(IdentityHubRecord record, IdentityHubRecordVersion version)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (version.Value is null)
        {
            throw new ArgumentException(ErrorMessages.RecordVersionRequired, nameof(version));
        }

        Record = record;
        Version = version;
    }

    /// <summary>
    /// Gets the record as it is stored.
    /// </summary>
    public IdentityHubRecord Record { get; }

    /// <summary>
    /// Gets the version of the stored state, which a conditional replace or remove names.
    /// </summary>
    public IdentityHubRecordVersion Version { get; }

    /// <summary>
    /// Returns the record's key and version. The payload is never part of the text.
    /// </summary>
    /// <returns>The record's key and version.</returns>
    public override string ToString() => $"{Record.Key} @ {Version}";
}
