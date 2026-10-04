using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Storage.Tests.TestObjects;
using Assimalign.Cohesion.Database.Storage.Units;
using Assimalign.Cohesion.FileSystem;

namespace Assimalign.Cohesion.Database.Storage.Tests;

/// <summary>
/// The journal's user-space append buffer (#1252). An append encodes its frame in place and
/// writes nothing; the buffer drains to the medium in one write when it is full and before
/// anything that needs the records there: a commit's acknowledgment in every durability mode, a
/// reader, the write-ahead gate, a checkpoint's truncation and a close. Modelled on PostgreSQL's
/// WAL buffers (<c>XLogInsertRecord</c> copies into them, <c>XLogWrite</c>/<c>XLogFlush</c>
/// write them out, <c>src/backend/access/transam/xlog.c</c>).
/// </summary>
public sealed class JournalBufferTests
{
    private static readonly byte[] Image = CreateImage();

    [Fact(DisplayName = "Cohesion Test [Storage] - Journal buffer: appends write nothing, and one drain writes them all")]
    public void Append_ManyFrames_ShouldReachTheMediumInOneWritePerDrain()
    {
        // Arrange
        var medium = new RecordingHandle();
        using var journal = new StreamJournal(new StorageStream(medium));

        // Act
        long first = journal.AppendBegin(1);
        journal.AppendPageImage(1, (PageId)3L, JournalRecordType.BeforePageImage, Image);
        journal.AppendOperation(1, [1, 2, 3]);
        long last = journal.AppendCommit(1);
        int writesBeforeTheDrain = medium.Writes;
        long writtenBeforeTheDrain = journal.WrittenLsn;
        journal.EnsureDurable(last);

        // Assert
        writesBeforeTheDrain.ShouldBe(0);
        writtenBeforeTheDrain.ShouldBe(first - 1);
        medium.Writes.ShouldBe(1);
        medium.DurableFlushes.ShouldBe(1);
        journal.WrittenLsn.ShouldBe(last);
        journal.DurableLsn.ShouldBe(last);
        journal.BufferedLength.ShouldBe(0);
        Reopen(medium).Select(record => record.Lsn).ShouldBe([1L, 2L, 3L, 4L]);
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Journal buffer: frames are built in place, with no allocation per frame")]
    public void Append_AfterWarmUp_ShouldNotAllocatePerFrame()
    {
        // Arrange: a medium with room, and a buffer already grown to its maximum.
        var medium = new RecordingHandle(new MemoryStream(capacity: 64 * 1024 * 1024));
        using var journal = new StreamJournal(new StorageStream(medium));
        for (int i = 0; i < 200; i++)
        {
            journal.AppendPageImage(i, (PageId)1L, JournalRecordType.AfterPageImage, Image);
            journal.AppendCommit(i);
        }

        journal.Flush();
        journal.BufferCapacity.ShouldBe(StorageJournal.MaximumBufferSize);
        const int frames = 200;

        // Act: as many frames as fit the buffer, then the drain that writes them.
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < frames / 2; i++)
        {
            journal.AppendPageImage(i, (PageId)1L, JournalRecordType.AfterPageImage, Image);
            journal.AppendCommit(i);
        }

        journal.Flush();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // Assert: until #1252 every append allocated its frame (8 KiB for a page image).
        allocated.ShouldBeLessThan(1024, $"{allocated} bytes for {frames} frames");
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Journal buffer: the buffer grows to its maximum, then drains when full, keeping LSN order")]
    public void Append_MoreThanTheBufferHolds_ShouldGrowThenDrainInOrder()
    {
        // Arrange
        var medium = new RecordingHandle();
        using var journal = new StreamJournal(new StorageStream(medium));
        var capacities = new List<int>();

        // Act: 2.5 MiB of page images.
        for (int i = 1; i <= 320; i++)
        {
            journal.AppendPageImage(i, (PageId)i, JournalRecordType.AfterPageImage, Image);
            if (capacities.Count == 0 || capacities[^1] != journal.BufferCapacity)
            {
                capacities.Add(journal.BufferCapacity);
            }
        }

        int writesBeforeTheFlush = medium.Writes;
        journal.Flush();

        // Assert: 64 KiB doubling to 1 MiB; each full buffer went out in one write; every frame
        // reads back in LSN order.
        capacities.ShouldBe([64 * 1024, 128 * 1024, 256 * 1024, 512 * 1024, 1024 * 1024]);
        writesBeforeTheFlush.ShouldBe(2);
        medium.Writes.ShouldBe(3);
        var records = Reopen(medium);
        records.Select(record => record.Lsn).ShouldBe(Enumerable.Range(1, 320).Select(i => (long)i));
        records.Select(record => (long)record.PageId).ShouldBe(Enumerable.Range(1, 320).Select(i => (long)i));
        journal.Length.ShouldBe(medium.Length);
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Journal buffer: a frame larger than the buffer is written directly, after the frames ahead of it")]
    public void Append_FrameLargerThanTheBuffer_ShouldBeWrittenDirectlyInOrder()
    {
        // Arrange
        var medium = new RecordingHandle();
        using var journal = new StreamJournal(new StorageStream(medium)) { MaximumBufferBytes = 4096 };
        var large = new byte[10_000];
        new Random(7).NextBytes(large);

        // Act
        journal.AppendBegin(1);
        journal.AppendOperation(1, large);
        int writesAfterTheLargeFrame = medium.Writes;
        journal.AppendCommit(1);
        journal.Flush();

        // Assert: the buffered begin record, then the large frame, then the commit record.
        writesAfterTheLargeFrame.ShouldBe(2);
        var records = Reopen(medium);
        records.Select(record => record.Type).ShouldBe(
            [JournalRecordType.BeginTransaction, JournalRecordType.Operation, JournalRecordType.CommitTransaction]);
        records[1].Payload.ToArray().ShouldBe(large);
        journal.BufferCapacity.ShouldBeLessThanOrEqualTo(4096);
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Journal buffer: every reader drains the buffer first")]
    public void Read_WithBufferedRecords_ShouldDrainFirst()
    {
        // Arrange
        var medium = new RecordingHandle();
        using var journal = new StreamJournal(new StorageStream(medium));
        journal.AppendBegin(1);
        journal.AppendCommit(1);

        // Act
        var all = journal.ReadAll();
        int writesAfterReadAll = medium.Writes;
        journal.AppendRollback(2);
        var sequential = journal.ReadSequential().ToList();

        // Assert
        all.Select(record => record.Lsn).ShouldBe([1L, 2L]);
        writesAfterReadAll.ShouldBe(1);
        sequential.Select(record => record.Lsn).ShouldBe([1L, 2L, 3L]);
        medium.Writes.ShouldBe(2);
        journal.WrittenLsn.ShouldBe(3L);
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Journal buffer: a close drains the buffer")]
    public void Dispose_WithBufferedRecords_ShouldDrain()
    {
        // Arrange
        var medium = new RecordingHandle();
        var journal = new StreamJournal(new StorageStream(medium), leaveOpen: true);
        journal.AppendBegin(1);
        journal.AppendCommit(1);

        // Act
        journal.Dispose();

        // Assert
        Reopen(medium).Select(record => record.Type).ShouldBe([JournalRecordType.BeginTransaction, JournalRecordType.CommitTransaction]);
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Journal buffer: a checkpoint drains before it truncates, and a failed drain truncates nothing")]
    public void Checkpoint_WithBufferedRecords_ShouldDrainBeforeTruncating()
    {
        // Arrange
        var medium = new RecordingHandle();
        using var journal = new StreamJournal(new StorageStream(medium));
        journal.AppendBegin(1);
        journal.AppendCommit(1);
        journal.Flush();
        long written = medium.Length;
        journal.AppendBegin(2);

        // Act: the checkpoint's drain fails.
        medium.FailNextWrite = true;
        var offline = Should.Throw<StorageOfflineException>(() => journal.Checkpoint([2]));

        // Assert: nothing was truncated, so the records the checkpoint would have discarded are
        // still on the medium; the buffered begin record never reached it. The failure reads as a
        // journal flush (#1268's cause), and its message names the write.
        offline.Cause.ShouldBe(StorageOfflineCause.JournalFlush);
        offline.Message.ShouldContain("a write of the journal");
        journal.OfflineError.ShouldBeSameAs(offline);
        medium.Log.ShouldNotContain(entry => entry.StartsWith("SetLength", StringComparison.Ordinal));
        medium.Length.ShouldBe(written);
        Reopen(medium).Select(record => record.Lsn).ShouldBe([1L, 2L]);

        // Arrange: a healthy journal drains its buffer, then truncates.
        var healthy = new RecordingHandle();
        using var other = new StreamJournal(new StorageStream(healthy));
        other.AppendBegin(1);
        other.Flush();
        other.AppendBegin(2);

        // Act
        other.Checkpoint([2]);

        // Assert
        healthy.Log.SkipWhile(entry => !entry.StartsWith("Write 38 38", StringComparison.Ordinal))
            .Select(entry => entry.Split(' ')[0]).Take(3).ShouldBe(["Write", "SetLength", "Write"]);
        Reopen(healthy).Single().Type.ShouldBe(JournalRecordType.Checkpoint);
    }

    /// <summary>
    /// A process crash right after a commit is acknowledged — the files as the operating system
    /// holds them, nothing of the process — must keep the commit in every durability mode,
    /// <see cref="StorageCommitDurability.None"/> included, as it did when every append was a
    /// write of its own. A bracket committed without awaiting durability (an inner statement
    /// bracket) is acknowledged by its outer commit and may stay buffered until then.
    /// </summary>
    [Theory(DisplayName = "Cohesion Test [Storage] - Journal buffer: an acknowledged commit survives a process crash in every durability mode")]
    [InlineData(StorageCommitDurability.None)]
    [InlineData(StorageCommitDurability.Synchronous)]
    [InlineData(StorageCommitDurability.Grouped)]
    public void Commit_ThenProcessCrash_ShouldKeepTheAcknowledgedCommit(StorageCommitDurability durability)
    {
        // Arrange
        using var storage = BufferStorage.Create(durability);

        // Act: an acknowledged commit, then a bracket committed without its acknowledgment.
        storage.Insert("acknowledged");
        var crashAfterTheCommit = storage.CaptureImages();
        long lastBefore = storage.Log.LastLsn;
        using (var inner = storage.BeginTransaction())
        {
            storage.Insert(inner, "inner");
            inner.Commit(awaitDurability: false);
        }

        long innerCommit = storage.Log.LastLsn;
        bool innerBuffered = storage.Log.WrittenLsn < innerCommit;
        var crashBeforeTheOuterCommit = storage.CaptureImages();
        storage.EnsureCommitDurable(innerCommit);
        var crashAfterTheOuterCommit = storage.CaptureImages();

        // Assert
        using (var reopened = BufferStorage.Open(crashAfterTheCommit))
        {
            reopened.ReadAllText().ShouldBe(["acknowledged"]);
        }

        innerBuffered.ShouldBeTrue();
        innerCommit.ShouldBeGreaterThan(lastBefore);
        using (var reopened = BufferStorage.Open(crashBeforeTheOuterCommit))
        {
            reopened.ReadAllText().ShouldBe(["acknowledged"]);
        }

        using (var reopened = BufferStorage.Open(crashAfterTheOuterCommit))
        {
            reopened.ReadAllText().ShouldBe(["acknowledged", "inner"]);
        }

        storage.Log.WrittenLsn.ShouldBe(storage.Log.LastLsn);
        if (durability != StorageCommitDurability.None)
        {
            storage.Log.DurableLsn.ShouldBe(storage.Log.LastLsn);
        }
    }

    /// <summary>
    /// The write-ahead gate: the buffer pool steals a page an active transaction changed, and
    /// before the page reaches the data file the journal holds its before image — durably when
    /// the storage flushes durably, at least on the medium otherwise.
    /// </summary>
    [Theory(DisplayName = "Cohesion Test [Storage] - Journal buffer: a stolen page reaches the data file only after the journal holds its LSN")]
    [InlineData(StorageCommitDurability.None)]
    [InlineData(StorageCommitDurability.Synchronous)]
    public void StealDirtyPage_WithBufferedImages_ShouldDrainThroughThePageLsnFirst(StorageCommitDurability durability)
    {
        // Arrange: a pool of four pages, and a transaction that changes twelve.
        using var storage = BufferStorage.Create(durability, poolCapacity: 4);
        var violations = new List<string>();
        int stolen = 0;
        storage.DataMedium.OnWrite = (offset, bytes) =>
        {
            if (offset == 0 || offset % Page.Size != 0 || bytes.Length != Page.Size)
            {
                return;
            }

            long lsn = BitConverter.ToInt64(bytes.Slice(8, sizeof(long)));
            long held = durability == StorageCommitDurability.None ? storage.Log.WrittenLsn : storage.Log.DurableLsn;
            if (lsn > held)
            {
                violations.Add($"page {offset / Page.Size} at LSN {lsn}, journal through {held}");
            }

            stolen++;
        };

        // Act
        using (var transaction = storage.BeginTransaction())
        {
            for (int i = 0; i < 12; i++)
            {
                using var handle = storage.AllocatePageForWrite(transaction, PageType.Data);
                handle.MarkDirty();
            }

            transaction.Rollback();
        }

        // Assert
        stolen.ShouldBeGreaterThan(0);
        violations.ShouldBeEmpty();
    }

    /// <summary>
    /// The engines' fault-injecting media fail the write that carries a given record by reading
    /// the frames off the wire (<see cref="JournalFrames"/>), so every record type a drain carries
    /// must read back from the bytes of that one write, and a write that does not start with a
    /// whole frame carries none.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Storage] - Journal buffer: the records a drain carries read off the bytes of its one write")]
    public void Drain_RecordsOfEveryType_ShouldReadOffTheWire()
    {
        // Arrange
        var medium = new RecordingHandle();
        var writes = new List<byte[]>();
        medium.OnWrite = (_, bytes) => writes.Add(bytes.ToArray());
        using var journal = new StreamJournal(new StorageStream(medium));
        journal.AppendBegin(1);
        journal.AppendPageImage(1, (PageId)3L, JournalRecordType.BeforePageImage, Image);
        journal.AppendOperation(1, [1, 2, 3]);
        journal.AppendPageImage(1, (PageId)3L, JournalRecordType.AfterPageImage, Image);
        journal.AppendCommit(1);
        journal.AppendRollback(2);

        // Act
        journal.Flush();
        journal.Checkpoint([]);

        // Assert: one write per drain; the first carries every record appended before it, the
        // checkpoint's own write only its record.
        writes.Count.ShouldBe(2);
        JournalRecordType[] appended =
        [
            JournalRecordType.BeginTransaction, JournalRecordType.BeforePageImage, JournalRecordType.Operation,
            JournalRecordType.AfterPageImage, JournalRecordType.CommitTransaction, JournalRecordType.RollbackTransaction,
        ];
        appended.ShouldAllBe(type => JournalFrames.Carries(writes[0], type));
        JournalFrames.Carries(writes[0], JournalRecordType.Checkpoint).ShouldBeFalse();
        JournalFrames.Carries(writes[1], JournalRecordType.Checkpoint).ShouldBeTrue();
        appended.ShouldAllBe(type => !JournalFrames.Carries(writes[1], type));
        JournalFrames.Carries(writes[0].AsSpan(1), JournalRecordType.BeginTransaction).ShouldBeFalse();
    }

    /// <summary>
    /// Appends racing the journal's close (#1252 review): every LSN an append returned is on the
    /// medium once the close returns, and every other append is refused as disposed. An append
    /// that passed the disposed check before the close took the append lock used to buffer its
    /// record after the close's final drain and return an LSN that was never written.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Storage] - Journal buffer: an append racing the close is written or refused, never lost")]
    public void Dispose_RacingAppends_ShouldWriteEveryReturnedLsnOrRefuseTheAppend()
    {
        const int rounds = 50;
        const int appenders = 4;
        byte[] payload = [1, 2, 3, 4];
        int lost = 0;
        int refused = 0;
        long returned = 0;
        var unexpected = new List<Exception>();

        for (int round = 0; round < rounds; round++)
        {
            // Arrange: appenders hammering the journal before the close starts.
            var medium = new RecordingHandle();
            var journal = new StreamJournal(new StorageStream(medium), leaveOpen: true);
            var lsns = new List<long>[appenders];
            int started = 0;
            var threads = new Thread[appenders];
            for (int i = 0; i < appenders; i++)
            {
                var own = lsns[i] = new List<long>();
                threads[i] = new Thread(() =>
                {
                    Interlocked.Increment(ref started);
                    try
                    {
                        while (true)
                        {
                            own.Add(journal.AppendOperation(1, payload));
                        }
                    }
                    catch (ObjectDisposedException)
                    {
                        Interlocked.Increment(ref refused);
                    }
                    catch (Exception exception)
                    {
                        lock (unexpected)
                        {
                            unexpected.Add(exception);
                        }
                    }
                })
                { IsBackground = true };
                threads[i].Start();
            }

            SpinWait.SpinUntil(() => Volatile.Read(ref started) == appenders && journal.LastLsn > 2_000, TimeSpan.FromSeconds(10));

            // Act
            journal.Dispose();
            foreach (var thread in threads)
            {
                thread.Join(TimeSpan.FromSeconds(10)).ShouldBeTrue();
            }

            // Assert: the medium holds every LSN an append returned.
            var written = Reopen(medium).Select(record => record.Lsn).ToHashSet();
            foreach (var own in lsns)
            {
                returned += own.Count;
                lost += own.Count(lsn => !written.Contains(lsn));
            }
        }

        unexpected.ShouldBeEmpty();
        refused.ShouldBe(rounds * appenders);
        lost.ShouldBe(0, $"{lost} of {returned} returned LSNs were never written in {rounds} rounds");
    }

    private static IReadOnlyList<JournalRecord> Reopen(RecordingHandle medium)
        => new StreamJournal(new MemoryStream(medium.ToArray())).ReadAll();

    private static byte[] CreateImage()
    {
        var image = new byte[Page.Size];
        new Random(1252).NextBytes(image);
        return image;
    }

    /// <summary>
    /// A storage over recording handles with an explicit durability policy; its files can be
    /// captured as a process crash would leave them.
    /// </summary>
    private sealed class BufferStorage : Storage
    {
        private BufferStorage(RecordingHandle data, RecordingHandle journal, int poolCapacity)
            : base(new StorageStream(data), new StorageStream(journal), new StorageStream(new MemoryStream()), poolCapacity)
        {
            DataMedium = data;
            JournalMedium = journal;
        }

        public override StorageModel Model => StorageModel.Custom;

        public StorageJournal Log => (StorageJournal)WriteAheadLog;

        public RecordingHandle DataMedium { get; }

        public RecordingHandle JournalMedium { get; }

        public static BufferStorage Create(StorageCommitDurability durability, int poolCapacity = 64)
        {
            var storage = new BufferStorage(new RecordingHandle(), new RecordingHandle(), poolCapacity)
            {
                CommitDurability = durability,
                GroupCommitWindow = TimeSpan.FromMilliseconds(1),
            };
            storage.InitializeNew((Name)"journal-buffer-harness");
            return storage;
        }

        public static BufferStorage Open((byte[] Data, byte[] Journal) images)
        {
            var storage = new BufferStorage(new RecordingHandle(images.Data), new RecordingHandle(images.Journal), 64);
            storage.OpenExisting();
            return storage;
        }

        /// <summary>Gets what the operating system holds right now: what a process crash leaves.</summary>
        public (byte[] Data, byte[] Journal) CaptureImages() => (DataMedium.ToArray(), JournalMedium.ToArray());

        public void Insert(IStorageTransaction transaction, string text) => InsertRecord(transaction, Encoding.UTF8.GetBytes(text));

        public void Insert(string text)
        {
            using var transaction = BeginTransaction();
            Insert(transaction, text);
            transaction.Commit();
        }

        public string[] ReadAllText()
        {
            var texts = new List<string>();
            using var iterator = GetUnitIterator();
            while (iterator.MoveNext())
            {
                texts.Add(Encoding.UTF8.GetString(iterator.Current.Data.Span));
            }

            return [.. texts];
        }
    }

    /// <summary>
    /// A durable-capable handle over memory that counts writes and flushes, logs every
    /// operation, can fail its next write, and calls back on every write.
    /// </summary>
    private sealed class RecordingHandle : IFileSystemFileHandle
    {
        private readonly MemoryStream _stream;
        private readonly object _gate = new();
        private readonly List<string> _log = new();

        public RecordingHandle(MemoryStream? stream = null) => _stream = stream ?? new MemoryStream();

        public RecordingHandle(byte[] content)
            : this(new MemoryStream())
        {
            _stream.Write(content);
        }

        public int Writes { get; private set; }

        public int DurableFlushes { get; private set; }

        public bool FailNextWrite { get; set; }

        public WriteCallback? OnWrite { get; set; }

        public IReadOnlyList<string> Log { get { lock (_gate) { return _log.ToArray(); } } }

        public long Length { get { lock (_gate) { return _stream.Length; } } }

        public bool SupportsDurableFlush => true;

        public byte[] ToArray() { lock (_gate) { return _stream.ToArray(); } }

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
            OnWrite?.Invoke(offset, buffer);
            lock (_gate)
            {
                _log.Add($"Write {offset} {buffer.Length}");
                if (FailNextWrite)
                {
                    FailNextWrite = false;
                    throw new IOException("Injected journal write failure.");
                }

                _stream.Position = offset;
                _stream.Write(buffer);
                Writes++;
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
                _log.Add($"SetLength {length}");
                _stream.SetLength(length);
            }
        }

        public void Flush(bool durable)
        {
            lock (_gate)
            {
                _log.Add($"Flush {durable}");
                if (durable)
                {
                    DurableFlushes++;
                }
            }
        }

        public ValueTask FlushAsync(bool durable, CancellationToken cancellationToken = default)
        {
            Flush(durable);
            return default;
        }

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => default;
    }

    /// <summary>Called with each write's offset and bytes, before the write.</summary>
    private delegate void WriteCallback(long offset, ReadOnlySpan<byte> bytes);
}
