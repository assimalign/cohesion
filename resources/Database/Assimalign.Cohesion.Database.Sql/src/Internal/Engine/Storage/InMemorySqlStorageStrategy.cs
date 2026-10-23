using System.Threading;
using Assimalign.Cohesion.Database.Sql.Storage;
using Assimalign.Cohesion.Database.Storage;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// In-memory storage strategy: each file set's data, journal and backup files are memory streams
/// the strategy keeps for the engine's lifetime, so a closed database reopens with its data.
/// Useful for unit testing and embedded scenarios.
/// </summary>
/// <remarks>
/// A reopen runs the same recovery a file-based open does, over the bytes the closed storage left
/// (<see cref="DatabaseMemoryFiles"/>): a database its holder closed, or one that went offline,
/// reopens with every commit its journal holds (#1272; owner decision 33 of 2026-10-06, #1289).
/// Until then an open returned fresh empty storage and silently lost the database's rows.
/// </remarks>
internal sealed class InMemorySqlStorageStrategy : SqlStorageStrategy
{
    private readonly DatabaseMemoryFiles _files = new();
    private readonly StorageCommitDurability? _durability;

    internal InMemorySqlStorageStrategy(StorageCommitDurability? durability = null)
    {
        _durability = durability;
    }

    /// <inheritdoc />
    public override SqlStorage CreateStorage(string databaseName, bool catalog)
    {
        // The data set comes first and claims the database, as a directory on disk does: any file
        // set of the name, in any spelling, means the database exists.
        if (!catalog && (_files.Exists(databaseName, DataFileSet) || _files.Exists(databaseName, DatabaseFileNames.CatalogFileSet)))
        {
            throw new DatabaseException(string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.StorageStrategyCreateStorageDatabase, databaseName));
        }

        if (!_files.TryCreate(databaseName, GetFileSet(catalog), out var data, out var journal, out var backup))
        {
            throw new DatabaseException(string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.StorageStrategyCreateStorageDatabase2, GetLabel(databaseName, catalog)));
        }

        return SqlStorage.Create(data, journal, backup, GetLabel(databaseName, catalog), _durability);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The open-time checkpoint is deferred, as the file-based strategy defers it: the engine runs
    /// transaction-recovery analysis over the recovered journal first.
    /// </remarks>
    public override SqlStorage OpenStorage(string databaseName, bool catalog, CancellationToken cancellationToken = default)
    {
        if (!_files.TryOpen(databaseName, GetFileSet(catalog), out var data, out var journal, out var backup))
        {
            throw new DatabaseException(string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.SqlStorageStrategyOpenStorageDatabase2, GetLabel(databaseName, catalog)));
        }

        try
        {
            return SqlStorage.Open(data, journal, backup, checkpointOnOpen: false, _durability, cancellationToken);
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
    public override bool StorageExists(string databaseName, bool catalog) => _files.Exists(databaseName, GetFileSet(catalog));

    /// <summary>
    /// Releases every file set's bytes, once the engine that owns the strategy closed its databases
    /// at its disposal: nothing opens them again.
    /// </summary>
    internal void Release() => _files.Clear();
}
