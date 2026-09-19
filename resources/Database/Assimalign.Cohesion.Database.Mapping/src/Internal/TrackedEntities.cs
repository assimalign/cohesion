using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Mapping;

internal sealed class TrackedEntities<TEntity, TKey, TSnapshot, TTransaction> :
    ITrackedEntities<TEntity, TKey>, ITrackedMapping<TTransaction>
    where TEntity : class where TKey : notnull where TTransaction : class, IMappingTransaction
{
    private readonly IEntityMapper<TEntity, TKey, TSnapshot> _mapper;
    private readonly IEntityChangeWriter<TKey, TSnapshot, TTransaction> _writer;
    private readonly IEqualityComparer<TKey> _keyComparer;
    private readonly Action _ensureIdle;
    private readonly Dictionary<TKey, Entry> _byKey;
    private readonly List<Entry> _entries = [];

    internal TrackedEntities(IEntityMapper<TEntity, TKey, TSnapshot> mapper,
        IEntityChangeWriter<TKey, TSnapshot, TTransaction> writer,
        IEqualityComparer<TKey> keyComparer, Action ensureIdle)
    {
        _mapper = mapper;
        _writer = writer;
        _keyComparer = keyComparer;
        _ensureIdle = ensureIdle;
        _byKey = new Dictionary<TKey, Entry>(keyComparer);
    }

    public TEntity Attach(TEntity entity)
    {
        _ensureIdle();
        ArgumentNullException.ThrowIfNull(entity);
        ValidateKeys();
        TKey key = ReadKey(entity);
        if (_byKey.TryGetValue(key, out Entry? existing))
        {
            return existing.Entity;
        }

        var entry = new Entry(entity, key, _mapper.Capture(entity), added: false);
        _byKey.Add(key, entry);
        _entries.Add(entry);
        return entity;
    }

    public void Add(TEntity entity)
    {
        _ensureIdle();
        ArgumentNullException.ThrowIfNull(entity);
        ValidateKeys();
        TKey key = ReadKey(entity);
        if (_byKey.ContainsKey(key))
        {
            throw new InvalidOperationException("An entity with this identity is already tracked.");
        }

        var entry = new Entry(entity, key, default!, added: true);
        _byKey.Add(key, entry);
        _entries.Add(entry);
    }

    public void Remove(TEntity entity)
    {
        _ensureIdle();
        ArgumentNullException.ThrowIfNull(entity);
        ValidateKeys();
        TKey key = ReadKey(entity);
        if (!_byKey.TryGetValue(key, out Entry? entry) || !ReferenceEquals(entity, entry.Entity))
        {
            throw new InvalidOperationException("Only the canonical tracked instance can be removed.");
        }

        if (entry.Added)
        {
            _byKey.Remove(key);
            _entries.Remove(entry);
        }
        else
        {
            entry.Deleted = true;
        }
    }

    public TEntity? Find(TKey key)
    {
        _ensureIdle();
        ArgumentNullException.ThrowIfNull(key);
        ValidateKeys();
        return _byKey.TryGetValue(key, out Entry? entry) ? entry.Entity : null;
    }

    public void Prepare(List<IPendingChange<TTransaction>> changes)
    {
        ValidateKeys();
        foreach (Entry entry in _entries)
        {
            if (entry.Deleted)
            {
                changes.Add(new PendingChange(this, entry,
                    new EntityChange<TKey, TSnapshot>(EntityChangeKind.Deleted, entry.Key, entry.Original, default!)));
                continue;
            }

            TSnapshot current = _mapper.Capture(entry.Entity);
            if (entry.Added || !_mapper.AreEqual(entry.Original, current))
            {
                changes.Add(new PendingChange(this, entry,
                    new EntityChange<TKey, TSnapshot>(entry.Added ? EntityChangeKind.Added : EntityChangeKind.Modified,
                        entry.Key, entry.Original, current)));
            }
        }
    }

    private TKey ReadKey(TEntity entity)
    {
        TKey key = _mapper.GetKey(entity);
        if (key is null)
        {
            throw new InvalidOperationException("A mapped entity must have a non-null identity.");
        }

        return key;
    }

    private void ValidateKeys()
    {
        foreach (Entry entry in _entries)
        {
            if (!_keyComparer.Equals(entry.Key, ReadKey(entry.Entity)))
            {
                throw new InvalidOperationException("A tracked entity's identity cannot be changed.");
            }
        }
    }

    private sealed class Entry(TEntity entity, TKey key, TSnapshot original, bool added)
    {
        internal TEntity Entity { get; } = entity;
        internal TKey Key { get; } = key;
        internal TSnapshot Original { get; set; } = original;
        internal bool Added { get; set; } = added;
        internal bool Deleted { get; set; }
    }

    private sealed class PendingChange(TrackedEntities<TEntity, TKey, TSnapshot, TTransaction> owner,
        Entry entry, EntityChange<TKey, TSnapshot> change) : IPendingChange<TTransaction>
    {
        public ValueTask ApplyAsync(TTransaction transaction, CancellationToken cancellationToken = default)
            => owner._writer.ApplyAsync(transaction, change, cancellationToken);

        public void Accept()
        {
            if (change.Kind == EntityChangeKind.Deleted)
            {
                owner._byKey.Remove(entry.Key);
                owner._entries.Remove(entry);
            }
            else
            {
                entry.Original = change.Current;
                entry.Added = false;
            }
        }
    }
}
