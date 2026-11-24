using System;

namespace Assimalign.Cohesion.IdentityHub;

/// <summary>
/// Maps one kind of hub value to and from the records of one record set. A codec is how the
/// package that owns a value says where it is stored, what its key is, when it expires, which
/// index entries find it, and how its payload is laid out.
/// </summary>
/// <remarks>
/// <para>
/// The package that owns the value owns its codec: a feature adds a record set by writing a
/// codec, and neither the area root nor any store changes. A codec is stateless and safe for
/// concurrent use.
/// </para>
/// <para>
/// A codec serializes without reflection, for example through a source-generated
/// <c>JsonSerializerContext</c>, and keeps secrets out of the record: a payload carries a
/// verifier digest, a reference, or protected bytes, and the key and index values are never
/// secret.
/// </para>
/// </remarks>
/// <typeparam name="TRecord">The hub value the codec reads and writes.</typeparam>
public interface IIdentityHubRecordCodec<TRecord>
    where TRecord : notnull
{
    /// <summary>
    /// Gets the record set the codec's values are stored in.
    /// </summary>
    IdentityHubRecordSetName Set { get; }

    /// <summary>
    /// Writes a value as a record: its key, payload, expiry, and index entries.
    /// </summary>
    /// <param name="value">The value to write.</param>
    /// <returns>The record that stores the value.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    IdentityHubRecord Encode(TRecord value);

    /// <summary>
    /// Reads a value back from a record that <see cref="Encode"/> wrote.
    /// </summary>
    /// <remarks>
    /// A codec reads every layout it ever wrote, because a store keeps records across versions
    /// of the hub. A record it cannot read is reported by throwing; the caller turns that into
    /// an <see cref="IdentityHubStoreException"/> with
    /// <see cref="IdentityHubErrorCode.StoreRecordCorrupt"/>.
    /// </remarks>
    /// <param name="record">The stored record.</param>
    /// <returns>The value the record stores.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="record"/> is <see langword="null"/>.</exception>
    TRecord Decode(IdentityHubRecord record);
}
