using System;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Storage.Units;
using Assimalign.Cohesion.FileSystem;

namespace Assimalign.Cohesion.Database.Storage.Tests.TestObjects;

/// <summary>
/// The faults a test switches on as one theory's cases (<see cref="DeviceFaults.SwitchOn"/>): each
/// takes the database offline.
/// </summary>
public enum DeviceFault
{
    /// <summary>A write to page 0, the file header's slots, fails (#1268).</summary>
    HeaderWrite,

    /// <summary>A durable flush of the journal fails (#1243).</summary>
    JournalFlush,

    /// <summary>A write of the journal, the drain of its append buffer, fails (#1252).</summary>
    JournalWrite,
}

/// <summary>
/// Device faults of one file set that fire on every thread, the engine's background workers
/// included, for as long as they are switched on: a data page write fails, a write to page 0 (the
/// file header's slots) fails, a write of the journal (since #1252 the drain of its append buffer)
/// fails, or a durable flush of the journal fails. Each counts the failures it injected, so a test
/// can wait for a worker to hit the fault. A durable flush of the data file can also stall until
/// the test releases it, as a device that does not answer an fsync leaves it, and one data page can
/// rot, reading back with a byte flipped (#1342). Linked into each engine's test project, whose
/// fault-injecting storage strategy wraps a file set's handles with them (#1268).
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
    private int _failJournalWrites;
    private long _pageWriteFailures;
    private long _headerWriteFailures;
    private long _journalFlushFailures;
    private long _journalWriteFailures;
    private long _pageWrites;

    // Open while durable flushes of the data file go through; closed while they stall.
    private readonly ManualResetEventSlim _dataFlushGate = new(initialState: true);
    private int _stalledDataFlushes;

    // The data page whose reads come back rotten, or -1 for none.
    private long _rottenPage = -1;
    private long _rottenPageReads;

    /// <summary>
    /// Gets or sets the data page every read of which returns its first body byte flipped, as a
    /// sector that decayed after the page was written, or null for none. The stored bytes are not
    /// changed, so clearing the fault heals the page; and a page the buffer pool holds is not read,
    /// so a test evicts the page before it expects the fault to fire (#1342).
    /// </summary>
    internal PageId? RottenPage
    {
        get
        {
            long page = Interlocked.Read(ref _rottenPage);
            return page < 0 ? null : (PageId)page;
        }

        set => Interlocked.Exchange(ref _rottenPage, value is { } page ? (long)page : -1L);
    }

    /// <summary>Gets the number of reads that returned the rotten page with its byte flipped.</summary>
    internal long RottenPageReads => Interlocked.Read(ref _rottenPageReads);

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

    /// <summary>
    /// Gets or sets whether every write of the journal fails: since #1252 a write is a drain of the
    /// append buffer (at a commit, a group flush, a checkpoint, the write-ahead gate), and a failed
    /// one takes the storage offline.
    /// </summary>
    internal bool FailJournalWrites
    {
        get => Volatile.Read(ref _failJournalWrites) != 0;
        set => Volatile.Write(ref _failJournalWrites, value ? 1 : 0);
    }

    /// <summary>Gets the number of data page writes failed so far.</summary>
    internal long PageWriteFailures => Interlocked.Read(ref _pageWriteFailures);

    /// <summary>Gets the number of journal writes failed so far.</summary>
    internal long JournalWriteFailures => Interlocked.Read(ref _journalWriteFailures);

    /// <summary>
    /// Gets the name of the thread the last failed journal write ran on (an engine names its worker
    /// threads), or null before one failed.
    /// </summary>
    internal string? JournalWriteFailureThread => Volatile.Read(ref _journalWriteFailureThread);

    private string? _journalWriteFailureThread;

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
    /// Gets the number of writes to page 0 that succeeded so far: each checkpoint writes the file
    /// header once, so a test counts a file set's checkpoints with it.
    /// </summary>
    internal long HeaderWrites => Interlocked.Read(ref _headerWrites);

    private long _headerWrites;

    /// <summary>
    /// Gets the number of durable flushes of the data file the stall holds right now
    /// (<see cref="StallDataFlushes"/>).
    /// </summary>
    internal int StalledDataFlushes => Volatile.Read(ref _stalledDataFlushes);

    /// <summary>
    /// Stalls every durable flush of the data file — the fsync a checkpoint makes after its page
    /// writes and again after its header slot write — until <see cref="ReleaseDataFlushes"/> or
    /// <see cref="Clear"/> is called: a device that does not answer an fsync, as a failing disk or
    /// an unreachable network volume leaves it. A stalled flush then completes normally.
    /// </summary>
    internal void StallDataFlushes() => _dataFlushGate.Reset();

    /// <summary>
    /// Lets the durable flushes of the data file through again, stalled ones included.
    /// </summary>
    internal void ReleaseDataFlushes() => _dataFlushGate.Set();

    /// <summary>
    /// Switches every fault off, and releases stalled flushes.
    /// </summary>
    internal void Clear()
    {
        FailPageWrites = false;
        FailHeaderWrites = false;
        FailJournalFlushes = false;
        FailJournalWrites = false;
        RottenPage = null;
        ReleaseDataFlushes();
    }

    /// <summary>
    /// Switches one fault on.
    /// </summary>
    /// <param name="fault">The fault.</param>
    internal void SwitchOn(DeviceFault fault)
    {
        switch (fault)
        {
            case DeviceFault.HeaderWrite:
                FailHeaderWrites = true;
                break;
            case DeviceFault.JournalFlush:
                FailJournalFlushes = true;
                break;
            case DeviceFault.JournalWrite:
                FailJournalWrites = true;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(fault), fault, null);
        }
    }

    /// <summary>
    /// Wraps a file set's data handle: its writes fail while <see cref="FailPageWrites"/> or
    /// <see cref="FailHeaderWrites"/> covers them.
    /// </summary>
    /// <param name="inner">The handle the writes reach when no fault fires.</param>
    /// <returns>The faulting handle.</returns>
    internal IFileSystemFileHandle WrapData(IFileSystemFileHandle inner) => new FaultingHandle(this, inner, journal: false);

    /// <summary>
    /// Wraps a file set's journal handle: its writes fail while <see cref="FailJournalWrites"/> is
    /// on, and its durable flushes while <see cref="FailJournalFlushes"/> is.
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

    private void AfterDataRead(Span<byte> read, long offset)
    {
        long page = Interlocked.Read(ref _rottenPage);
        if (page < 0)
        {
            return;
        }

        long target = (page * Page.Size) + Page.HeaderSize;
        if (target >= offset && target < offset + read.Length)
        {
            read[(int)(target - offset)] ^= 0x5A;
            Interlocked.Increment(ref _rottenPageReads);
        }
    }

    private void AfterWrite(long offset)
    {
        if (offset >= Page.Size)
        {
            Interlocked.Increment(ref _pageWrites);
        }
        else
        {
            Interlocked.Increment(ref _headerWrites);
        }
    }

    private void BeforeJournalWrite()
    {
        if (FailJournalWrites)
        {
            Volatile.Write(ref _journalWriteFailureThread, Thread.CurrentThread.Name ?? $"unnamed thread {Environment.CurrentManagedThreadId}");
            Interlocked.Increment(ref _journalWriteFailures);
            throw new System.IO.IOException("Injected journal write failure.");
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

    private void BeforeDurableDataFlush()
    {
        if (_dataFlushGate.IsSet)
        {
            return;
        }

        Interlocked.Increment(ref _stalledDataFlushes);
        try
        {
            _dataFlushGate.Wait();
        }
        finally
        {
            Interlocked.Decrement(ref _stalledDataFlushes);
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

        public int Read(Span<byte> buffer, long offset)
        {
            int read = _inner.Read(buffer, offset);
            if (!_journal)
            {
                _faults.AfterDataRead(buffer[..read], offset);
            }

            return read;
        }

        public async ValueTask<int> ReadAsync(Memory<byte> buffer, long offset, CancellationToken cancellationToken = default)
        {
            int read = await _inner.ReadAsync(buffer, offset, cancellationToken).ConfigureAwait(false);
            if (!_journal)
            {
                _faults.AfterDataRead(buffer.Span[..read], offset);
            }

            return read;
        }

        public void Write(ReadOnlySpan<byte> buffer, long offset)
        {
            if (_journal)
            {
                _faults.BeforeJournalWrite();
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
                _faults.BeforeJournalWrite();
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
            BeforeFlush(durable);
            _inner.Flush(durable);
        }

        public ValueTask FlushAsync(bool durable, CancellationToken cancellationToken = default)
        {
            BeforeFlush(durable);
            return _inner.FlushAsync(durable, cancellationToken);
        }

        private void BeforeFlush(bool durable)
        {
            if (!durable)
            {
                return;
            }

            if (_journal)
            {
                _faults.BeforeDurableFlush();
            }
            else
            {
                _faults.BeforeDurableDataFlush();
            }
        }

        public void Dispose() => _inner.Dispose();

        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }
}
