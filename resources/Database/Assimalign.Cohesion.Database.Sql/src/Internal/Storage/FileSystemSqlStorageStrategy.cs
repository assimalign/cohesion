using System.Threading;
using System.IO;

namespace Assimalign.Cohesion.Database.Sql.Internal;

using Assimalign.Cohesion.Database.Sql.Storage;
using Assimalign.Cohesion.Database.Storage;

/// <summary>
/// File-based storage strategy: each database is one directory under the root path, named for its
/// lower-case storage name (<see cref="DatabaseFileNames"/>), holding its data file set
/// (<c>sql.dat</c>, <c>sql.log</c>, <c>sql.bak</c>) and its catalog file set (<c>catalog.dat</c>,
/// <c>catalog.log</c>, <c>catalog.bak</c>).
/// </summary>
/// <remarks>
/// Every member maps the name it is given through the one shared mapping, so every spelling of a
/// name reaches the same directory on a case-sensitive file system too (#1392): before it, a
/// database created as <c>sales</c> and opened as <c>SALES</c> on Linux was not found, and the
/// engine's open-or-create made a second, empty database.
/// </remarks>
internal sealed class FileSystemSqlStorageStrategy : SqlStorageStrategy
{
    private readonly string _rootPath;
    private readonly StorageCommitDurability? _durability;

    internal FileSystemSqlStorageStrategy(string rootPath, StorageCommitDurability? durability = null)
    {
        _rootPath = rootPath;
        _durability = durability;
    }

    /// <inheritdoc />
    public override SqlStorage CreateStorage(string databaseName, bool catalog)
    {
        var directory = DatabaseFileNames.GetDirectory(_rootPath, databaseName);

        // The data set comes first and claims the database's directory. One already there is the
        // database, or, without the data file, what an interrupted create or drop left, which a
        // drop deletes; a create takes over neither, so a stale catalog set never meets new data.
        if (!catalog && Directory.Exists(directory))
        {
            throw new DatabaseException(File.Exists(DatabaseFileNames.GetFiles(directory, GetFileSet(catalog)).Data)
                ? $"Storage for database '{databaseName}' already exists."
                : DatabaseFileNames.GetLeftoverRefusal(databaseName, directory));
        }

        Directory.CreateDirectory(directory);
        var files = DatabaseFileNames.GetFiles(directory, GetFileSet(catalog));

        var dataStream = StorageStream.FromFile(files.Data, FileMode.CreateNew, FileShare.Read);

        StorageStream? journalStream = null;
        StorageStream? backupStream = null;
        try
        {
            journalStream = StorageStream.FromFile(files.Journal, FileMode.CreateNew, FileShare.Read);
            backupStream = StorageStream.FromFile(files.Backup, FileMode.CreateNew, FileShare.Read);

            return SqlStorage.Create(dataStream, journalStream, backupStream, GetLabel(databaseName, catalog), _durability);
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
    public override SqlStorage OpenStorage(string databaseName, bool catalog, CancellationToken cancellationToken = default)
    {
        var files = DatabaseFileNames.GetFiles(DatabaseFileNames.GetDirectory(_rootPath, databaseName), GetFileSet(catalog));

        if (!File.Exists(files.Data))
        {
            throw new DatabaseException($"Storage '{GetLabel(databaseName, catalog)}' does not exist.");
        }

        var dataStream = StorageStream.FromFile(files.Data, FileMode.Open, FileShare.Read);

        StorageStream? journalStream = null;
        StorageStream? backupStream = null;
        try
        {
            journalStream = StorageStream.FromFile(files.Journal, FileMode.OpenOrCreate, FileShare.Read);
            backupStream = StorageStream.FromFile(files.Backup, FileMode.OpenOrCreate, FileShare.Read);

            // Defer the open-time checkpoint: the engine's transaction coordinator
            // analyzes the recovered journal before checkpoint truncates its records.
            return SqlStorage.Open(dataStream, journalStream, backupStream, checkpointOnOpen: false, _durability, cancellationToken);
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
    public override void DropStorage(string databaseName)
    {
        var directory = DatabaseFileNames.GetDirectory(_rootPath, databaseName);

        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <inheritdoc />
    public override bool StorageExists(string databaseName, bool catalog)
        => File.Exists(DatabaseFileNames.GetFiles(DatabaseFileNames.GetDirectory(_rootPath, databaseName), GetFileSet(catalog)).Data);
}
