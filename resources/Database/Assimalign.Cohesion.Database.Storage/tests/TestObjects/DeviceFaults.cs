using System;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Storage.Units;
using Assimalign.Cohesion.FileSystem;

namespace Assimalign.Cohesion.Database.Storage.Tests.TestObjects;

/// <summary>
/// Device faults of one file set that fire on every thread, the engine's background workers
/// included, for as long as they are switched on: a data page write fails, a write to page 0 (the
/// file header's slots) fails, or a durable flush of the journal fails. Each counts the failures it
/// injected, so a test can wait for a worker to hit the fault. Linked into each engine's test
/// project, whose fault-injecting storage strategy wraps a file set's handles with them (#1268).
/// </summary>
/// <remarks>
/// A failed write writes nothing, and a failed flush leaves the bytes written before it where they
/// are, as an operating system's cache keeps them.
/// </remarks>
internal sealed class DeviceFaults
{
    private int _failPageWrites;
    private int _failHeaderWrites;
    private int _failJournalFlushes;
    private long _pageWriteFailures;
    private long _headerWriteFailures;
    private long _journalFlushFailures;
    private long _pageWrites;

    /// <summary>
    /// Gets or sets whether every write to a data page (page 1 and beyond) of the data file fails:
    /// a page write-back, an eviction, or a checkpoint's flush of the buffer pool.
    /// </summary>
    internal bool FailPageWrites
    {
        get => Volatile.Read(ref _failPageWrites) != 0;
        set => Volatile.Write(ref _failPageWrites, value ? 1 : 0);
    }

    /// <summary>
    /// Gets or sets whether every write to page 0 of the data file fails: the header slot write of a
    /// checkpoint, an explicit flush or a close.
    /// </summary>
    internal bool FailHeaderWrites
    {
        get => Volatile.Read(ref _failHeaderWrites) != 0;
        set => Volatile.Write(ref _failHeaderWrites, value ? 1 : 0);
    }

    /// <summary>
    /// Gets or sets whether every durable flush of the journal fails.
    /// </summary>
    internal bool FailJournalFlushes
    {
        get => Volatile.Read(ref _failJournalFlushes) != 0;
        set => Volatile.Write(ref _failJournalFlushes, value ? 1 : 0);
    }

    /// <summary>Gets the number of data page writes failed so far.</summary>
    internal long PageWriteFailures => Interlocked.Read(ref _pageWriteFailures);

    /// <summary>Gets the number of header writes failed so far.</summary>
    internal long HeaderWriteFailures => Interlocked.Read(ref _headerWriteFailures);

    /// <summary>Gets the number of durable journal flushes failed so far.</summary>
    internal long JournalFlushFailures => Interlocked.Read(ref _journalFlushFailures);

    /// <summary>
    /// Gets the name of the thread the last failed durable journal flush ran on (an engine names
    /// its worker threads), or null before one failed.
    /// </summary>
    internal string? JournalFlushFailureThread => Volatile.Read(ref _journalFlushFailureThread);

    private string? _journalFlushFailureThread;

    /// <summary>
    /// Gets the number of data page writes that succeeded so far: a write-back, an eviction or a
    /// checkpoint reached the data file.
    /// </summary>
    internal long PageWrites => Interlocked.Read(ref _pageWrites);

    /// <summary>
    /// Switches every fault off.
    /// </summary>
    internal void Clear()
    {
        FailPageWrites = false;
        FailHeaderWrites = false;
        FailJournalFlushes = false;
    }

    /// <summary>
    /// Wraps a file set's data handle: its writes fail while <see cref="FailPageWrites"/> or
    /// <see cref="FailHeaderWrites"/> covers them.
    /// </summary>
    /// <param name="inner">The handle the writes reach when no fault fires.</param>
    /// <returns>The faulting handle.</returns>
    internal IFileSystemFileHandle WrapData(IFileSystemFileHandle inner) => new FaultingHandle(this, inner, journal: false);

    /// <summary>
    /// Wraps a file set's journal handle: its durable flushes fail while
    /// <see cref="FailJournalFlushes"/> is on.
    /// </summary>
    /// <param name="inner">The handle the flushes reach when no fault fires.</param>
    /// <returns>The faulting handle.</returns>
    internal IFileSystemFileHandle WrapJournal(IFileSystemFileHandle inner) => new FaultingHandle(this, inner, journal: true);

    private void BeforeWrite(long offset)
    {
        if (offset < Page.Size)
        {
            if (FailHeaderWrites)
            {
                Interlocked.Increment(ref _headerWriteFailures);
                throw new System.IO.IOException("Injected header write failure.");
            }
        }
        else if (FailPageWrites)
        {
            Interlocked.Increment(ref _pageWriteFailures);
            throw new System.IO.IOException("Injected page write failure.");
        }
    }

    private void AfterWrite(long offset)
    {
        if (offset >= Page.Size)
        {
            Interlocked.Increment(ref _pageWrites);
        }
    }

    private void BeforeDurableFlush()
    {
        if (FailJournalFlushes)
        {
            Volatile.Write(ref _journalFlushFailureThread, Thread.CurrentThread.Name ?? $"unnamed thread {Environment.CurrentManagedThreadId}");
            Interlocked.Increment(ref _journalFlushFailures);
            throw new System.IO.IOException("Injected journal fsync failure.");
        }
    }

    private sealed class FaultingHandle : IFileSystemFileHandle
    {
        private readonly DeviceFaults _faults;
        private readonly IFileSystemFileHandle _inner;
        private readonly bool _journal;

        public FaultingHandle(DeviceFaults faults, IFileSystemFileHandle inner, bool journal)
        {
            _faults = faults;
            _inner = inner;
            _journal = journal;
        }

        public long Length => _inner.Length;

        public bool SupportsDurableFlush => _inner.SupportsDurableFlush;

        public int Read(Span<byte> buffer, long offset) => _inner.Read(buffer, offset);

        public ValueTask<int> ReadAsync(Memory<byte> buffer, long offset, CancellationToken cancellationToken = default)
            => _inner.ReadAsync(buffer, offset, cancellationToken);

        public void Write(ReadOnlySpan<byte> buffer, long offset)
        {
            if (_journal)
            {
                _inner.Write(buffer, offset);
                return;
            }

            _faults.BeforeWrite(offset);
            _inner.Write(buffer, offset);
            _faults.AfterWrite(offset);
        }

        public async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, long offset, CancellationToken cancellationToken = default)
        {
            if (_journal)
            {
                await _inner.WriteAsync(buffer, offset, cancellationToken).ConfigureAwait(false);
                return;
            }

            _faults.BeforeWrite(offset);
            await _inner.WriteAsync(buffer, offset, cancellationToken).ConfigureAwait(false);
            _faults.AfterWrite(offset);
        }

        public void SetLength(long length) => _inner.SetLength(length);

        public void Flush(bool durable)
        {
            if (durable && _journal)
            {
                _faults.BeforeDurableFlush();
            }

            _inner.Flush(durable);
        }

        public ValueTask FlushAsync(bool durable, CancellationToken cancellationToken = default)
        {
            if (durable && _journal)
            {
                _faults.BeforeDurableFlush();
            }

            return _inner.FlushAsync(durable, cancellationToken);
        }

        public void Dispose() => _inner.Dispose();

        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }
}
