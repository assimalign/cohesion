using System.IO;
using Assimalign.Cohesion.Database;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.KeyValuePair.Internal;

using Assimalign.Cohesion.Database.KeyValuePair.Storage;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Storage.Units;

/// <summary>
/// File-based storage strategy: each database is one directory under the root path, named for its
/// lower-case storage name (<see cref="DatabaseFileNames"/>), holding its data file set
/// (<c>keyvalue.dat</c>, <c>keyvalue.log</c>, <c>keyvalue.bak</c>) and its catalog file set (<c>catalog.dat</c>,
/// <c>catalog.log</c>, <c>catalog.bak</c>).
/// </summary>
/// <remarks>
/// Every member maps the name it is given through the one shared mapping, so every spelling of a
/// name reaches the same directory on a case-sensitive file system too (#1392): before it, a
/// database created as <c>sales</c> and opened as <c>SALES</c> on Linux was not found, and the
/// engine's open-or-create made a second, empty database.
/// </remarks>
[global::System.Diagnostics.DebuggerDisplay("FileSystemKeyValueStorageStrategy")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.KeyValuePair.Internal.FileSystemKeyValueStorageStrategy.DebugView))]
internal sealed class FileSystemKeyValueStorageStrategy : KeyValueStorageStrategy
{
    private readonly string _rootPath;
    private readonly StorageCommitDurability? _durability;

    internal FileSystemKeyValueStorageStrategy(string rootPath, StorageCommitDurability? durability = null)
    {
        _rootPath = rootPath;
        _durability = durability;
    }

    /// <inheritdoc />
    public override KeyValueStorage CreateStorage(string databaseName, bool catalog)
    {
        var directory = DatabaseFileNames.GetDirectory(_rootPath, databaseName);

        // The data set comes first and claims the database's directory. One already there is the
        // database, or, without the data file, what an interrupted create or drop left, which a
        // drop deletes; a create takes over neither, so a stale catalog set never meets new data.
        if (!catalog && Directory.Exists(directory))
        {
            bool exists = File.Exists(DatabaseFileNames.GetFiles(directory, GetFileSet(catalog)).Data);
            string message = exists
                ? string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.FileStorageAlreadyExists, databaseName)
                : DatabaseFileNames.GetLeftoverRefusal(databaseName, directory);
            throw DatabaseException.CreateEngineFailure(exists ? "CK03001" : "CK06004", message);
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

            return KeyValueStorage.Create(dataStream, journalStream, backupStream, GetLabel(databaseName, catalog), _durability);
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
    public override KeyValueStorage OpenStorage(string databaseName, bool catalog)
    {
        var files = DatabaseFileNames.GetFiles(DatabaseFileNames.GetDirectory(_rootPath, databaseName), GetFileSet(catalog));

        if (!File.Exists(files.Data))
        {
            throw DatabaseException.CreateEngineFailure("CK06005", string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.StorageStrategyOpenStorageDatabase, GetLabel(databaseName, catalog)));
        }

        var catalogFiles = DatabaseFileNames.GetFiles(DatabaseFileNames.GetDirectory(_rootPath, databaseName), GetFileSet(catalog: true));
        if (!File.Exists(files.Journal) || !File.Exists(files.Backup)
            || (!catalog && (!File.Exists(catalogFiles.Data) || !File.Exists(catalogFiles.Journal) || !File.Exists(catalogFiles.Backup))))
        {
            string message = string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.CreationSidecarMissing, databaseName);
            throw new DatabaseException(message, [new Diagnostic { Code = "CV06104", Message = message, Severity = DiagnosticSeverity.Error }]);
        }
        var dataStream = StorageStream.FromFile(files.Data, FileMode.Open, FileShare.Read);

        StorageStream? journalStream = null;
        StorageStream? backupStream = null;
        try
        {
            journalStream = StorageStream.FromFile(files.Journal, FileMode.Open, FileShare.Read);
            backupStream = StorageStream.FromFile(files.Backup, FileMode.Open, FileShare.Read);

            // Defer the open-time checkpoint: the engine's transaction coordinator
            // analyzes the recovered journal before checkpoint truncates its records.
            return KeyValueStorage.Open(dataStream, journalStream, backupStream, checkpointOnOpen: false, _durability);
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
            // The primary data file is the database identity; delete it before either sidecar.
            File.Delete(DatabaseFileNames.GetFiles(directory, DataFileSet).Data);
            foreach (string dataFile in Directory.EnumerateFiles(directory, "*.dat")) { File.Delete(dataFile); }
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <inheritdoc />
    public override bool StorageExists(string databaseName, bool catalog)
        => File.Exists(DatabaseFileNames.GetFiles(DatabaseFileNames.GetDirectory(_rootPath, databaseName), GetFileSet(catalog)).Data);

    // As in Documents, at most one page means creation stopped before both initial pages
    // reached disk. Inspect only the length: a refused open must not recover either set.
    internal bool HasIncompleteCreation(string databaseName)
        => new FileInfo(DatabaseFileNames.GetFiles(DatabaseFileNames.GetDirectory(_rootPath, databaseName), DataFileSet).Data).Length <= Page.Size;

    private sealed class DebugView
    {
        public DebugView(global::Assimalign.Cohesion.Database.KeyValuePair.Internal.FileSystemKeyValueStorageStrategy value)
        {
            _ = value;
        }
    }
}
