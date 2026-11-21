using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.IdentityHub;

/// <summary>
/// The base class of an <see cref="IIdentityHubStore"/> implementation. It applies the contract's
/// argument rules once, so that an implementation supplies only the behavior of its medium.
/// </summary>
/// <remarks>
/// Each public operation refuses a <see langword="default"/> record set name, key, or version and
/// a <see langword="null"/> record or query, observes a canceled token, and then calls the
/// matching <c>Core</c> member. A <c>Core</c> member therefore always receives valid arguments. It
/// still owns everything else the contract promises: atomic conditional writes, immediate
/// visibility, expiry, uniqueness, and translating the medium's failures into
/// <see cref="IdentityHubStoreException"/>.
/// </remarks>
public abstract class IdentityHubStore : IIdentityHubStore
{
    /// <summary>
    /// Initializes the base class.
    /// </summary>
    protected IdentityHubStore()
    {
    }

    /// <inheritdoc />
    public ValueTask<IdentityHubStoredRecord?> FindAsync(
        IdentityHubRecordSetName set,
        IdentityHubRecordKey key,
        CancellationToken cancellationToken = default)
    {
        RequireSet(set);
        RequireKey(key);
        cancellationToken.ThrowIfCancellationRequested();
        return FindCoreAsync(set, key, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<IdentityHubRecordPage> QueryAsync(
        IdentityHubRecordSetName set,
        IdentityHubRecordQuery query,
        CancellationToken cancellationToken = default)
    {
        RequireSet(set);
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();
        return QueryCoreAsync(set, query, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<IdentityHubWriteResult> TryAddAsync(
        IdentityHubRecordSetName set,
        IdentityHubRecord record,
        CancellationToken cancellationToken = default)
    {
        RequireSet(set);
        ArgumentNullException.ThrowIfNull(record);
        cancellationToken.ThrowIfCancellationRequested();
        return TryAddCoreAsync(set, record, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<IdentityHubWriteResult> TryReplaceAsync(
        IdentityHubRecordSetName set,
        IdentityHubRecord record,
        IdentityHubRecordVersion expectedVersion,
        CancellationToken cancellationToken = default)
    {
        RequireSet(set);
        ArgumentNullException.ThrowIfNull(record);
        RequireVersion(expectedVersion);
        cancellationToken.ThrowIfCancellationRequested();
        return TryReplaceCoreAsync(set, record, expectedVersion, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<bool> TryRemoveAsync(
        IdentityHubRecordSetName set,
        IdentityHubRecordKey key,
        IdentityHubRecordVersion expectedVersion,
        CancellationToken cancellationToken = default)
    {
        RequireSet(set);
        RequireKey(key);
        RequireVersion(expectedVersion);
        cancellationToken.ThrowIfCancellationRequested();
        return TryRemoveCoreAsync(set, key, expectedVersion, cancellationToken);
    }

    /// <summary>
    /// Reads the live record that holds a key. See <see cref="IIdentityHubStore.FindAsync"/>.
    /// </summary>
    /// <param name="set">The record set to read from. Never <see langword="default"/>.</param>
    /// <param name="key">The record's key. Never <see langword="default"/>.</param>
    /// <param name="cancellationToken">The token that cancels the read.</param>
    /// <returns>The record and its version, or <see langword="null"/> when no live record holds the key.</returns>
    protected abstract ValueTask<IdentityHubStoredRecord?> FindCoreAsync(
        IdentityHubRecordSetName set,
        IdentityHubRecordKey key,
        CancellationToken cancellationToken);

    /// <summary>
    /// Reads one page of the live records a query selects. See <see cref="IIdentityHubStore.QueryAsync"/>.
    /// </summary>
    /// <param name="set">The record set to read from. Never <see langword="default"/>.</param>
    /// <param name="query">The query. Never <see langword="null"/>.</param>
    /// <param name="cancellationToken">The token that cancels the read.</param>
    /// <returns>The page.</returns>
    protected abstract ValueTask<IdentityHubRecordPage> QueryCoreAsync(
        IdentityHubRecordSetName set,
        IdentityHubRecordQuery query,
        CancellationToken cancellationToken);

    /// <summary>
    /// Stores a record when no live record holds its key. See <see cref="IIdentityHubStore.TryAddAsync"/>.
    /// </summary>
    /// <param name="set">The record set to add to. Never <see langword="default"/>.</param>
    /// <param name="record">The record to store. Never <see langword="null"/>.</param>
    /// <param name="cancellationToken">The token that cancels the write.</param>
    /// <returns>The outcome of the write.</returns>
    protected abstract ValueTask<IdentityHubWriteResult> TryAddCoreAsync(
        IdentityHubRecordSetName set,
        IdentityHubRecord record,
        CancellationToken cancellationToken);

    /// <summary>
    /// Replaces the live record that holds a key, when its version is the expected one. See
    /// <see cref="IIdentityHubStore.TryReplaceAsync"/>.
    /// </summary>
    /// <param name="set">The record set to replace in. Never <see langword="default"/>.</param>
    /// <param name="record">The replacement. Never <see langword="null"/>.</param>
    /// <param name="expectedVersion">The version of the state the caller read. Never <see langword="default"/>.</param>
    /// <param name="cancellationToken">The token that cancels the write.</param>
    /// <returns>The outcome of the write.</returns>
    protected abstract ValueTask<IdentityHubWriteResult> TryReplaceCoreAsync(
        IdentityHubRecordSetName set,
        IdentityHubRecord record,
        IdentityHubRecordVersion expectedVersion,
        CancellationToken cancellationToken);

    /// <summary>
    /// Removes the live record that holds a key, when its version is the expected one. See
    /// <see cref="IIdentityHubStore.TryRemoveAsync"/>.
    /// </summary>
    /// <param name="set">The record set to remove from. Never <see langword="default"/>.</param>
    /// <param name="key">The record's key. Never <see langword="default"/>.</param>
    /// <param name="expectedVersion">The version of the state the caller read. Never <see langword="default"/>.</param>
    /// <param name="cancellationToken">The token that cancels the write.</param>
    /// <returns><see langword="true"/> when this call removed the record.</returns>
    protected abstract ValueTask<bool> TryRemoveCoreAsync(
        IdentityHubRecordSetName set,
        IdentityHubRecordKey key,
        IdentityHubRecordVersion expectedVersion,
        CancellationToken cancellationToken);

    private static void RequireSet(IdentityHubRecordSetName set)
    {
        if (set.Value is null)
        {
            throw new ArgumentException(ErrorMessages.RecordSetNameRequired, nameof(set));
        }
    }

    private static void RequireKey(IdentityHubRecordKey key)
    {
        if (key.Value is null)
        {
            throw new ArgumentException(ErrorMessages.RecordKeyRequired, nameof(key));
        }
    }

    private static void RequireVersion(IdentityHubRecordVersion expectedVersion)
    {
        if (expectedVersion.Value is null)
        {
            throw new ArgumentException(ErrorMessages.RecordVersionRequired, nameof(expectedVersion));
        }
    }
}
