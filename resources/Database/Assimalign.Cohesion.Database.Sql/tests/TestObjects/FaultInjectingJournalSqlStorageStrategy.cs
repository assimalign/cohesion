using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Sql.Tests.TestObjects;

using Assimalign.Cohesion.Database.Sql;
using Assimalign.Cohesion.Database.Sql.Storage;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Storage.Tests.TestObjects;
using Assimalign.Cohesion.FileSystem;

/// <summary>
/// An in-memory SQL storage strategy whose journals fail writes, or durable flushes, on demand.
/// Failures fire only on the asynchronous flow that armed them, so a test can fail one journal
/// append or fsync of its own call while the engine's background workers keep writing normally.
/// A file set the engine closed can be reopened from the bytes its storage left behind, as files
/// on disk would be.
/// </summary>
/// <remarks>
/// With <c>durable: true</c> the file sets support durable flushes (simulated over memory, as the
/// storage tests' handles do), so the engine commits synchronously and a journal fsync can fail;
/// <see cref="LoseUnconfirmedJournalOnReopen"/> then reopens a journal with only the bytes a
/// durable flush confirmed, as an operating system that dropped the writes a failed fsync covered
/// would leave it (#1243).
/// </remarks>
internal sealed class FaultInjectingJournalSqlStorageStrategy : ISqlStorageStrategy
{
    private static readonly AsyncLocal<Budget?> s_failures = new();
    private static readonly AsyncLocal<Budget?> s_flushFailures = new();
    private readonly Dictionary<string, Files> _storages = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DeviceFaults> _faults = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _sync = new();
    private readonly bool _durable;
    private bool _failEveryJournalWrite;

    /// <summary>Initializes a new instance of the <see cref="FaultInjectingJournalSqlStorageStrategy"/> class.</summary>
    /// <param name="durable">True for file sets that support durable flushes.</param>
    public FaultInjectingJournalSqlStorageStrategy(bool durable = false)
    {
        _durable = durable;
    }

    /// <summary>
    /// Gets or sets whether a reopen keeps only the journal bytes a durable flush confirmed.
    /// </summary>
    internal bool LoseUnconfirmedJournalOnReopen { get; set; }

    /// <summary>
    /// Gets or sets whether every journal write of this strategy's file sets fails, on every flow:
    /// the engine's background workers included, as a device that refuses writes for a while.
    /// </summary>
    internal bool FailEveryJournalWrite
    {
        get => Volatile.Read(ref _failEveryJournalWrite);
        set => Volatile.Write(ref _failEveryJournalWrite, value);
    }

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
        return new FailureScope(s_failures, previous, budget);
    }

    /// <summary>
    /// Fails <paramref name="flushes"/> durable journal flushes made on the calling flow, after
    /// letting the next <paramref name="skip"/> through, until the returned scope is disposed.
    /// The journal's bytes stay as they were written, as an operating system's cache keeps them.
    /// </summary>
    /// <param name="flushes">The number of durable flushes to fail.</param>
    /// <param name="skip">The number of durable flushes to let through before the first failure.</param>
    /// <param name="storageName">
    /// Only the journal of this file set (the database name, or with <c>.catalog</c>) fails and
    /// counts; null for every journal.
    /// </param>
    /// <returns>The scope that disarms the failure and reports how many failures remain unspent.</returns>
    internal static FailureScope FailJournalFlushes(int flushes, int skip = 0, string? storageName = null)
    {
        var previous = s_flushFailures.Value;
        var budget = new Budget { Skip = skip, Fail = flushes, StorageName = storageName };
        s_flushFailures.Value = budget;
        return new FailureScope(s_flushFailures, previous, budget);
    }

    /// <summary>
    /// Gets the bytes a file set's data and journal streams hold right now.
    /// </summary>
    /// <param name="storageName">The file set's storage name (the database name, or with <c>.catalog</c>).</param>
    internal (byte[] Data, byte[] Journal) Capture(string storageName)
    {
        lock (_sync)
        {
            var files = _storages[storageName];
            return (files.Data.ToArray(), files.Journal.ToArray());
        }
    }

    /// <inheritdoc />
    public SqlStorage CreateStorage(string databaseName)
    {
        var files = new Files(new MemoryStream(), new FaultInjectingStream(this), new MemoryStream());
        lock (_sync)
        {
            if (!_storages.TryAdd(databaseName, files))
            {
                throw new DatabaseException($"Fault-injecting storage for '{databaseName}' already exists.");
            }
        }

        return SqlStorage.Create(DataStream(files, databaseName), JournalStream(files, databaseName), new StorageStream(files.Backup), databaseName);
    }

    /// <summary>
    /// Reopens a file set from the bytes its last storage left behind. A memory stream keeps its
    /// bytes after disposal, so this works for a storage the engine closed.
    /// </summary>
    /// <inheritdoc />
    public SqlStorage OpenStorage(string databaseName)
    {
        Files files;
        lock (_sync)
        {
            if (!_storages.TryGetValue(databaseName, out var closed))
            {
                throw new DatabaseNotFoundException($"Database '{databaseName}' does not exist.");
            }

            var journal = Copy(closed.Journal, new FaultInjectingStream(this));
            if (LoseUnconfirmedJournalOnReopen)
            {
                journal.SetLength(closed.Journal.ConfirmedLength);
            }

            journal.ConfirmedLength = journal.Length;
            files = new Files(Copy(closed.Data, new MemoryStream()), journal, Copy(closed.Backup, new MemoryStream()));
            _storages[databaseName] = files;
        }

        return SqlStorage.Open(DataStream(files, databaseName), JournalStream(files, databaseName), new StorageStream(files.Backup), checkpointOnOpen: false);
    }

    /// <inheritdoc />
    public void DropStorage(string databaseName)
    {
        lock (_sync)
        {
            _storages.Remove(databaseName);
        }
    }

    /// <inheritdoc />
    public bool StorageExists(string databaseName)
    {
        lock (_sync)
        {
            return _storages.ContainsKey(databaseName);
        }
    }

    /// <summary>
    /// Gets the device faults of a file set, which fire on every thread, the engine's background
    /// workers included (#1268). They apply to the file sets of a durable strategy, and survive a
    /// reopen of the file set.
    /// </summary>
    /// <param name="storageName">The file set's storage name (the database name, or with <c>.catalog</c>).</param>
    internal DeviceFaults Faults(string storageName)
    {
        lock (_sync)
        {
            if (!_faults.TryGetValue(storageName, out var faults))
            {
                faults = new DeviceFaults();
                _faults.Add(storageName, faults);
            }

            return faults;
        }
    }

    private StorageStream DataStream(Files files, string storageName)
        => _durable ? new StorageStream(Faults(storageName).WrapData(new DurableMemoryHandle(files.Data, null, null))) : new StorageStream(files.Data);

    private StorageStream JournalStream(Files files, string storageName)
        => _durable ? new StorageStream(Faults(storageName).WrapJournal(new DurableMemoryHandle(files.Journal, files.Journal, storageName))) : new StorageStream(files.Journal);

    private static T Copy<T>(MemoryStream source, T target)
        where T : MemoryStream
    {
        target.Write(source.ToArray());
        target.Position = 0;
        return target;
    }

    private static bool Spend(AsyncLocal<Budget?> failures, string? storageName = null)
    {
        if (failures.Value is not { } budget
            || (budget.StorageName is not null && !string.Equals(budget.StorageName, storageName, StringComparison.OrdinalIgnoreCase)))
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

        /// <summary>Gets or sets the only file set whose journal the budget fails, or null for every one.</summary>
        public string? StorageName { get; set; }
    }

    /// <summary>The data, journal and backup streams of one file set.</summary>
    private sealed record Files(MemoryStream Data, FaultInjectingStream Journal, MemoryStream Backup);

    // Every write funnels through the array overload, so each journal frame is counted once: a
    // MemoryStream subclass's span overload would otherwise call back into the array overload.
    private sealed class FaultInjectingStream : MemoryStream
    {
        private readonly FaultInjectingJournalSqlStorageStrategy _owner;

        public FaultInjectingStream(FaultInjectingJournalSqlStorageStrategy owner)
        {
            _owner = owner;
        }

        /// <summary>Gets or sets the length the last successful durable flush confirmed.</summary>
        public long ConfirmedLength { get; set; }

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (_owner.FailEveryJournalWrite || Spend(s_failures))
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
        private readonly string? _storageName;
        private readonly object _gate = new();

        public DurableMemoryHandle(MemoryStream stream, FaultInjectingStream? journal, string? storageName)
        {
            _stream = stream;
            _journal = journal;
            _storageName = storageName;
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

            if (Spend(s_flushFailures, _storageName))
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

        // The memory stream outlives the handle, so a closed file set can be reopened.
        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => default;
    }
}
