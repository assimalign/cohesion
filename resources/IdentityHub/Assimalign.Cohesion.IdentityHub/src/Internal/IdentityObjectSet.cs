using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.IdentityHub.Internal;

/// <summary>
/// The directory objects of one kind: the ones defined in code, held in memory and served
/// locked, over the ones a record set stores. It is the one implementation of "code defines,
/// the registry serves, and code wins" that every kind's registry forwards to.
/// </summary>
/// <remarks>
/// <para>
/// The objects defined in code are fixed when the set is created and are never written to the
/// store. A stored object with the identifier of one defined in code is not served by
/// <see cref="FindAsync"/>, <see cref="FindByNameAsync"/>, or <see cref="ListAsync"/>, and a
/// write may not claim a name code holds. Such a stored object is left in the store: it is
/// served again if the code that shadows it is removed, and until then its name stays taken
/// among stored objects.
/// </para>
/// <para>
/// The set holds no connection of its own, does not own the store, and is safe for concurrent
/// use when the store is.
/// </para>
/// </remarks>
/// <typeparam name="TObject">The directory object.</typeparam>
[DebuggerDisplay("{Locked.Count} locked")]
[DebuggerTypeProxy(typeof(IdentityObjectSet<>.DebugView))]
internal sealed class IdentityObjectSet<TObject>
    where TObject : class
{
    private const string lockedPrefix = "c";
    private const string storedPrefix = "s";

    private readonly IdentityObjectCodec<TObject> _codec;
    private readonly IdentityHubRecordSet<TObject> _stored;
    private readonly TObject[] _locked;
    private readonly Dictionary<Guid, TObject> _lockedById;
    private readonly Dictionary<string, TObject> _lockedByName;

    private IdentityObjectSet(
        IdentityObjectCodec<TObject> codec,
        IdentityHubRecordSet<TObject> stored,
        TObject[] locked,
        Dictionary<Guid, TObject> lockedById,
        Dictionary<string, TObject> lockedByName)
    {
        _codec = codec;
        _stored = stored;
        _locked = locked;
        _lockedById = lockedById;
        _lockedByName = lockedByName;
        Locked = Array.AsReadOnly(locked);
    }

    /// <summary>
    /// Gets the objects defined in code, in the order of their identifiers.
    /// </summary>
    internal IReadOnlyList<TObject> Locked { get; }

    /// <summary>
    /// Creates a set over a store, with the objects defined in code.
    /// </summary>
    /// <remarks>
    /// The objects defined in code are checked against each other here, once, so a mistake in
    /// them stops the hub from starting instead of failing a request later: an identifier and a
    /// name may each appear once.
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// <paramref name="defined"/> holds a <see langword="null"/> object, or the same identifier
    /// or name twice. The exception names <paramref name="parameterName"/>.
    /// </exception>
    internal static IdentityObjectSet<TObject> Create(
        IIdentityHubStore store,
        IdentityObjectCodec<TObject> codec,
        IEnumerable<TObject>? defined,
        string parameterName)
    {
        var byId = new Dictionary<Guid, TObject>();
        var byName = new Dictionary<string, TObject>(StringComparer.Ordinal);
        if (defined is not null)
        {
            foreach (TObject value in defined)
            {
                if (value is null)
                {
                    throw new ArgumentException(codec.RequiredMessage, parameterName);
                }

                TObject locked = codec.AsLocked(value);
                if (!byId.TryAdd(codec.IdOf(locked), locked))
                {
                    throw new ArgumentException(
                        string.Format(CultureInfo.CurrentCulture, codec.DuplicateMessage, codec.Describe(locked)),
                        parameterName);
                }

                string name = codec.NameOf(locked);
                if (!byName.TryAdd(name, locked))
                {
                    throw new ArgumentException(
                        string.Format(
                            CultureInfo.CurrentCulture,
                            codec.DuplicateNameMessage,
                            name,
                            codec.Describe(byName[name]),
                            codec.Describe(locked)),
                        parameterName);
                }
            }
        }

        var ordered = new TObject[byId.Count];
        byId.Values.CopyTo(ordered, 0);
        // Guid's order is the ordinal order of the identifiers' text, which is the order a store
        // keeps the stored objects' keys in.
        Array.Sort(ordered, (left, right) => codec.IdOf(left).CompareTo(codec.IdOf(right)));

        return new IdentityObjectSet<TObject>(
            codec,
            IdentityHubRecordSet<TObject>.Create(store, codec),
            ordered,
            byId,
            byName);
    }

    internal ValueTask<TObject?> FindAsync(Guid id, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromCanceled<TObject?>(cancellationToken);
        }

        return _lockedById.TryGetValue(id, out TObject? locked)
            ? new ValueTask<TObject?>(locked)
            : FindStoredAsync(id, cancellationToken);
    }

    /// <summary>
    /// Finds the object that holds a name, given in lowercase.
    /// </summary>
    internal ValueTask<TObject?> FindByNameAsync(string name, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromCanceled<TObject?>(cancellationToken);
        }

        return _lockedByName.TryGetValue(name, out TObject? locked)
            ? new ValueTask<TObject?>(locked)
            : FindStoredByNameAsync(name, cancellationToken);
    }

    internal ValueTask<IdentityObjectPage<TObject>> ListAsync(
        int limit,
        string? continuation,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, IdentityHubRecordQuery.MaxLimit);
        if (continuation is { Length: 0 })
        {
            throw new ArgumentException(ErrorMessages.PageContinuationEmpty, nameof(continuation));
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromCanceled<IdentityObjectPage<TObject>>(cancellationToken);
        }

        // A continuation is "c<offset>" while objects defined in code remain, and
        // "s<store token>" once the stored ones are being read ("s" alone starts them).
        int offset = 0;
        string? storeToken = null;
        bool readsStore = false;
        if (continuation is not null)
        {
            if (continuation.StartsWith(storedPrefix, StringComparison.Ordinal))
            {
                readsStore = true;
                storeToken = continuation.Length is 1 ? null : continuation[1..];
            }
            else if (!continuation.StartsWith(lockedPrefix, StringComparison.Ordinal) ||
                !int.TryParse(continuation.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out offset) ||
                offset < 1 ||
                offset >= _locked.Length)
            {
                return ValueTask.FromException<IdentityObjectPage<TObject>>(
                    IdentityHubStoreException.ContinuationInvalid(_stored.Name));
            }
        }

        var items = new List<TObject>();
        if (!readsStore)
        {
            int count = Math.Min(limit, _locked.Length - offset);
            for (int index = 0; index < count; index++)
            {
                items.Add(_locked[offset + index]);
            }

            offset += count;
            if (offset < _locked.Length)
            {
                return new ValueTask<IdentityObjectPage<TObject>>(new IdentityObjectPage<TObject>(
                    items,
                    string.Concat(lockedPrefix, offset.ToString(CultureInfo.InvariantCulture))));
            }

            if (items.Count == limit)
            {
                return new ValueTask<IdentityObjectPage<TObject>>(new IdentityObjectPage<TObject>(items, storedPrefix));
            }
        }

        return ListStoredAsync(items, limit - items.Count, storeToken, cancellationToken);
    }

    internal ValueTask<IdentityObjectWriteResult> TryAddAsync(TObject value, CancellationToken cancellationToken)
    {
        return TryRefuse(value, out IdentityObjectWriteResult refusal)
            ? new ValueTask<IdentityObjectWriteResult>(refusal)
            : AddStoredAsync(value, cancellationToken);
    }

    internal ValueTask<IdentityObjectWriteResult> TryReplaceAsync(
        TObject value,
        IdentityHubRecordVersion expectedVersion,
        CancellationToken cancellationToken)
    {
        return TryRefuse(value, out IdentityObjectWriteResult refusal)
            ? new ValueTask<IdentityObjectWriteResult>(refusal)
            : ReplaceStoredAsync(value, expectedVersion, cancellationToken);
    }

    internal ValueTask<IdentityObjectWriteResult> TryRemoveAsync(
        Guid id,
        IdentityHubRecordVersion expectedVersion,
        CancellationToken cancellationToken)
    {
        return _lockedById.ContainsKey(id)
            ? new ValueTask<IdentityObjectWriteResult>(IdentityObjectWriteResult.Locked)
            : RemoveStoredAsync(id, expectedVersion, cancellationToken);
    }

    private bool TryRefuse(TObject value, out IdentityObjectWriteResult refusal)
    {
        if (_lockedById.ContainsKey(_codec.IdOf(value)))
        {
            refusal = IdentityObjectWriteResult.Locked;
            return true;
        }

        if (_lockedByName.ContainsKey(_codec.NameOf(value)))
        {
            refusal = IdentityObjectWriteResult.NameConflict;
            return true;
        }

        refusal = default;
        return false;
    }

    private async ValueTask<TObject?> FindStoredAsync(Guid id, CancellationToken cancellationToken)
    {
        IdentityHubEntry<TObject>? entry = await _stored
            .FindAsync(IdentityObjectCodec<TObject>.KeyOf(id), cancellationToken)
            .ConfigureAwait(false);
        return entry is null ? null : _codec.AsStored(entry.Value, entry.Version);
    }

    private async ValueTask<TObject?> FindStoredByNameAsync(string name, CancellationToken cancellationToken)
    {
        IdentityHubEntry<TObject>? entry = await _stored
            .FindByIndexAsync(IdentityObjectIndexes.Name, new IdentityHubRecordKey(name), cancellationToken)
            .ConfigureAwait(false);

        // A stored object that code shadows is not served under its name either.
        return entry is null || _lockedById.ContainsKey(_codec.IdOf(entry.Value))
            ? null
            : _codec.AsStored(entry.Value, entry.Version);
    }

    private async ValueTask<IdentityObjectPage<TObject>> ListStoredAsync(
        List<TObject> items,
        int limit,
        string? storeToken,
        CancellationToken cancellationToken)
    {
        IdentityHubEntryPage<TObject> page = await _stored
            .QueryAsync(IdentityHubRecordQuery.All(limit, storeToken), cancellationToken)
            .ConfigureAwait(false);

        for (int index = 0; index < page.Entries.Count; index++)
        {
            IdentityHubEntry<TObject> entry = page.Entries[index];
            if (!_lockedById.ContainsKey(_codec.IdOf(entry.Value)))
            {
                items.Add(_codec.AsStored(entry.Value, entry.Version));
            }
        }

        return new IdentityObjectPage<TObject>(
            items,
            page.Continuation is null ? null : string.Concat(storedPrefix, page.Continuation));
    }

    private async ValueTask<IdentityObjectWriteResult> AddStoredAsync(TObject value, CancellationToken cancellationToken)
    {
        IdentityHubWriteResult result = await _stored.TryAddAsync(value, cancellationToken).ConfigureAwait(false);
        return result.Status switch
        {
            IdentityHubWriteStatus.Written => IdentityObjectWriteResult.Written(result.Version),
            IdentityHubWriteStatus.KeyConflict => IdentityObjectWriteResult.IdConflict,
            IdentityHubWriteStatus.IndexConflict => IdentityObjectWriteResult.NameConflict,
            _ => throw Unexpected(result.Status),
        };
    }

    private async ValueTask<IdentityObjectWriteResult> ReplaceStoredAsync(
        TObject value,
        IdentityHubRecordVersion expectedVersion,
        CancellationToken cancellationToken)
    {
        IdentityHubWriteResult result =
            await _stored.TryReplaceAsync(value, expectedVersion, cancellationToken).ConfigureAwait(false);
        return result.Status switch
        {
            IdentityHubWriteStatus.Written => IdentityObjectWriteResult.Written(result.Version),
            IdentityHubWriteStatus.VersionConflict => IdentityObjectWriteResult.VersionConflict,
            IdentityHubWriteStatus.IndexConflict => IdentityObjectWriteResult.NameConflict,
            _ => throw Unexpected(result.Status),
        };
    }

    private async ValueTask<IdentityObjectWriteResult> RemoveStoredAsync(
        Guid id,
        IdentityHubRecordVersion expectedVersion,
        CancellationToken cancellationToken)
    {
        bool removed = await _stored
            .TryRemoveAsync(IdentityObjectCodec<TObject>.KeyOf(id), expectedVersion, cancellationToken)
            .ConfigureAwait(false);
        return removed ? IdentityObjectWriteResult.Removed : IdentityObjectWriteResult.VersionConflict;
    }

    private IdentityHubStoreException Unexpected(IdentityHubWriteStatus status)
    {
        return new IdentityHubStoreException(
            IdentityHubErrorCode.Unknown,
            string.Format(CultureInfo.CurrentCulture, ErrorMessages.StoreStatusUnexpected, _stored.Name, status))
        {
            Set = _stored.Name,
        };
    }

    private sealed class DebugView
    {
        private readonly IdentityObjectSet<TObject> _set;

        public DebugView(IdentityObjectSet<TObject> set)
        {
            _set = set;
        }

        public IReadOnlyList<TObject> Locked => _set.Locked;
    }
}
