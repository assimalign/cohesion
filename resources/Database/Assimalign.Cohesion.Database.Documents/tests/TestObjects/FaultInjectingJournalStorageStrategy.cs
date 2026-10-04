using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Documents.Storage;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Storage.Tests.TestObjects;
using Assimalign.Cohesion.FileSystem;

namespace Assimalign.Cohesion.Database.Documents.Tests;

/// <summary>
/// An in-memory document storage strategy whose journal fails writes, or durable flushes, on
/// demand. Failures fire only on the asynchronous flow that armed them, so a test can fail one
/// journal append or fsync of its own call while the engine's background workers keep writing
/// normally. A database the engine closed can be reopened from the bytes its storage left behind,
/// as a file set would be.
/// </summary>
/// <remarks>
/// With <c>durable: true</c> the file sets support durable flushes (simulated over memory, as the
/// storage tests' handles do), so the engine commits synchronously and a journal fsync can fail;
/// <see cref="LoseUnconfirmedJournalOnReopen"/> then reopens a journal with only the bytes a
/// durable flush confirmed, as an operating system that dropped the writes a failed fsync covered
/// would leave it (#1243).
/// </remarks>
internal sealed class FaultInjectingJournalStorageStrategy : IDocumentStorageStrategy
{
    private static readonly AsyncLocal<Budget?> s_failures = new();
    private static readonly AsyncLocal<Budget?> s_flushFailures = new();
    private readonly Dictionary<string, Files> _databases = new(StringComparer.Ordinal);
    private readonly object _sync = new();
    private readonly bool _durable;

    /// <summary>Initializes a new instance of the <see cref="FaultInjectingJournalStorageStrategy"/> class.</summary>
    /// <param name="durable">True for file sets that support durable flushes.</param>
    public FaultInjectingJournalStorageStrategy(bool durable = false)
    {
        _durable = durable;
    }

    /// <summary>
    /// Gets or sets whether a reopen keeps only the journal bytes a durable flush confirmed.
    /// </summary>
    internal bool LoseUnconfirmedJournalOnReopen { get; set; }

    /// <summary>
    /// Gets or sets whether every journal this strategy opens keeps an append buffer of one small
    /// frame (#1252): each append then drains the frame buffered ahead of it, and a page image is
    /// written directly, so a test can fail the write an append of its own causes. A failed journal
    /// write takes the storage offline.
    /// </summary>
    internal bool SmallJournalBuffer { get; set; }

    // Applies SmallJournalBuffer to a storage this strategy created or opened.
    private void ConfigureJournal(IStorageJournal journal)
    {
        if (SmallJournalBuffer)
        {
            JournalBufferHooks.SetMaximumBufferBytes(journal, JournalBufferHooks.SmallestBuffer);
        }
    }

    /// <summary>
    /// Fails <paramref name="writes"/> journal writes made on the calling flow, after letting the
    /// next <paramref name="skip"/> writes through, until the returned scope is disposed.
    /// </summary>
    /// <param name="writes">The number of writes to fail.</param>
    /// <param name="skip">The number of writes to let through before the first failure.</param>
    /// <remarks>
    /// Since #1252 an append writes nothing: the journal's append buffer drains at a commit, a
    /// reader, the write-ahead gate, a checkpoint, a full buffer and a close, and each drain is one
    /// write. A failed one takes the storage offline. With <see cref="SmallJournalBuffer"/> each
    /// append drains the frame ahead of it, which is how a test fails an append's own write.
    /// </remarks>
    /// <returns>The scope that disarms the failure and reports how many failures remain unspent.</returns>
    internal static FailureScope FailJournalWrites(int writes, int skip = 0)
    {
        var previous = s_failures.Value;
        var budget = new Budget { Skip = skip, Fail = writes };
        s_failures.Value = budget;
        return new FailureScope(s_failures, previous, budget);
    }

    /// <summary>
    /// Fails <paramref name="flushes"/> durable journal flushes made on the calling flow, after
    /// letting the next <paramref name="skip"/> through, until the returned scope is disposed.
    /// The journal's bytes stay as they were written, as an operating system's cache keeps them.
    /// </summary>
    /// <param name="flushes">The number of durable flushes to fail.</param>
    /// <param name="skip">The number of durable flushes to let through before the first failure.</param>
    /// <returns>The scope that disarms the failure and reports how many failures remain unspent.</returns>
    internal static FailureScope FailJournalFlushes(int flushes, int skip = 0)
    {
        var previous = s_flushFailures.Value;
        var budget = new Budget { Skip = skip, Fail = flushes };
        s_flushFailures.Value = budget;
        return new FailureScope(s_flushFailures, previous, budget);
    }

    /// <summary>
    /// Gets the bytes a database's data and journal streams hold right now.
    /// </summary>
    /// <param name="databaseName">The database's name.</param>
    internal (byte[] Data, byte[] Journal) Capture(string databaseName)
    {
        lock (_sync)
        {
            var files = _databases[databaseName];
            return (files.Data.ToArray(), files.Journal.ToArray());
        }
    }

    public DocumentStorage CreateStorage(DatabaseName databaseName, StorageCommitDurability? durability)
    {
        var files = new Files(new MemoryStream(), new FaultInjectingStream(), new MemoryStream());
        lock (_sync)
        {
            _databases[databaseName.ToString()] = files;
        }

        var created = DocumentStorage.Create(DataStream(files), JournalStream(files),
            new StorageStream(files.Backup), databaseName.ToString(), durability);
        ConfigureJournal(created.WriteAheadJournal);
        return created;
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

            var journal = Copy(closed.Journal, new FaultInjectingStream());
            if (LoseUnconfirmedJournalOnReopen)
            {
                journal.SetLength(closed.Journal.ConfirmedLength);
            }

            journal.ConfirmedLength = journal.Length;
            files = new Files(Copy(closed.Data, new MemoryStream()), journal, Copy(closed.Backup, new MemoryStream()));
            _databases[databaseName.ToString()] = files;
        }

        var created = DocumentStorage.Open(DataStream(files), JournalStream(files),
            new StorageStream(files.Backup), checkpointOnOpen: false, durability);
        ConfigureJournal(created.WriteAheadJournal);
        return created;
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

    private StorageStream DataStream(Files files)
        => _durable ? new StorageStream(new DurableMemoryHandle(files.Data, null)) : new StorageStream(files.Data);

    private StorageStream JournalStream(Files files)
        => _durable ? new StorageStream(new DurableMemoryHandle(files.Journal, files.Journal)) : new StorageStream(files.Journal);

    private static T Copy<T>(MemoryStream source, T target)
        where T : MemoryStream
    {
        target.Write(source.ToArray());
        target.Position = 0;
        return target;
    }

    private static bool Spend(AsyncLocal<Budget?> failures)
    {
        if (failures.Value is not { } budget)
        {
            return false;
        }

        if (budget.Skip > 0)
        {
            budget.Skip--;
            return false;
        }

        if (budget.Fail > 0)
        {
            budget.Fail--;
            return true;
        }

        return false;
    }

    /// <summary>The armed failure budget of one calling flow.</summary>
    internal sealed class FailureScope : IDisposable
    {
        private readonly AsyncLocal<Budget?> _slot;
        private readonly Budget? _previous;
        private readonly Budget _budget;

        /// <summary>Initializes a new instance of the <see cref="FailureScope"/> class.</summary>
        /// <param name="slot">The flow-local slot the budget was armed in.</param>
        /// <param name="previous">The failure budget the scope replaced, restored on disposal.</param>
        /// <param name="budget">The failure budget the scope armed.</param>
        public FailureScope(AsyncLocal<Budget?> slot, Budget? previous, Budget budget)
        {
            _slot = slot;
            _previous = previous;
            _budget = budget;
        }

        /// <summary>Gets the number of armed failures no write or flush has spent yet.</summary>
        public int Remaining => _budget.Fail;

        public void Dispose() => _slot.Value = _previous;
    }

    /// <summary>The writes or flushes one calling flow lets through, then fails.</summary>
    internal sealed class Budget
    {
        /// <summary>Gets or sets the number still to let through.</summary>
        public int Skip { get; set; }

        /// <summary>Gets or sets the number still to fail.</summary>
        public int Fail { get; set; }
    }

    /// <summary>The data, journal and backup streams of one database.</summary>
    private sealed record Files(MemoryStream Data, FaultInjectingStream Journal, MemoryStream Backup);

    // Every write funnels through the array overload, so each journal frame is counted once: a
    // MemoryStream subclass's span overload would otherwise call back into the array overload.
    private sealed class FaultInjectingStream : MemoryStream
    {
        /// <summary>Gets or sets the length the last successful durable flush confirmed.</summary>
        public long ConfirmedLength { get; set; }

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (Spend(s_failures))
            {
                throw new IOException("Injected journal write failure.");
            }

            base.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer) => Write(buffer.ToArray(), 0, buffer.Length);

        public override void WriteByte(byte value) => Write([value], 0, 1);
    }

    /// <summary>
    /// A positional handle over a memory stream that claims durable flushes: explicit simulated
    /// durability for the engine's recovery tests, never a claim that memory survives power loss.
    /// A journal's durable flush can fail on demand, and a successful one records the length it
    /// confirmed.
    /// </summary>
    private sealed class DurableMemoryHandle : IFileSystemFileHandle
    {
        private readonly MemoryStream _stream;
        private readonly FaultInjectingStream? _journal;
        private readonly object _gate = new();

        public DurableMemoryHandle(MemoryStream stream, FaultInjectingStream? journal)
        {
            _stream = stream;
            _journal = journal;
        }

        public long Length { get { lock (_gate) { return _stream.Length; } } }

        public bool SupportsDurableFlush => true;

        public int Read(Span<byte> buffer, long offset)
        {
            lock (_gate)
            {
                _stream.Position = offset;
                return _stream.Read(buffer);
            }
        }

        public ValueTask<int> ReadAsync(Memory<byte> buffer, long offset, CancellationToken cancellationToken = default)
            => new(Read(buffer.Span, offset));

        public void Write(ReadOnlySpan<byte> buffer, long offset)
        {
            lock (_gate)
            {
                _stream.Position = offset;
                _stream.Write(buffer);
            }
        }

        public ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, long offset, CancellationToken cancellationToken = default)
        {
            Write(buffer.Span, offset);
            return default;
        }

        public void SetLength(long length)
        {
            lock (_gate)
            {
                _stream.SetLength(length);
            }
        }

        public void Flush(bool durable)
        {
            if (!durable || _journal is null)
            {
                return;
            }

            if (Spend(s_flushFailures))
            {
                throw new IOException("Injected journal fsync failure.");
            }

            lock (_gate)
            {
                _journal.ConfirmedLength = _journal.Length;
            }
        }

        public ValueTask FlushAsync(bool durable, CancellationToken cancellationToken = default)
        {
            Flush(durable);
            return default;
        }

        // The memory stream outlives the handle, so a closed database can be reopened.
        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => default;
    }
}
