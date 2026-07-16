using System.IO;

namespace Assimalign.Cohesion.Database.KeyValuePair.Internal;

using Assimalign.Cohesion.Database.KeyValuePair.Storage;

/// <summary>
/// File-based storage strategy that creates subdirectories under a root path
/// with .dat, .log, and .bak files for each database.
/// </summary>
internal sealed class FileSystemKeyValueStorageStrategy : IKeyValueStorageStrategy
{
    private readonly string _rootPath;

    internal FileSystemKeyValueStorageStrategy(string rootPath)
    {
        _rootPath = rootPath;
    }

    /// <inheritdoc />
    public KeyValueStorage CreateStorage(string databaseName)
    {
        var dbDirectory = Path.Combine(_rootPath, databaseName);

        if (Directory.Exists(dbDirectory))
        {
            throw new DatabaseException($"Storage for database '{databaseName}' already exists.");
        }

        Directory.CreateDirectory(dbDirectory);

        var dataStream = new FileStream(
            Path.Combine(dbDirectory, $"{databaseName}.dat"),
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.Read);

        var journalStream = new FileStream(
            Path.Combine(dbDirectory, $"{databaseName}.log"),
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.Read);

        var backupStream = new FileStream(
            Path.Combine(dbDirectory, $"{databaseName}.bak"),
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.Read);

        return KeyValueStorage.Create(dataStream, journalStream, backupStream, databaseName);
    }

    /// <inheritdoc />
    public KeyValueStorage OpenStorage(string databaseName)
    {
        var dbDirectory = Path.Combine(_rootPath, databaseName);
        var dataFilePath = Path.Combine(dbDirectory, $"{databaseName}.dat");

        if (!Directory.Exists(dbDirectory) || !File.Exists(dataFilePath))
        {
            throw new DatabaseException($"Storage for database '{databaseName}' does not exist.");
        }

        var dataStream = new FileStream(
            dataFilePath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.Read);

        var journalStream = new FileStream(
            Path.Combine(dbDirectory, $"{databaseName}.log"),
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.Read);

        var backupStream = new FileStream(
            Path.Combine(dbDirectory, $"{databaseName}.bak"),
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.Read);

        // Defer the open-time checkpoint: the engine's transaction coordinator
        // analyzes the recovered journal (TransactionRecovery.Analyze) before the
        // truncation destroys its lifecycle records, then checkpoints itself.
        return KeyValueStorage.Open(dataStream, journalStream, backupStream, checkpointOnOpen: false);
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
