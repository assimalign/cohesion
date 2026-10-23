using System;
using System.IO;
using System.Text;
using System.Threading;

using Assimalign.Cohesion.Database;
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
[global::System.Diagnostics.DebuggerDisplay("FileSystemSqlStorageStrategy")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Sql.Internal.FileSystemSqlStorageStrategy.DebugView))]
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
        => CreateStorage(databaseName, catalog, restarting: false);

    private SqlStorage CreateStorage(string databaseName, bool catalog, bool restarting)
    {
        var directory = DatabaseFileNames.GetDirectory(_rootPath, databaseName);

        // The data set comes first and claims the database's directory. One already there is the
        // database, or, without the data file, what an interrupted create or drop left, which a
        // drop deletes; a create takes over neither, so a stale catalog set never meets new data.
        if (!catalog && !restarting && Directory.Exists(directory))
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

            if (!catalog) { ClaimCreation(databaseName); }
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
            throw DatabaseException.CreateEngineFailure("CK06005", string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.SqlStorageStrategyOpenStorageDatabase, GetLabel(databaseName, catalog)));
        }

        if (!File.Exists(files.Journal) || !File.Exists(files.Backup))
        {
            throw new SqlDataStorageFormatException(string.Format(System.Globalization.CultureInfo.CurrentCulture,
                ErrorMessages.CreationSidecarMissing, databaseName), "CS03401");
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
            // The primary data file is the database identity; delete it before either sidecar.
            File.Delete(DatabaseFileNames.GetFiles(directory, DataFileSet).Data);
            foreach (string dataFile in Directory.EnumerateFiles(directory, "*.dat")) { File.Delete(dataFile); }
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <inheritdoc />
    public override bool StorageExists(string databaseName, bool catalog)
        => File.Exists(DatabaseFileNames.GetFiles(DatabaseFileNames.GetDirectory(_rootPath, databaseName), GetFileSet(catalog)).Data);
    internal override SqlStorageProbe? ProbeStorage(string databaseName, bool catalog)
    {
        var files = DatabaseFileNames.GetFiles(DatabaseFileNames.GetDirectory(_rootPath, databaseName), GetFileSet(catalog));
        long journalLength = File.Exists(files.Journal) ? new FileInfo(files.Journal).Length : 0;
        long backupLength = File.Exists(files.Backup) ? new FileInfo(files.Backup).Length : 0;
        if (!File.Exists(files.Data))
        {
            if (!File.Exists(files.Journal) && !File.Exists(files.Backup)) { return null; }
            return new SqlStorageProbe(-1, [], [], []) { JournalLength = journalLength, BackupLength = backupLength };
        }
        using var data = new FileStream(files.Data, FileMode.Open, FileAccess.Read, FileShare.Read);
        bool hasSidecars = File.Exists(files.Journal) && File.Exists(files.Backup);
        byte[] bytes = new byte[(int)Math.Min(data.Length, 2L * Assimalign.Cohesion.Database.Storage.Units.Page.Size)];
        data.ReadExactly(bytes);
        return new SqlStorageProbe(data.Length, bytes, [], [], hasSidecars)
        {
            JournalLength = journalLength,
            BackupLength = backupLength,
            // All file sizes use overlays. Recovery changes only private blocks; the
            // catalog format gate cannot redo or truncate the real files.
            OpenReadOnly = () => OpenProbe(files.Data, files.Journal, files.Backup),
        };
    }

    private static SqlStorage OpenProbe(string dataPath, string journalPath, string backupPath)
    {
        var copy = new SqlProbeStream(dataPath);
        Stream? journal = null;
        Stream? backup = null;
        try
        {
            journal = new SqlProbeStream(journalPath);
            backup = new SqlProbeStream(backupPath);
            return SqlStorage.Open(copy, journal, backup, checkpointOnOpen: false);
        }
        catch
        {
            copy.Dispose();
            journal?.Dispose();
            backup?.Dispose();
            throw;
        }
    }

    internal override SqlStorage RestartStorage(string databaseName, bool catalog)
    {
        var files = DatabaseFileNames.GetFiles(DatabaseFileNames.GetDirectory(_rootPath, databaseName), GetFileSet(catalog));
        // The engine has proved this set contains no header or committed content.
        if (!catalog) { File.Delete(CreationPath(databaseName)); }
        File.Delete(files.Data);
        File.Delete(files.Journal);
        File.Delete(files.Backup);
        return CreateStorage(databaseName, catalog, restarting: true);
    }
    internal override bool IsCreationPending(string databaseName)
    {
        string path = CreationPath(databaseName);
        return File.Exists(path) && new FileInfo(path).Length == CreationSignature.Length
            && File.ReadAllText(path, Encoding.UTF8) == CreationSignature;
    }

    private void ClaimCreation(string databaseName)
    {
        if (IsCreationPending(databaseName)) { return; }
        using var claim = new FileStream(CreationPath(databaseName), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        claim.Write(Encoding.UTF8.GetBytes(CreationSignature));
        claim.Flush(flushToDisk: true);
    }

    internal override void CompleteCreation(string databaseName) => File.Delete(CreationPath(databaseName));

    private string CreationPath(string databaseName)
        => Path.Combine(DatabaseFileNames.GetDirectory(_rootPath, databaseName), CreationFileName);

    private sealed class DebugView
    {
        public DebugView(global::Assimalign.Cohesion.Database.Sql.Internal.FileSystemSqlStorageStrategy value)
        {
            _ = value;
        }
    }
}
