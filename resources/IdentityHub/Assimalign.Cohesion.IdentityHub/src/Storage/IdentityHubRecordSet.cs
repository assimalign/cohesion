using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.IdentityHub;

/// <summary>
/// One record set of an <see cref="IIdentityHubStore"/>, read and written as hub values instead
/// of records. It is what hub code holds: the store and the codec stay behind it.
/// </summary>
/// <remarks>
/// <para>
/// The set adds no behavior to the store's: every write is the store's conditional write, and a
/// refused write is still a result, not an exception. It adds two things. It runs the codec in
/// both directions, and it reports a stored record the codec cannot read as an
/// <see cref="IdentityHubStoreException"/> with <see cref="IdentityHubErrorCode.StoreRecordCorrupt"/>,
/// naming the set and key and never the payload.
/// </para>
/// <para>
/// A set holds no state of its own and is safe for concurrent use when its store and codec are.
/// It does not own the store and never disposes it.
/// </para>
/// </remarks>
/// <typeparam name="TRecord">The hub value the set stores.</typeparam>
[DebuggerDisplay("{Name,nq}")]
[DebuggerTypeProxy(typeof(IdentityHubRecordSet<>.DebugView))]
public sealed class IdentityHubRecordSet<TRecord>
    where TRecord : notnull
{
    private readonly IIdentityHubRecordCodec<TRecord> _codec;
    private readonly IIdentityHubStore _store;

    private IdentityHubRecordSet(IIdentityHubStore store, IIdentityHubRecordCodec<TRecord> codec, IdentityHubRecordSetName name)
    {
        _store = store;
        _codec = codec;
        Name = name;
    }

    /// <summary>
    /// Gets the name of the record set.
    /// </summary>
    public IdentityHubRecordSetName Name { get; }

    /// <summary>
    /// Creates the typed view of a codec's record set in a store.
    /// </summary>
    /// <param name="store">The store that holds the record set.</param>
    /// <param name="codec">The codec that maps the set's records to hub values.</param>
    /// <returns>The record set.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="store"/> or <paramref name="codec"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">The codec's record set name is <see langword="default"/>.</exception>
    public static IdentityHubRecordSet<TRecord> Create(IIdentityHubStore store, IIdentityHubRecordCodec<TRecord> codec)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(codec);

        IdentityHubRecordSetName name = codec.Set;
        if (name.Value is null)
        {
            throw new ArgumentException(ErrorMessages.CodecSetRequired, nameof(codec));
        }

        return new IdentityHubRecordSet<TRecord>(store, codec, name);
    }

    /// <summary>
    /// Reads the value stored under a key.
    /// </summary>
    /// <param name="key">The key of the record that stores the value.</param>
    /// <param name="cancellationToken">The token that cancels the read.</param>
    /// <returns>
    /// The value and the version of the state it was read from, or <see langword="null"/> when no
    /// live record holds the key.
    /// </returns>
    /// <exception cref="ArgumentException"><paramref name="key"/> is <see langword="default"/>.</exception>
    /// <exception cref="IdentityHubStoreException">
    /// The store could not complete the read, or the stored record could not be decoded.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    public async ValueTask<IdentityHubEntry<TRecord>?> FindAsync(
        IdentityHubRecordKey key,
        CancellationToken cancellationToken = default)
    {
        IdentityHubStoredRecord? stored = await _store.FindAsync(Name, key, cancellationToken).ConfigureAwait(false);
        return stored is null ? null : Decode(stored);
    }

    /// <summary>
    /// Reads the one value that lists a value under an index.
    /// </summary>
    /// <remarks>
    /// This is the lookup for a unique index, such as a user name or a user code. It reads until
    /// it has found the record or reached the last page, so it also answers correctly when the
    /// store returns short pages.
    /// </remarks>
    /// <param name="index">The index to match on.</param>
    /// <param name="value">The value the record lists under <paramref name="index"/>.</param>
    /// <param name="cancellationToken">The token that cancels the read.</param>
    /// <returns>
    /// The value and the version of the state it was read from, or <see langword="null"/> when no
    /// live record lists the index value.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="index"/> or <paramref name="value"/> is <see langword="default"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">More than one live record lists the index value.</exception>
    /// <exception cref="IdentityHubStoreException">
    /// The store could not complete the read, or the stored record could not be decoded.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    public async ValueTask<IdentityHubEntry<TRecord>?> FindByIndexAsync(
        IdentityHubRecordIndexName index,
        IdentityHubRecordKey value,
        CancellationToken cancellationToken = default)
    {
        IdentityHubRecordQuery query = IdentityHubRecordQuery.ByIndex(index, value, limit: 2);
        IdentityHubStoredRecord? found = null;
        while (true)
        {
            IdentityHubRecordPage page = await ReadPageAsync(query, cancellationToken).ConfigureAwait(false);
            for (int position = 0; position < page.Records.Count; position++)
            {
                if (found is not null)
                {
                    throw new InvalidOperationException(
                        string.Format(CultureInfo.CurrentCulture, ErrorMessages.IndexNotUnique, Name, index));
                }

                found = page.Records[position];
            }

            if (page.Continuation is null)
            {
                return found is null ? null : Decode(found);
            }

            query = query.After(page.Continuation);
        }
    }

    /// <summary>
    /// Reads one page of the values a query selects.
    /// </summary>
    /// <param name="query">The values to select, the page size, and the page to continue after.</param>
    /// <param name="cancellationToken">The token that cancels the read.</param>
    /// <returns>
    /// The page. Its <see cref="IdentityHubEntryPage{TRecord}.Continuation"/> is
    /// <see langword="null"/> when no page follows.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="query"/> is <see langword="null"/>.</exception>
    /// <exception cref="IdentityHubStoreException">
    /// The store could not complete the read, refused the query's continuation, or returned a
    /// record that could not be decoded.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    public async ValueTask<IdentityHubEntryPage<TRecord>> QueryAsync(
        IdentityHubRecordQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        IdentityHubRecordPage page = await ReadPageAsync(query, cancellationToken).ConfigureAwait(false);
        var entries = new List<IdentityHubEntry<TRecord>>(page.Records.Count);
        for (int position = 0; position < page.Records.Count; position++)
        {
            entries.Add(Decode(page.Records[position]));
        }

        return new IdentityHubEntryPage<TRecord>(entries, page.Continuation);
    }

    /// <summary>
    /// Stores a value when no live record holds its key.
    /// </summary>
    /// <param name="value">The value to store.</param>
    /// <param name="cancellationToken">The token that cancels the write.</param>
    /// <returns>
    /// The outcome of the write: written with the record's version, a key conflict, or an index
    /// conflict. See <see cref="IIdentityHubStore.TryAddAsync"/>.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The codec encoded the value to no record.</exception>
    /// <exception cref="IdentityHubStoreException">The store could not complete the write.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    public ValueTask<IdentityHubWriteResult> TryAddAsync(TRecord value, CancellationToken cancellationToken = default)
    {
        return _store.TryAddAsync(Name, Encode(value), cancellationToken);
    }

    /// <summary>
    /// Replaces the value stored under the value's key, when its version is the expected one.
    /// </summary>
    /// <param name="value">The replacement, whose key names the record to replace.</param>
    /// <param name="expectedVersion">The version of the state the caller read.</param>
    /// <param name="cancellationToken">The token that cancels the write.</param>
    /// <returns>
    /// The outcome of the write: written with the record's new version, a version conflict, or an
    /// index conflict. See <see cref="IIdentityHubStore.TryReplaceAsync"/>.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="expectedVersion"/> is <see langword="default"/>.</exception>
    /// <exception cref="InvalidOperationException">The codec encoded the value to no record.</exception>
    /// <exception cref="IdentityHubStoreException">The store could not complete the write.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    public ValueTask<IdentityHubWriteResult> TryReplaceAsync(
        TRecord value,
        IdentityHubRecordVersion expectedVersion,
        CancellationToken cancellationToken = default)
    {
        return _store.TryReplaceAsync(Name, Encode(value), expectedVersion, cancellationToken);
    }

    /// <summary>
    /// Removes the value stored under a key, when its version is the expected one.
    /// </summary>
    /// <param name="key">The key of the record that stores the value.</param>
    /// <param name="expectedVersion">The version of the state the caller read.</param>
    /// <param name="cancellationToken">The token that cancels the write.</param>
    /// <returns>
    /// <see langword="true"/> when this call removed the record. Of the callers that name one
    /// version, at most one is answered <see langword="true"/>.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="key"/> or <paramref name="expectedVersion"/> is <see langword="default"/>.
    /// </exception>
    /// <exception cref="IdentityHubStoreException">The store could not complete the write.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was canceled.</exception>
    public ValueTask<bool> TryRemoveAsync(
        IdentityHubRecordKey key,
        IdentityHubRecordVersion expectedVersion,
        CancellationToken cancellationToken = default)
    {
        return _store.TryRemoveAsync(Name, key, expectedVersion, cancellationToken);
    }

    private async ValueTask<IdentityHubRecordPage> ReadPageAsync(IdentityHubRecordQuery query, CancellationToken cancellationToken)
    {
        IdentityHubRecordPage? page = await _store.QueryAsync(Name, query, cancellationToken).ConfigureAwait(false);
        return page ?? throw new IdentityHubStoreException(
            IdentityHubErrorCode.Unknown,
            string.Format(CultureInfo.CurrentCulture, ErrorMessages.StoreResultRequired, Name))
        {
            Set = Name,
        };
    }

    private IdentityHubRecord Encode(TRecord value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return _codec.Encode(value) ?? throw new InvalidOperationException(
            string.Format(CultureInfo.CurrentCulture, ErrorMessages.CodecRecordRequired, Name));
    }

    private IdentityHubEntry<TRecord> Decode(IdentityHubStoredRecord stored)
    {
        TRecord? value;
        try
        {
            value = _codec.Decode(stored.Record);
        }
        catch (Exception exception) when (exception is not IdentityHubException)
        {
            // A codec reports an unreadable record with whatever its serializer throws
            // (JsonException, FormatException, InvalidOperationException). Each of them means
            // the same thing here, so all are translated; the payload stays out of the message.
            throw IdentityHubStoreException.RecordCorrupt(Name, stored.Record.Key, exception);
        }

        if (value is null)
        {
            throw IdentityHubStoreException.RecordCorrupt(Name, stored.Record.Key);
        }

        return new IdentityHubEntry<TRecord>(stored.Record.Key, value, stored.Version);
    }

    private sealed class DebugView
    {
        private readonly IdentityHubRecordSet<TRecord> _set;

        public DebugView(IdentityHubRecordSet<TRecord> set)
        {
            _set = set;
        }

        public IdentityHubRecordSetName Name => _set.Name;
    }
}
