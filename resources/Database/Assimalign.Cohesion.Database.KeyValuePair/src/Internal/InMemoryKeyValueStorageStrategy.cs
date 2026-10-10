using Assimalign.Cohesion.Database.KeyValuePair.Storage;
using Assimalign.Cohesion.Database.Storage;

namespace Assimalign.Cohesion.Database.KeyValuePair.Internal;

/// <summary>
/// In-memory storage strategy: each file set's data, journal and backup files are memory streams
/// the strategy keeps for the engine's lifetime, so a closed database reopens with its data.
/// Useful for unit testing and embedded scenarios.
/// </summary>
/// <remarks>
/// A reopen runs the same recovery a file-based open does, over the bytes the closed storage left
/// (<see cref="DatabaseMemoryFiles"/>): a database its holder closed, or one that went offline,
/// reopens with every commit its journal holds (#1272; owner decision 33 of 2026-10-06, #1289).
/// Until then an open returned fresh empty storage and silently lost the database's entries.
/// </remarks>
internal sealed class InMemoryKeyValueStorageStrategy : KeyValueStorageStrategy
{
    private readonly DatabaseMemoryFiles _files = new();
    private readonly StorageCommitDurability? _durability;

    internal InMemoryKeyValueStorageStrategy(StorageCommitDurability? durability = null)
    {
        _durability = durability;
    }

    /// <inheritdoc />
    public override KeyValueStorage CreateStorage(string databaseName)
    {
        if (!_files.TryCreate(databaseName, out var data, out var journal, out var backup))
        {
            throw new DatabaseException($"In-memory storage for database '{databaseName}' already exists.");
        }

        return KeyValueStorage.Create(data, journal, backup, databaseName, _durability);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The open-time checkpoint is deferred, as the file-based strategy defers it: the engine runs
    /// transaction-recovery analysis over the recovered journal first.
    /// </remarks>
    public override KeyValueStorage OpenStorage(string databaseName)
    {
        if (!_files.TryOpen(databaseName, out var data, out var journal, out var backup))
        {
            throw new DatabaseException($"In-memory storage for database '{databaseName}' does not exist.");
        }

        try
        {
            return KeyValueStorage.Open(data, journal, backup, checkpointOnOpen: false, _durability);
        }
        catch
        {
            backup.Dispose();
            journal.Dispose();
            data.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    public override void DropStorage(string databaseName) => _files.Drop(databaseName);

    /// <inheritdoc />
    public override bool StorageExists(string databaseName) => _files.Exists(databaseName);

    /// <summary>
    /// Releases every file set's bytes, once the engine that owns the strategy closed its databases
    /// at its disposal: nothing opens them again.
    /// </summary>
    internal void Release() => _files.Clear();
}
