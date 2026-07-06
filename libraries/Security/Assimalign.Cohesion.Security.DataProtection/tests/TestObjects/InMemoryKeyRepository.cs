using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Security.DataProtection.Tests;

/// <summary>
/// An in-memory <see cref="IKeyRepository"/> for tests that need to observe or share the ring's
/// persisted documents without touching the file system.
/// </summary>
internal sealed class InMemoryKeyRepository : IKeyRepository
{
    private readonly Dictionary<string, byte[]> _store = new(StringComparer.Ordinal);

    public int Count => _store.Count;

    public IReadOnlyList<KeyDocument> GetAllKeys()
    {
        List<KeyDocument> documents = new(_store.Count);
        foreach (KeyValuePair<string, byte[]> entry in _store)
        {
            documents.Add(new KeyDocument(entry.Key, (byte[])entry.Value.Clone()));
        }

        return documents;
    }

    public void StoreKey(KeyDocument key)
    {
        _store[key.Name] = key.Content.ToArray();
    }
}
