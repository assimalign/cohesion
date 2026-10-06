using System;
using System.Collections.Generic;
using System.IO;

using Assimalign.Cohesion.Database.Storage;

// Deviates from namespace-matches-assembly: this model-local helper is compiled
// from the Database root's shared source into each model assembly.
namespace Assimalign.Cohesion.Database;

/// <summary>
/// The files of an engine's in-memory databases, kept for the engine's lifetime: each file set's
/// data, journal and backup streams, by storage name. A closed in-memory database reopens with
/// its data, as a database on disk reopens from its files (#1272; owner decision 33 of
/// 2026-10-06, #1289): the open runs the same recovery over the bytes its last storage left.
/// </summary>
/// <remarks>
/// <para>
/// A storage disposes its streams when it closes, and a <see cref="MemoryStream"/> keeps its bytes
/// after that (<see cref="MemoryStream.ToArray"/> reads a closed stream). An open copies them into
/// new streams, so the reopened storage never shares a buffer with the one that closed, and a
/// write the closed storage could still attempt fails on its own disposed stream rather than
/// changing the reopened database. Before this type, an in-memory reopen got fresh empty streams
/// and silently lost every row (#1272).
/// </para>
/// <para>
/// An engine reopens a database only after its close ended (the root engine base waits for a
/// holder's close, and an engine's own disposal of a database waits for one in flight), so the
/// open refuses a file set whose last storage still has a stream open: that is a defect of the
/// caller, reported instead of copying bytes a running storage still writes.
/// </para>
/// <para>
/// Compiled into each model assembly from the root's <c>shared</c> folder, because each model
/// engine owns its in-memory files; its instances never leave the model assembly.
/// </para>
/// </remarks>
internal sealed class DatabaseMemoryFiles
{
    private readonly Dictionary<string, Files> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _sync = new();

    /// <summary>
    /// Gets the names of the file sets that exist, in no particular order: a point-in-time
    /// snapshot.
    /// </summary>
    internal string[] Names
    {
        get
        {
            lock (_sync)
            {
                return [.. _files.Keys];
            }
        }
    }

    /// <summary>
    /// Reports whether a file set of this name exists: created and not dropped.
    /// </summary>
    /// <param name="name">The storage name.</param>
    /// <returns>True when the file set exists.</returns>
    internal bool Exists(string name)
    {
        lock (_sync)
        {
            return _files.ContainsKey(name);
        }
    }

    /// <summary>
    /// Creates the empty files of a new file set.
    /// </summary>
    /// <param name="name">The storage name.</param>
    /// <param name="data">The data file.</param>
    /// <param name="journal">The journal file.</param>
    /// <param name="backup">The backup file.</param>
    /// <returns>False when a file set of this name already exists; nothing was created.</returns>
    internal bool TryCreate(string name, out StorageStream data, out StorageStream journal, out StorageStream backup)
    {
        var files = new Files(new MemoryStream(), new MemoryStream(), new MemoryStream());
        lock (_sync)
        {
            if (!_files.TryAdd(name, files))
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
    /// <param name="name">The storage name.</param>
    /// <param name="data">The data file.</param>
    /// <param name="journal">The journal file.</param>
    /// <param name="backup">The backup file.</param>
    /// <returns>False when no file set of this name exists; nothing was opened.</returns>
    /// <exception cref="InvalidOperationException">The file set's last storage has not closed.</exception>
    internal bool TryOpen(string name, out StorageStream data, out StorageStream journal, out StorageStream backup)
    {
        Files reopened;
        lock (_sync)
        {
            if (!_files.TryGetValue(name, out var closed))
            {
                data = journal = backup = null!;
                return false;
            }

            if (closed.Data.CanRead || closed.Journal.CanRead || closed.Backup.CanRead)
            {
                throw new InvalidOperationException(
                    $"The in-memory file set '{name}' is still open: its database's storage has not closed, so it cannot be opened again.");
            }

            reopened = new Files(Copy(closed.Data), Copy(closed.Journal), Copy(closed.Backup));
            _files[name] = reopened;
        }

        (data, journal, backup) = reopened.Open();
        return true;
    }

    /// <summary>
    /// Drops a file set: its bytes are released, and a later create of the name starts empty.
    /// </summary>
    /// <param name="name">The storage name.</param>
    /// <returns>True when a file set of this name existed.</returns>
    internal bool Drop(string name)
    {
        lock (_sync)
        {
            return _files.Remove(name);
        }
    }

    // A closed stream's bytes in a new, growable stream positioned at its start.
    private static MemoryStream Copy(MemoryStream closed)
    {
        var copy = new MemoryStream();
        copy.Write(closed.ToArray());
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
