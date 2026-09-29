using System.IO;

namespace Assimalign.Cohesion.Database.Sql.Internal;

using Assimalign.Cohesion.Database.Sql.Storage;
using Assimalign.Cohesion.Database.Storage;

/// <summary>
/// File-based storage strategy that creates subdirectories under a root path
/// with .dat, .log, and .bak files for each database.
/// </summary>
internal sealed class FileSystemSqlStorageStrategy : ISqlStorageStrategy
{
    private readonly string _rootPath;
    private readonly StorageCommitDurability? _durability;

    internal FileSystemSqlStorageStrategy(string rootPath, StorageCommitDurability? durability = null)
    {
        _rootPath = rootPath;
        _durability = durability;
    }

    /// <inheritdoc />
    public SqlStorage CreateStorage(string databaseName)
    {
        var dbDirectory = Path.Combine(_rootPath, databaseName);

        if (Directory.Exists(dbDirectory))
        {
            throw new DatabaseException($"Storage for database '{databaseName}' already exists.");
        }

        Directory.CreateDirectory(dbDirectory);

        var dataStream = StorageStream.FromFile(
            Path.Combine(dbDirectory, $"{databaseName}.dat"),
            FileMode.CreateNew,
            FileShare.Read);

        StorageStream? journalStream = null;
        StorageStream? backupStream = null;
        try
        {
            journalStream = StorageStream.FromFile(
                Path.Combine(dbDirectory, $"{databaseName}.log"),
                FileMode.CreateNew,
                FileShare.Read);
            backupStream = StorageStream.FromFile(
                Path.Combine(dbDirectory, $"{databaseName}.bak"),
                FileMode.CreateNew,
                FileShare.Read);

            return SqlStorage.Create(dataStream, journalStream, backupStream, databaseName, _durability);
        }
        catch
        {
            backupStream?.Dispose();
            journalStream?.Dispose();
            dataStream.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    public SqlStorage OpenStorage(string databaseName)
    {
        var dbDirectory = Path.Combine(_rootPath, databaseName);
        var dataFilePath = Path.Combine(dbDirectory, $"{databaseName}.dat");

        if (!Directory.Exists(dbDirectory) || !File.Exists(dataFilePath))
        {
            throw new DatabaseException($"Storage for database '{databaseName}' does not exist.");
        }

        var dataStream = StorageStream.FromFile(
            dataFilePath,
            FileMode.Open,
            FileShare.Read);

        StorageStream? journalStream = null;
        StorageStream? backupStream = null;
        try
        {
            journalStream = StorageStream.FromFile(
                Path.Combine(dbDirectory, $"{databaseName}.log"),
                FileMode.OpenOrCreate,
                FileShare.Read);
            backupStream = StorageStream.FromFile(
                Path.Combine(dbDirectory, $"{databaseName}.bak"),
                FileMode.OpenOrCreate,
                FileShare.Read);

            // Defer the open-time checkpoint: the engine's transaction coordinator
            // analyzes the recovered journal before checkpoint truncates its records.
            return SqlStorage.Open(dataStream, journalStream, backupStream, checkpointOnOpen: false, _durability);
        }
        catch
        {
            backupStream?.Dispose();
            journalStream?.Dispose();
            dataStream.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    public void DropStorage(string databaseName)
    {
        var dbDirectory = Path.Combine(_rootPath, databaseName);

        if (Directory.Exists(dbDirectory))
        {
            Directory.Delete(dbDirectory, recursive: true);
        }
    }

    /// <inheritdoc />
    public bool StorageExists(string databaseName)
    {
        var dbDirectory = Path.Combine(_rootPath, databaseName);
        var dataFilePath = Path.Combine(dbDirectory, $"{databaseName}.dat");
        return Directory.Exists(dbDirectory) && File.Exists(dataFilePath);
    }
}
