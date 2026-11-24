using System;
using System.Collections.Generic;
using System.Globalization;

namespace Assimalign.Cohesion.IdentityHub;

/// <summary>
/// One record as the hub hands it to an <see cref="IIdentityHubStore"/>: a key, an opaque
/// payload, an optional expiry, and the index entries the record is also found by.
/// </summary>
/// <remarks>
/// <para>
/// <b>The payload is opaque to the store.</b> The hub owns its layout and a store returns
/// exactly the bytes it was given, so a new kind of record never changes a store. A store
/// reads only the key, the expiry, and the index entries.
/// </para>
/// <para>
/// <b>A record holds no secret in the clear.</b> A payload carries a verifier digest, a
/// reference to a secret kept elsewhere, or protected bytes, never a client secret, a bearer
/// token, or private key material as they are. The key and the index values are never secret.
/// </para>
/// <para>
/// A record is immutable. The constructor copies the payload and the index entries, so a
/// caller that changes its own buffer afterwards does not change the record.
/// </para>
/// </remarks>
public sealed class IdentityHubRecord
{
    /// <summary>
    /// The largest payload a record carries, in bytes. A store accepts every payload up to
    /// this size.
    /// </summary>
    public const int MaxPayloadLength = 1024 * 1024;

    /// <summary>
    /// The most index entries a record lists.
    /// </summary>
    public const int MaxIndexCount = 16;

    private static readonly IReadOnlyList<IdentityHubRecordIndex> _noIndexes = Array.Empty<IdentityHubRecordIndex>();

    /// <summary>
    /// Initializes a record.
    /// </summary>
    /// <param name="key">The record's key within its record set.</param>
    /// <param name="payload">The record's content, which the store does not interpret. It is copied.</param>
    /// <param name="expiresAt">
    /// The instant from which the store no longer returns the record, or <see langword="null"/>
    /// for a record that does not expire.
    /// </param>
    /// <param name="indexes">The index entries the record is also found by, or <see langword="null"/> for none.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="key"/> is <see langword="default"/>; <paramref name="payload"/> is longer than
    /// <see cref="MaxPayloadLength"/>; or <paramref name="indexes"/> holds more than
    /// <see cref="MaxIndexCount"/> entries, a <see langword="default"/> entry, the same entry twice,
    /// or one index as both unique and not unique.
    /// </exception>
    public IdentityHubRecord(
        IdentityHubRecordKey key,
        ReadOnlyMemory<byte> payload,
        DateTimeOffset? expiresAt = null,
        IEnumerable<IdentityHubRecordIndex>? indexes = null)
    {
        if (key.Value is null)
        {
            throw new ArgumentException(ErrorMessages.RecordKeyRequired, nameof(key));
        }

        if (payload.Length > MaxPayloadLength)
        {
            throw new ArgumentException(
                string.Format(CultureInfo.CurrentCulture, ErrorMessages.RecordPayloadTooLarge, MaxPayloadLength),
                nameof(payload));
        }

        Key = key;
        Payload = payload.IsEmpty ? ReadOnlyMemory<byte>.Empty : payload.ToArray();
        ExpiresAt = expiresAt?.ToUniversalTime();
        Indexes = indexes is null ? _noIndexes : Snapshot(indexes);
    }

    /// <summary>
    /// Gets the record's key within its record set.
    /// </summary>
    public IdentityHubRecordKey Key { get; }

    /// <summary>
    /// Gets the record's content, which the store does not interpret.
    /// </summary>
    public ReadOnlyMemory<byte> Payload { get; }

    /// <summary>
    /// Gets the instant, in UTC, from which the store no longer returns the record, or
    /// <see langword="null"/> for a record that does not expire.
    /// </summary>
    public DateTimeOffset? ExpiresAt { get; }

    /// <summary>
    /// Gets the index entries the record is also found by.
    /// </summary>
    public IReadOnlyList<IdentityHubRecordIndex> Indexes { get; }

    /// <summary>
    /// Returns the record's key. The payload is never part of the text.
    /// </summary>
    /// <returns>The record's key.</returns>
    public override string ToString() => Key.ToString();

    private static IReadOnlyList<IdentityHubRecordIndex> Snapshot(IEnumerable<IdentityHubRecordIndex> indexes)
    {
        var entries = new List<IdentityHubRecordIndex>();
        foreach (IdentityHubRecordIndex index in indexes)
        {
            if (index.Name.Value is null)
            {
                throw new ArgumentException(ErrorMessages.RecordIndexNameRequired, nameof(indexes));
            }

            if (entries.Count == MaxIndexCount)
            {
                throw new ArgumentException(
                    string.Format(CultureInfo.CurrentCulture, ErrorMessages.RecordIndexCountExceeded, MaxIndexCount),
                    nameof(indexes));
            }

            for (int position = 0; position < entries.Count; position++)
            {
                IdentityHubRecordIndex existing = entries[position];
                if (existing.Name != index.Name)
                {
                    continue;
                }

                if (existing.IsUnique != index.IsUnique)
                {
                    throw new ArgumentException(
                        string.Format(CultureInfo.CurrentCulture, ErrorMessages.RecordIndexUniquenessMixed, index.Name),
                        nameof(indexes));
                }

                if (existing.Value == index.Value)
                {
                    throw new ArgumentException(
                        string.Format(CultureInfo.CurrentCulture, ErrorMessages.RecordIndexDuplicate, index.Name),
                        nameof(indexes));
                }
            }

            entries.Add(index);
        }

        return entries.Count is 0 ? _noIndexes : Array.AsReadOnly(entries.ToArray());
    }
}
