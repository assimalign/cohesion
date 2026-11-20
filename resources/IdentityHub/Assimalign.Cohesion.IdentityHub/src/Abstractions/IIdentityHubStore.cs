using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.IdentityHub;

/// <summary>
/// The seam between an identity hub and the data layer that keeps its state. The hub reads and
/// writes every durable fact through this contract and depends on no concrete store.
/// </summary>
/// <remarks>
/// <para>
/// <b>Shape.</b> A store holds named record sets. A record has a key that is unique within its
/// set, an opaque payload, an optional expiry, and index entries it is also found by. The hub
/// owns what a record means and how its payload is laid out; a store reads only the key, the
/// expiry, and the index entries, and returns exactly the payload it was given. A new kind of
/// record is therefore a new record set name, never a change to this contract or to a store.
/// </para>
/// <para>
/// <b>Every write is conditional.</b> <see cref="TryAddAsync"/> stores a record only when no
/// live record holds its key, and <see cref="TryReplaceAsync"/> and <see cref="TryRemoveAsync"/>
/// act only on the version their caller read. A refused write is an ordinary outcome, reported
/// as an <see cref="IdentityHubWriteResult"/> or <see langword="false"/>, and never throws. The
/// hub builds its rules on that: of two callers that redeem one device authorization, exactly
/// one removes the record, and only that caller issues a token.
/// </para>
/// <para>
/// <b>Each operation is atomic and immediately visible.</b> A write changes the record, its
/// expiry, and all of its index entries together or not at all, and the checks that refuse it
/// are part of the same step. Once a write returns, every later read through this store sees
/// it. A store on an eventually consistent medium reads with the consistency that keeps this
/// true. There is no operation over more than one record: the hub designs each rule to need one.
/// </para>
/// <para>
/// <b>Expiry.</b> From its <see cref="IdentityHubRecord.ExpiresAt"/> instant, by the store's
/// clock, a record is absent: it is not found, not listed, does not hold its key, and does not
/// hold its unique index entries. When the store reclaims the space is its own business. The
/// hub still applies its own protocol expiry to what it reads, because its clock and the
/// store's may differ.
/// </para>
/// <para>
/// <b>Uniqueness.</b> Among the live records of one set, at most one lists a given index name
/// and value as unique. A write that would break that is refused with
/// <see cref="IdentityHubWriteStatus.IndexConflict"/>. A record's own earlier entries never
/// conflict with its replacement.
/// </para>
/// <para>
/// <b>Arguments and failures.</b> Every operation refuses a <see langword="default"/> record set
/// name, key, or version with <see cref="ArgumentException"/>. A failure of the store's medium
/// is an <see cref="IdentityHubStoreException"/>. Implementations are safe for concurrent use.
/// <see cref="IdentityHubStore"/> is the base class that applies the argument rules for an
/// implementation.
/// </para>
/// </remarks>
public interface IIdentityHubStore
{
    /// <summary>
    /// Reads the live record that holds a key.
    /// </summary>
    /// <param name="set">The record set to read from.</param>
    /// <param name="key">The record's key.</param>
    /// <param name="cancellationToken">The token that cancels the read.</param>
    /// <returns>
    /// The record and the version of the state it was read from, or <see langword="null"/> when
    /// no live record holds the key: it was never added, was removed, or has expired.
    /// </returns>
    /// <exception cref="ArgumentException"><paramref name="set"/> or <paramref name="key"/> is <see langword="default"/>.</exception>
    /// <exception cref="IdentityHubStoreException">The store could not complete the read.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    ValueTask<IdentityHubStoredRecord?> FindAsync(
        IdentityHubRecordSetName set,
        IdentityHubRecordKey key,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads one page of the live records a query selects.
    /// </summary>
    /// <remarks>
    /// The order of records is the store's own and is the same from page to page of one
    /// enumeration. A record that is live from the first page to the last is returned exactly
    /// once; one added, replaced, or removed meanwhile is returned at most once.
    /// </remarks>
    /// <param name="set">The record set to read from.</param>
    /// <param name="query">The records to select, the page size, and the page to continue after.</param>
    /// <param name="cancellationToken">The token that cancels the read.</param>
    /// <returns>
    /// The page. Its <see cref="IdentityHubRecordPage.Continuation"/> is <see langword="null"/>
    /// when no page follows.
    /// </returns>
    /// <exception cref="ArgumentException"><paramref name="set"/> is <see langword="default"/>.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="query"/> is <see langword="null"/>.</exception>
    /// <exception cref="IdentityHubStoreException">
    /// The store could not complete the read, or it recognized the query's continuation as one
    /// it did not issue for this query.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    ValueTask<IdentityHubRecordPage> QueryAsync(
        IdentityHubRecordSetName set,
        IdentityHubRecordQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores a record when no live record holds its key.
    /// </summary>
    /// <param name="set">The record set to add to.</param>
    /// <param name="record">The record to store.</param>
    /// <param name="cancellationToken">The token that cancels the write.</param>
    /// <returns>
    /// <see cref="IdentityHubWriteStatus.Written"/> with the record's version;
    /// <see cref="IdentityHubWriteStatus.KeyConflict"/> when a live record already holds the key;
    /// or <see cref="IdentityHubWriteStatus.IndexConflict"/> when another live record lists one of
    /// the record's unique index entries. Nothing is stored unless the status is written.
    /// </returns>
    /// <exception cref="ArgumentException"><paramref name="set"/> is <see langword="default"/>.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="record"/> is <see langword="null"/>.</exception>
    /// <exception cref="IdentityHubStoreException">The store could not complete the write.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    ValueTask<IdentityHubWriteResult> TryAddAsync(
        IdentityHubRecordSetName set,
        IdentityHubRecord record,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the live record that holds a key, when its version is the expected one.
    /// </summary>
    /// <remarks>
    /// The replacement takes the place of the whole record: payload, expiry, and index entries.
    /// </remarks>
    /// <param name="set">The record set to replace in.</param>
    /// <param name="record">The replacement, whose key names the record to replace.</param>
    /// <param name="expectedVersion">The version of the state the caller read.</param>
    /// <param name="cancellationToken">The token that cancels the write.</param>
    /// <returns>
    /// <see cref="IdentityHubWriteStatus.Written"/> with the record's new version;
    /// <see cref="IdentityHubWriteStatus.VersionConflict"/> when no live record holds the key or
    /// its version is not <paramref name="expectedVersion"/>; or
    /// <see cref="IdentityHubWriteStatus.IndexConflict"/> when another live record lists one of
    /// the replacement's unique index entries. Nothing changes unless the status is written.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="set"/> or <paramref name="expectedVersion"/> is <see langword="default"/>.
    /// </exception>
    /// <exception cref="ArgumentNullException"><paramref name="record"/> is <see langword="null"/>.</exception>
    /// <exception cref="IdentityHubStoreException">The store could not complete the write.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    ValueTask<IdentityHubWriteResult> TryReplaceAsync(
        IdentityHubRecordSetName set,
        IdentityHubRecord record,
        IdentityHubRecordVersion expectedVersion,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes the live record that holds a key, when its version is the expected one.
    /// </summary>
    /// <param name="set">The record set to remove from.</param>
    /// <param name="key">The record's key.</param>
    /// <param name="expectedVersion">The version of the state the caller read.</param>
    /// <param name="cancellationToken">The token that cancels the write.</param>
    /// <returns>
    /// <see langword="true"/> when this call removed the record; <see langword="false"/> when no
    /// live record holds the key or its version is not <paramref name="expectedVersion"/>. Of the
    /// callers that name one version, at most one is answered <see langword="true"/>.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="set"/>, <paramref name="key"/>, or <paramref name="expectedVersion"/> is
    /// <see langword="default"/>.
    /// </exception>
    /// <exception cref="IdentityHubStoreException">The store could not complete the write.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    ValueTask<bool> TryRemoveAsync(
        IdentityHubRecordSetName set,
        IdentityHubRecordKey key,
        IdentityHubRecordVersion expectedVersion,
        CancellationToken cancellationToken = default);
}
