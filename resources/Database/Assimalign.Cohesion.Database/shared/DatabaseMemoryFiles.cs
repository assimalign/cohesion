using System;
using System.Collections.Generic;
using System.IO;

using Assimalign.Cohesion.Database.Storage;

// Deviates from namespace-matches-assembly: this model-local helper is compiled
// from the Database root's shared source into each model assembly.
namespace Assimalign.Cohesion.Database;

/// <summary>
/// The files of an engine's in-memory databases, kept for the engine's lifetime: each file set's
/// data, journal and backup streams, by the database's storage name and the set's name. A closed
/// in-memory database reopens with its data, as a database on disk reopens from its files (#1272;
/// owner decision 33 of 2026-10-06, #1289): the open runs the same recovery over the bytes its last
/// storage left.
/// </summary>
/// <remarks>
/// <para>
/// A database's file sets are keyed as a database directory holds them on disk
/// (<see cref="DatabaseFileNames"/>, #1392): by its storage name, the lower case of its name, so
/// every spelling of the name reaches the same files, and by the set's name inside it, so no file
/// set of one database can be another database. Until #1392 a catalog set was keyed
/// <c>{name}.catalog</c>, which a database named <c>sales.catalog</c> shared with the catalog of
/// <c>sales</c>.
/// </para>
/// <para>
/// A storage disposes its streams when it closes, and a <see cref="MemoryStream"/> keeps its bytes
/// after that (<see cref="MemoryStream.TryGetBuffer"/> and <see cref="MemoryStream.ToArray"/> read
/// a closed stream). An open copies them into new streams, outside the lock, so the reopened
/// storage never shares a buffer with the one that closed, and a write the closed storage could
/// still attempt fails on its own disposed stream rather than changing the reopened database.
/// Before this type, an in-memory reopen got fresh empty streams and silently lost every row
/// (#1272).
/// </para>
/// <para>
/// An engine reopens a database only after its close ended (the root engine base waits for a
/// holder's close, and an engine's own disposal of a database waits for one in flight), so the
/// open refuses a file set whose last storage still has a stream open: that is a defect of the
/// caller, reported instead of copying bytes a running storage still writes.
/// </para>
/// <para>
/// The bytes live until the database is dropped (<see cref="Drop"/>) or the owning engine is disposed
/// (<see cref="Clear"/>): an engine's disposal releases them, so a disposed engine something
/// still references holds none of its databases.
/// </para>
/// <para>
/// Compiled into each model assembly from the root's <c>shared</c> folder, because each model
/// engine owns its in-memory files; its instances never leave the model assembly.
/// </para>
/// </remarks>
internal sealed class DatabaseMemoryFiles
{
    // By the database's storage name and the set's name, both compared ordinally: the storage name
    // is already the one lower-case spelling of every name that reaches it.
    private readonly Dictionary<(string Database, string FileSet), Files> _files = [];
    private readonly object _sync = new();

    /// <summary>
    /// Gets the storage names of the databases that have a file set, each once, in no particular
    /// order: a point-in-time snapshot.
    /// </summary>
    internal string[] Names
    {
        get
        {
            lock (_sync)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var key in _files.Keys)
                {
                    names.Add(key.Database);
                }

                return [.. names];
            }
        }
    }

    /// <summary>
    /// Reports whether a database's file set exists: created and not dropped.
    /// </summary>
    /// <param name="name">The database name, in any spelling.</param>
    /// <param name="fileSet">The file set's name.</param>
    /// <returns>True when the file set exists.</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> breaks the database-name rule.</exception>
    internal bool Exists(string name, string fileSet)
    {
        var key = Key(name, fileSet);
        lock (_sync)
        {
            return _files.ContainsKey(key);
        }
    }

    /// <summary>
    /// Creates the empty files of a new file set.
    /// </summary>
    /// <param name="name">The database name, in any spelling.</param>
    /// <param name="fileSet">The file set's name.</param>
    /// <param name="data">The data file.</param>
    /// <param name="journal">The journal file.</param>
    /// <param name="backup">The backup file.</param>
    /// <returns>False when the database already has this file set; nothing was created.</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> breaks the database-name rule.</exception>
    internal bool TryCreate(string name, string fileSet, out StorageStream data, out StorageStream journal, out StorageStream backup)
    {
        var key = Key(name, fileSet);
        var files = new Files(new MemoryStream(), new MemoryStream(), new MemoryStream());
        lock (_sync)
        {
            if (!_files.TryAdd(key, files))
            {
                data = journal = backup = null!;
                return false;
            }
        }

        (data, journal, backup) = files.Open();
        return true;
    }

    /// <summary>
    /// Opens an existing file set again: new files holding the bytes its last storage left.
    /// </summary>
    /// <param name="name">The database name, in any spelling.</param>
    /// <param name="fileSet">The file set's name.</param>
    /// <param name="data">The data file.</param>
    /// <param name="journal">The journal file.</param>
    /// <param name="backup">The backup file.</param>
    /// <returns>False when the database has no such file set; nothing was opened.</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> breaks the database-name rule.</exception>
    /// <exception cref="InvalidOperationException">The file set's last storage has not closed.</exception>
    internal bool TryOpen(string name, string fileSet, out StorageStream data, out StorageStream journal, out StorageStream backup)
    {
        var key = Key(name, fileSet);
        Files? closed;
        lock (_sync)
        {
            if (!_files.TryGetValue(key, out closed))
            {
                data = journal = backup = null!;
                return false;
            }

            ThrowIfOpen(key, closed);
        }

        // Copied outside the lock, so the copy of a large database never blocks a lookup or a
        // create of another name. The closed streams cannot change any more, and the copy is
        // published only if no other open, and no drop and create, replaced them meanwhile; the
        // engines serialize their opens, so that is a defect of the caller, reported.
        var reopened = new Files(Copy(closed.Data), Copy(closed.Journal), Copy(closed.Backup));
        lock (_sync)
        {
            if (!_files.TryGetValue(key, out var current))
            {
                data = journal = backup = null!;
                return false;
            }

            if (!ReferenceEquals(current, closed))
            {
                throw new InvalidOperationException(
                    $"The in-memory file set '{Describe(key)}' was opened or created again while an open copied it.");
            }

            _files[key] = reopened;
        }

        (data, journal, backup) = reopened.Open();
        return true;
    }

    /// <summary>
    /// Releases every file set's bytes: the engine that owns the files was disposed, and nothing
    /// opens them again.
    /// </summary>
    internal void Clear()
    {
        lock (_sync)
        {
            _files.Clear();
        }
    }

    /// <summary>
    /// Drops a database: the bytes of every file set it has are released, as dropping a database
    /// on disk deletes its directory, and a later create of the name starts empty.
    /// </summary>
    /// <param name="name">The database name, in any spelling.</param>
    /// <returns>True when the database had a file set.</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> breaks the database-name rule.</exception>
    internal bool Drop(string name)
    {
        string database = DatabaseFileNames.GetStorageName(name);
        lock (_sync)
        {
            List<(string Database, string FileSet)>? sets = null;
            foreach (var key in _files.Keys)
            {
                if (string.Equals(key.Database, database, StringComparison.Ordinal))
                {
                    (sets ??= []).Add(key);
                }
            }

            foreach (var key in sets ?? [])
            {
                _files.Remove(key);
            }

            return sets is not null;
        }
    }

    // A file set's key: the database's storage name, so every spelling reaches the same files, and
    // the set's name.
    private static (string Database, string FileSet) Key(string name, string fileSet)
    {
        ArgumentException.ThrowIfNullOrEmpty(fileSet);
        return (DatabaseFileNames.GetStorageName(name), fileSet);
    }

    // A file set as messages name it: its database's storage name and the set, as its path in the
    // database's directory reads.
    private static string Describe((string Database, string FileSet) key) => key.Database + "/" + key.FileSet;

    // Refuses a file set whose last storage still has a stream open.
    private static void ThrowIfOpen((string Database, string FileSet) key, Files files)
    {
        if (files.Data.CanRead || files.Journal.CanRead || files.Backup.CanRead)
        {
            throw new InvalidOperationException(
                $"The in-memory file set '{Describe(key)}' is still open: its database's storage has not closed, so it cannot be opened again.");
        }
    }

    // A closed stream's bytes in a new, growable stream positioned at its start. A closed
    // MemoryStream still exposes its buffer, so each copy allocates once, at its final size.
    private static MemoryStream Copy(MemoryStream closed)
    {
        ReadOnlySpan<byte> bytes = closed.TryGetBuffer(out var buffer) ? buffer.AsSpan() : closed.ToArray();
        var copy = new MemoryStream(bytes.Length);
        copy.Write(bytes);
        copy.Position = 0;
        return copy;
    }

    // The three streams of one file set. The storage streams own them, and close them with the storage.
    private sealed record Files(MemoryStream Data, MemoryStream Journal, MemoryStream Backup)
    {
        public (StorageStream Data, StorageStream Journal, StorageStream Backup) Open()
            => (new StorageStream(Data), new StorageStream(Journal), new StorageStream(Backup));
    }
}
