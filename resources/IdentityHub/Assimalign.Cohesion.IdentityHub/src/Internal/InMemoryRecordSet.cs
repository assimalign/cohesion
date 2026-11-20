using System;
using System.Buffers.Text;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;

namespace Assimalign.Cohesion.IdentityHub.Internal;

/// <summary>
/// One record set of the in-memory store. Every operation runs under the set's lock, which is
/// what makes a conditional write and its checks one step.
/// </summary>
[DebuggerDisplay("{Name,nq}")]
[DebuggerTypeProxy(typeof(DebugView))]
internal sealed class InMemoryRecordSet
{
    private const string ContinuationPrefix = "m1.";

    private static readonly UTF8Encoding _strictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly Lock _gate = new();
    private readonly Dictionary<string, IdentityHubStoredRecord> _records = new(StringComparer.Ordinal);
    private readonly SortedSet<string> _keys = new(StringComparer.Ordinal);
    private readonly Dictionary<InMemoryIndexSlot, SortedSet<string>> _entries = new();
    private readonly Dictionary<InMemoryIndexSlot, string> _unique = new();
    private readonly SortedSet<InMemoryExpiry> _expiries = new();

    internal InMemoryRecordSet(IdentityHubRecordSetName name)
    {
        Name = name;
    }

    public IdentityHubRecordSetName Name { get; }

    /// <summary>
    /// The number of records the set holds in memory, expired ones not yet reclaimed included.
    /// </summary>
    internal int HeldCount
    {
        get
        {
            lock (_gate)
            {
                return _records.Count;
            }
        }
    }

    internal IdentityHubStoredRecord? Find(IdentityHubRecordKey key, DateTimeOffset now)
    {
        lock (_gate)
        {
            return FindLive(key.Value, now);
        }
    }

    internal IdentityHubWriteResult TryAdd(IdentityHubRecord record, DateTimeOffset now, Func<IdentityHubRecordVersion> nextVersion)
    {
        lock (_gate)
        {
            Reclaim(now);
            string key = record.Key.Value;
            if (FindLive(key, now) is not null)
            {
                return IdentityHubWriteResult.KeyConflict;
            }

            if (FindConflict(record, key, now) is { } conflict)
            {
                return IdentityHubWriteResult.IndexConflict(conflict);
            }

            IdentityHubRecordVersion version = nextVersion();
            Store(key, new IdentityHubStoredRecord(record, version));
            return IdentityHubWriteResult.Written(version);
        }
    }

    internal IdentityHubWriteResult TryReplace(
        IdentityHubRecord record,
        IdentityHubRecordVersion expectedVersion,
        DateTimeOffset now,
        Func<IdentityHubRecordVersion> nextVersion)
    {
        lock (_gate)
        {
            Reclaim(now);
            string key = record.Key.Value;
            IdentityHubStoredRecord? existing = FindLive(key, now);
            if (existing is null || existing.Version != expectedVersion)
            {
                return IdentityHubWriteResult.VersionConflict;
            }

            if (FindConflict(record, key, now) is { } conflict)
            {
                return IdentityHubWriteResult.IndexConflict(conflict);
            }

            Purge(key);
            IdentityHubRecordVersion version = nextVersion();
            Store(key, new IdentityHubStoredRecord(record, version));
            return IdentityHubWriteResult.Written(version);
        }
    }

    internal bool TryRemove(IdentityHubRecordKey key, IdentityHubRecordVersion expectedVersion, DateTimeOffset now)
    {
        lock (_gate)
        {
            IdentityHubStoredRecord? existing = FindLive(key.Value, now);
            if (existing is null || existing.Version != expectedVersion)
            {
                return false;
            }

            Purge(key.Value);
            return true;
        }
    }

    internal IdentityHubRecordPage Query(IdentityHubRecordQuery query, DateTimeOffset now)
    {
        string? after = query.Continuation is null ? null : ReadContinuation(query.Continuation);
        lock (_gate)
        {
            SortedSet<string>? source = _keys;
            if (query.Index is { } index && query.Value is { } value)
            {
                _entries.TryGetValue(new InMemoryIndexSlot(index.Value, value.Value), out source);
            }

            if (source is null || source.Count is 0)
            {
                return IdentityHubRecordPage.Empty;
            }

            IEnumerable<string> keys = source;
            if (after is not null)
            {
                if (StringComparer.Ordinal.Compare(after, source.Max) >= 0)
                {
                    return IdentityHubRecordPage.Empty;
                }

                keys = source.GetViewBetween(after, source.Max);
            }

            var page = new List<IdentityHubStoredRecord>(Math.Min(query.Limit, source.Count));
            List<string>? expired = null;
            bool more = false;
            foreach (string key in keys)
            {
                if (after is not null && string.Equals(key, after, StringComparison.Ordinal))
                {
                    continue;
                }

                IdentityHubStoredRecord stored = _records[key];
                if (IsExpired(stored, now))
                {
                    (expired ??= new List<string>()).Add(key);
                    continue;
                }

                if (page.Count == query.Limit)
                {
                    more = true;
                    break;
                }

                page.Add(stored);
            }

            if (expired is not null)
            {
                // Removed after the enumeration: a set cannot change while it is being read.
                for (int position = 0; position < expired.Count; position++)
                {
                    Purge(expired[position]);
                }
            }

            if (page.Count is 0)
            {
                return IdentityHubRecordPage.Empty;
            }

            string? continuation = more ? WriteContinuation(page[^1].Record.Key.Value) : null;
            return new IdentityHubRecordPage(page, continuation);
        }
    }

    private static bool IsExpired(IdentityHubStoredRecord stored, DateTimeOffset now)
    {
        return stored.Record.ExpiresAt is { } expiresAt && expiresAt <= now;
    }

    private static string WriteContinuation(string key)
    {
        return ContinuationPrefix + Base64Url.EncodeToString(Encoding.UTF8.GetBytes(key));
    }

    private string ReadContinuation(string continuation)
    {
        if (!continuation.StartsWith(ContinuationPrefix, StringComparison.Ordinal))
        {
            throw IdentityHubStoreException.ContinuationInvalid(Name);
        }

        string key;
        try
        {
            key = _strictUtf8.GetString(Base64Url.DecodeFromChars(continuation.AsSpan(ContinuationPrefix.Length)));
        }
        catch (FormatException exception)
        {
            throw IdentityHubStoreException.ContinuationInvalid(Name, exception);
        }
        catch (ArgumentException exception)
        {
            // DecoderFallbackException: the bytes are not the UTF-8 form of a key.
            throw IdentityHubStoreException.ContinuationInvalid(Name, exception);
        }

        if (!IdentityHubGrammar.IsKey(key))
        {
            throw IdentityHubStoreException.ContinuationInvalid(Name);
        }

        return key;
    }

    private IdentityHubStoredRecord? FindLive(string key, DateTimeOffset now)
    {
        if (!_records.TryGetValue(key, out IdentityHubStoredRecord? stored))
        {
            return null;
        }

        if (IsExpired(stored, now))
        {
            Purge(key);
            return null;
        }

        return stored;
    }

    private IdentityHubRecordIndexName? FindConflict(IdentityHubRecord record, string key, DateTimeOffset now)
    {
        IReadOnlyList<IdentityHubRecordIndex> indexes = record.Indexes;
        for (int position = 0; position < indexes.Count; position++)
        {
            IdentityHubRecordIndex index = indexes[position];
            if (!index.IsUnique)
            {
                continue;
            }

            var slot = new InMemoryIndexSlot(index.Name.Value, index.Value.Value);
            if (!_unique.TryGetValue(slot, out string? owner) || string.Equals(owner, key, StringComparison.Ordinal))
            {
                continue;
            }

            // An expired owner holds nothing: FindLive removes it and frees the entry.
            if (FindLive(owner, now) is not null)
            {
                return index.Name;
            }
        }

        return null;
    }

    // Only a write makes the set grow, so every write first drops what has expired. A record
    // that expires and is never read again is then held until the set's next write at most.
    private void Reclaim(DateTimeOffset now)
    {
        while (_expiries.Count > 0)
        {
            InMemoryExpiry earliest = _expiries.Min;
            if (earliest.ExpiresAt > now)
            {
                return;
            }

            // Removed here as well as in Purge, so the loop ends whatever Purge finds.
            _expiries.Remove(earliest);
            Purge(earliest.Key);
        }
    }

    private void Store(string key, IdentityHubStoredRecord stored)
    {
        _records[key] = stored;
        _keys.Add(key);
        if (stored.Record.ExpiresAt is { } expiresAt)
        {
            _expiries.Add(new InMemoryExpiry(expiresAt, key));
        }

        IReadOnlyList<IdentityHubRecordIndex> indexes = stored.Record.Indexes;
        for (int position = 0; position < indexes.Count; position++)
        {
            IdentityHubRecordIndex index = indexes[position];
            var slot = new InMemoryIndexSlot(index.Name.Value, index.Value.Value);
            if (!_entries.TryGetValue(slot, out SortedSet<string>? keys))
            {
                keys = new SortedSet<string>(StringComparer.Ordinal);
                _entries.Add(slot, keys);
            }

            keys.Add(key);
            if (index.IsUnique)
            {
                _unique[slot] = key;
            }
        }
    }

    private void Purge(string key)
    {
        if (!_records.Remove(key, out IdentityHubStoredRecord? stored))
        {
            return;
        }

        _keys.Remove(key);
        if (stored.Record.ExpiresAt is { } expiresAt)
        {
            _expiries.Remove(new InMemoryExpiry(expiresAt, key));
        }

        IReadOnlyList<IdentityHubRecordIndex> indexes = stored.Record.Indexes;
        for (int position = 0; position < indexes.Count; position++)
        {
            IdentityHubRecordIndex index = indexes[position];
            var slot = new InMemoryIndexSlot(index.Name.Value, index.Value.Value);
            if (_entries.TryGetValue(slot, out SortedSet<string>? keys))
            {
                keys.Remove(key);
                if (keys.Count is 0)
                {
                    _entries.Remove(slot);
                }
            }

            if (index.IsUnique &&
                _unique.TryGetValue(slot, out string? owner) &&
                string.Equals(owner, key, StringComparison.Ordinal))
            {
                _unique.Remove(slot);
            }
        }
    }

    private sealed class DebugView
    {
        private readonly InMemoryRecordSet _set;

        public DebugView(InMemoryRecordSet set)
        {
            _set = set;
        }

        public IdentityHubRecordSetName Name => _set.Name;
    }
}
