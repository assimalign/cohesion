using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Sql.Internal;

using Assimalign.Cohesion.Database.Sql.Storage;
using Assimalign.Cohesion.Database.Storage;

/// <summary>
/// In-memory storage strategy that uses MemoryStreams for all three storage files.
/// Useful for unit testing and embedded scenarios.
/// </summary>
internal sealed class InMemorySqlStorageStrategy : ISqlStorageStrategy
{
    private readonly HashSet<string> _databases = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _syncRoot = new();
    private readonly StorageCommitDurability? _durability;

    internal InMemorySqlStorageStrategy(StorageCommitDurability? durability = null)
    {
        _durability = durability;
    }

    /// <inheritdoc />
    public SqlStorage CreateStorage(string databaseName)
    {
        lock (_syncRoot)
        {
            if (!_databases.Add(databaseName))
            {
                throw new DatabaseException($"In-memory storage for database '{databaseName}' already exists.");
            }
        }

        return SqlStorage.Create(StorageStream.FromInMemory(), StorageStream.FromInMemory(), StorageStream.FromInMemory(), databaseName, _durability);
    }

    /// <inheritdoc />
    public SqlStorage OpenStorage(string databaseName)
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
        return SqlStorage.Create(StorageStream.FromInMemory(), StorageStream.FromInMemory(), StorageStream.FromInMemory(), databaseName, _durability);
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
