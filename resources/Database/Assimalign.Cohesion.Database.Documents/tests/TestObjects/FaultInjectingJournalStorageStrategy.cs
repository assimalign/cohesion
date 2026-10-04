using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

using Assimalign.Cohesion.Database.Documents.Storage;
using Assimalign.Cohesion.Database.Storage;

namespace Assimalign.Cohesion.Database.Documents.Tests;

/// <summary>
/// An in-memory document storage strategy whose journal fails writes on demand. Writes fail only
/// on the asynchronous flow that armed the failure, so a test can fail one journal append of its
/// own call while the engine's background workers keep writing normally. A database the engine
/// closed can be reopened from the bytes its storage left behind, as a file set would be.
/// </summary>
internal sealed class FaultInjectingJournalStorageStrategy : IDocumentStorageStrategy
{
    private static readonly AsyncLocal<Budget?> s_failures = new();
    private readonly Dictionary<string, Files> _databases = new(StringComparer.Ordinal);
    private readonly object _sync = new();

    /// <summary>
    /// Fails <paramref name="writes"/> journal writes made on the calling flow, after letting the
    /// next <paramref name="skip"/> writes through, until the returned scope is disposed.
    /// </summary>
    /// <param name="writes">The number of writes to fail.</param>
    /// <param name="skip">The number of writes to let through before the first failure.</param>
    /// <returns>The scope that disarms the failure and reports how many failures remain unspent.</returns>
    internal static FailureScope FailJournalWrites(int writes, int skip = 0)
    {
        var previous = s_failures.Value;
        var budget = new Budget { Skip = skip, Fail = writes };
        s_failures.Value = budget;
        return new FailureScope(previous, budget);
    }

    public DocumentStorage CreateStorage(DatabaseName databaseName, StorageCommitDurability? durability)
    {
        var files = new Files(new MemoryStream(), new FaultInjectingStream(), new MemoryStream());
        lock (_sync)
        {
            _databases[databaseName.ToString()] = files;
        }

        return DocumentStorage.Create(new StorageStream(files.Data), new StorageStream(files.Journal),
            new StorageStream(files.Backup), databaseName.ToString(), durability);
    }

    /// <summary>
    /// Reopens a database from the bytes its last storage left behind. A memory stream keeps its
    /// bytes after disposal, so this works for a storage the engine closed.
    /// </summary>
    public DocumentStorage OpenStorage(DatabaseName databaseName, StorageCommitDurability? durability)
    {
        Files files;
        lock (_sync)
        {
            if (!_databases.TryGetValue(databaseName.ToString(), out var closed))
            {
                throw new DatabaseNotFoundException($"Database '{databaseName}' does not exist.");
            }

            files = new Files(Copy(closed.Data, new MemoryStream()), Copy(closed.Journal, new FaultInjectingStream()), Copy(closed.Backup, new MemoryStream()));
            _databases[databaseName.ToString()] = files;
        }

        return DocumentStorage.Open(new StorageStream(files.Data), new StorageStream(files.Journal),
            new StorageStream(files.Backup), checkpointOnOpen: false, durability);
    }

    public void DropStorage(DatabaseName databaseName)
    {
        lock (_sync)
        {
            _databases.Remove(databaseName.ToString());
        }
    }

    public bool StorageExists(DatabaseName databaseName)
    {
        lock (_sync)
        {
            return _databases.ContainsKey(databaseName.ToString());
        }
    }

    public IEnumerable<DatabaseName> GetDatabaseNames()
    {
        string[] names;
        lock (_sync)
        {
            names = [.. _databases.Keys];
        }

        foreach (string name in names)
        {
            yield return new DatabaseName(name);
        }
    }

    private static T Copy<T>(MemoryStream source, T target)
        where T : MemoryStream
    {
        target.Write(source.ToArray());
        target.Position = 0;
        return target;
    }

    /// <summary>The armed failure budget of one calling flow.</summary>
    internal sealed class FailureScope : IDisposable
    {
        private readonly Budget? _previous;
        private readonly Budget _budget;

        /// <summary>Initializes a new instance of the <see cref="FailureScope"/> class.</summary>
        /// <param name="previous">The failure budget the scope replaced, restored on disposal.</param>
        /// <param name="budget">The failure budget the scope armed.</param>
        public FailureScope(Budget? previous, Budget budget)
        {
            _previous = previous;
            _budget = budget;
        }

        /// <summary>Gets the number of armed failures no write has spent yet.</summary>
        public int Remaining => _budget.Fail;

        public void Dispose() => s_failures.Value = _previous;
    }

    /// <summary>The writes one calling flow lets through, then fails.</summary>
    internal sealed class Budget
    {
        /// <summary>Gets or sets the number of writes still to let through.</summary>
        public int Skip { get; set; }

        /// <summary>Gets or sets the number of writes still to fail.</summary>
        public int Fail { get; set; }
    }

    /// <summary>The data, journal and backup streams of one database.</summary>
    private sealed record Files(MemoryStream Data, FaultInjectingStream Journal, MemoryStream Backup);

    // Every write funnels through the array overload, so each journal frame is counted once: a
    // MemoryStream subclass's span overload would otherwise call back into the array overload.
    private sealed class FaultInjectingStream : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count)
        {
            ThrowIfArmed();
            base.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer) => Write(buffer.ToArray(), 0, buffer.Length);

        public override void WriteByte(byte value)
        {
            ThrowIfArmed();
            base.WriteByte(value);
        }

        private static void ThrowIfArmed()
        {
            if (s_failures.Value is not { } budget)
            {
                return;
            }

            if (budget.Skip > 0)
            {
                budget.Skip--;
                return;
            }

            if (budget.Fail > 0)
            {
                budget.Fail--;
                throw new IOException("Injected journal write failure.");
            }
        }
    }
}
