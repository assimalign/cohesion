using System;
using System.Collections.Generic;
using System.IO;

namespace Assimalign.Cohesion.Database.KeyValuePair.Internal;

using Assimalign.Cohesion.Database.KeyValuePair.Storage;

/// <summary>
/// In-memory storage strategy that uses MemoryStreams for all three storage files.
/// Useful for unit testing and embedded scenarios.
/// </summary>
internal sealed class InMemoryKeyValueStorageStrategy : IKeyValueStorageStrategy
{
    private readonly HashSet<string> _databases = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _syncRoot = new();

    /// <inheritdoc />
    public KeyValueStorage CreateStorage(string databaseName)
    {
        lock (_syncRoot)
        {
            if (!_databases.Add(databaseName))
            {
                throw new DatabaseException($"In-memory storage for database '{databaseName}' already exists.");
            }
        }

        return KeyValueStorage.Create(new MemoryStream(), new MemoryStream(), new MemoryStream(), databaseName);
    }

    /// <inheritdoc />
    public KeyValueStorage OpenStorage(string databaseName)
    {
        lock (_syncRoot)
        {
            if (!_databases.Contains(databaseName))
            {
                throw new DatabaseException($"In-memory storage for database '{databaseName}' does not exist.");
            }
        }

        // In-memory storage cannot truly reopen persisted data without snapshot support.
        // For now, open returns a fresh instance. Recovery scenarios require file-based storage.
        return KeyValueStorage.Create(new MemoryStream(), new MemoryStream(), new MemoryStream(), databaseName);
    }

    /// <inheritdoc />
    public void DropStorage(string databaseName)
    {
        lock (_syncRoot)
        {
            _databases.Remove(databaseName);
        }
    }

    /// <inheritdoc />
    public bool StorageExists(string databaseName)
    {
        lock (_syncRoot)
        {
            return _databases.Contains(databaseName);
        }
    }
}
