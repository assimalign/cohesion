using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Text;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Storage.Tests.TestObjects;
using Assimalign.Cohesion.FileSystem;

namespace Assimalign.Cohesion.Database.Storage.Tests;

/// <summary>
/// Tests for the write-ahead log: LSN ordering, record round-trips, durability
/// tracking, torn-tail tolerance, and checkpoint truncation (#160).
/// </summary>
[Collection(StorageRaceCollection.Name)]
public sealed class JournalTests
{
    [Theory(DisplayName = "Cohesion Test [Storage] - Sequential journal: interrupted enumeration releases its buffer and locks")]
    [InlineData(false)]
    [InlineData(true)]
    public async System.Threading.Tasks.Task ReadSequential_InterruptedConsumer_ShouldPermitAnotherThreadToAppend(bool cancel)
    {
        // Arrange
        using var journal = StorageJournal.Create(new MemoryStream());
        journal.AppendOperation(1, new byte[300_000]);
        journal.AppendCommit(1);

        // Act: both an early break and an exception must unwind the nested iterators.
        if (cancel)
        {
            using var cancellation = new System.Threading.CancellationTokenSource();
            Should.Throw<OperationCanceledException>(() =>
            {
                foreach (var record in journal.ReadSequential())
                {
                    cancellation.Cancel();
                    cancellation.Token.ThrowIfCancellationRequested();
                }
            });
        }
        else
        {
            foreach (var record in journal.ReadSequential())
            {
                break;
            }
        }
        long next = await System.Threading.Tasks.Task.Run(() => journal.AppendCommit(2)).WaitAsync(TimeSpan.FromSeconds(10));

        // Assert
        next.ShouldBe(3);
        journal.ReadAll().Count.ShouldBe(3);
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Recovery journal: scans allocate per enumeration, not per frame")]
    public void ReadSequentialBorrowed_ManyFrames_ShouldHaveBoundedAllocation()
    {
        // Arrange: cross chunk boundaries and reuse the pool before measuring.
        using var journal = StorageJournal.Create(new MemoryStream());
        byte[] payload = new byte[64];
        for (int i = 0; i < 20_000; i++)
        {
            journal.AppendOperation(1, payload);
        }
        journal.Flush();
        long Scan()
        {
            long total = 0;
            foreach (var record in journal.ReadSequentialBorrowed())
            {
                total += record.Payload.Length;
            }
            return total;
        }
        Scan().ShouldBe(20_000L * payload.Length);

        // Act: the minimum of three rounds excludes unrelated allocation-context retirement.
        long allocated = long.MaxValue;
        long read = 0;
        for (int round = 0; round < 3; round++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            read = Scan();
            allocated = Math.Min(allocated, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        // Assert: a single object per frame would exceed 480 KiB; only iterator state is needed.
        read.ShouldBe(20_000L * payload.Length);
        allocated.ShouldBeLessThan(4096, $"{allocated} bytes for 20,000 frames");
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Journal readers: sequential payloads remain owned after enumeration and pool churn")]
    public void ReadSequential_ReusedBuffer_ShouldKeepRetainedPayloadsIndependent()
    {
        // Arrange: a frame larger than a read chunk also exercises pool growth.
        using var journal = StorageJournal.Create(new MemoryStream());
        byte[] first = new byte[300_000];
        Array.Fill(first, (byte)0xA7);
        journal.AppendOperation(1, first);
        journal.AppendOperation(2, [4, 5, 6]);
        var retained = journal.ReadAll();

        // Act: retain the public payload itself across MoveNext, disposal and pool churn.
        ReadOnlyMemory<byte> sequential;
        using (var records = journal.ReadSequential().GetEnumerator())
        {
            records.MoveNext().ShouldBeTrue();
            sequential = records.Current.Payload;
            records.MoveNext().ShouldBeTrue();
            records.Current.Payload.ToArray().ShouldBe(new byte[] { 4, 5, 6 });
            records.MoveNext().ShouldBeFalse();
        }
        journal.AppendOperation(3, new byte[300_000]);
        foreach (var record in journal.ReadSequential())
        {
            _ = record.Payload.Length;
        }

        byte[] rented = ArrayPool<byte>.Shared.Rent(524_288);
        try
        {
            Array.Fill(rented, (byte)0xCC);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }

        // Assert
        sequential.ToArray().ShouldBe(first);
        retained[0].Payload.ToArray().ShouldBe(first);
        retained[1].Payload.ToArray().ShouldBe(new byte[] { 4, 5, 6 });
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Sequential journal: reentrant append is refused without spinning")]
    public void Append_ActiveEnumerationOnSameThread_ShouldThrow()
    {
        // Arrange
        using var journal = StorageJournal.Create(new MemoryStream());
        journal.AppendCommit(1);
        using (var records = journal.ReadSequential().GetEnumerator())
        {
            records.MoveNext().ShouldBeTrue();

            // Act / Assert
            Should.Throw<InvalidOperationException>(() => journal.AppendCommit(2));
        }
        journal.AppendCommit(2).ShouldBe(2);
    }

    [Theory(DisplayName = "Cohesion Test [Storage] - Journal writes: closing a borrowed stream while live latches offline")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Flush_ClosedBorrowedStreamWhileLive_ShouldLatchOffline(int pending)
    {
        // Arrange: buffered write, written prefix awaiting flush, or a direct oversized frame.
        using var stream = new ClosedFlushStream();
        using var journal = StorageJournal.Create(stream, leaveOpen: true);
        journal.MaximumBufferBytes = 64;
        journal.AppendCommit(1);
        if (pending > 0)
        {
            journal.ReadAll();
        }
        int offline = 0;
        journal.Offline = _ => offline++;
        stream.Dispose();

        // Act
        var failure = Should.Throw<StorageOfflineException>(() =>
        {
            if (pending == 2)
            {
                journal.AppendOperation(1, new byte[128]);
            }
            else
            {
                journal.Flush();
            }
        });

        // Assert: provider disposal is a medium failure until the journal itself closes.
        failure.InnerException.ShouldBeOfType<ObjectDisposedException>();
        failure.Cause.ShouldBe(StorageOfflineCause.JournalFlush);
        journal.IsOffline.ShouldBeTrue();
        journal.OfflineError.ShouldBeSameAs(failure);
        offline.ShouldBe(1);
        Should.Throw<StorageOfflineException>(() => journal.AppendCommit(2));
        journal.Dispose();
        offline.ShouldBe(1);
    }

    [Theory(DisplayName = "Cohesion Test [Storage] - Journal disposal: an already closed borrowed stream does not take it offline")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Dispose_ClosedLeaveOpenStream_ShouldNotRaiseOffline(int pending)
    {
        // Arrange: no pending I/O, a buffered write, or a written but unflushed prefix.
        using var stream = new ClosedFlushStream();
        var journal = StorageJournal.Create(stream, leaveOpen: true);
        journal.LastLsn.ShouldBe(0);
        int offline = 0;
        journal.Offline = _ => offline++;
        if (pending > 0)
        {
            journal.AppendCommit(1);
            if (pending == 2)
            {
                journal.ReadAll();
            }
        }
        stream.Dispose();

        // Act
        if (pending == 0)
        {
            journal.Dispose();
        }
        else
        {
            Should.Throw<ObjectDisposedException>(() => journal.Dispose());
        }

        // Assert
        journal.IsOffline.ShouldBeFalse();
        journal.OfflineError.ShouldBeNull();
        offline.ShouldBe(0);
        journal.Dispose();
    }

    [Theory(DisplayName = "Cohesion Test [Storage] - Journal tail: corrupt lengths stop before overflow or oversized reads")]
    [InlineData(int.MaxValue, 3_000_000_000L)]
    [InlineData(int.MaxValue - 12, 100L)]
    [InlineData(-1, 100L)]
    public void ReadSequential_CorruptFrameLength_ShouldStopAtValidPrefix(int bodyLength, long reportedLength)
    {
        // Arrange: a sparse logical file avoids allocating the corrupt length's gigabytes.
        using var seed = new MemoryStream();
        using (var journal = StorageJournal.Create(seed, leaveOpen: true))
        {
            journal.AppendCommit(1);
            journal.Flush();
        }
        byte[] tail = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(tail, bodyLength);
        BinaryPrimitives.WriteInt32LittleEndian(tail.AsSpan(4), 0x324C4157);
        seed.Write(tail);
        using var stream = new ReportedLengthStream(seed.ToArray(), reportedLength);
        using var reopened = StorageJournal.Create(stream);

        // Act
        var records = reopened.ReadSequential().ToArray();

        // Assert
        records.ShouldHaveSingleItem().Lsn.ShouldBe(1);
        reopened.Length.ShouldBe(38);
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Recovery reader: checkpoint truncation invalidates the verified prefix")]
    public void ReadSequentialBorrowed_CheckpointReusesOffsets_ShouldVerifyNewFrames()
    {
        // Arrange
        using var stream = new MemoryStream();
        using var journal = StorageJournal.Create(stream, leaveOpen: true);
        journal.AppendOperation(1, new byte[1024]);
        journal.ReadSequentialBorrowed().Count().ShouldBe(1);
        Should.Throw<NotSupportedException>(() => journal.Checkpoint([]));
        stream.GetBuffer()[(int)stream.Length - 1] ^= 0xFF;

        // Act / Assert: the replacement checkpoint must not inherit the old prefix's trust.
        journal.ReadSequentialBorrowed().ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Recovery analysis: interleaved outcomes retain the same last applicable page record")]
    public void ReadRecoveryAnalysis_InterleavedTransactions_ShouldPreservePagePlanning()
    {
        // Arrange: an earlier delta commits after a later uncommitted image; a committed
        // image has a later uncommitted delta; a commit precedes another record of its sequence.
        using var journal = StorageJournal.Create(new MemoryStream());
        journal.AppendPageRecord(1, (PageId)1L, JournalRecordType.FullPageImage, [], out _);
        journal.AppendPageRecord(1, (PageId)1L, JournalRecordType.PageDelta, [], out _);
        long laterImage = journal.AppendPageRecord(2, (PageId)1L, JournalRecordType.FullPageImage, [], out _);
        journal.AppendCommit(1);
        long committedImage = journal.AppendPageRecord(3, (PageId)2L, JournalRecordType.FullPageImage, [], out _);
        journal.AppendPageRecord(4, (PageId)2L, JournalRecordType.PageDelta, [], out _);
        journal.AppendCommit(3);
        long lastDelta = journal.AppendPageRecord(3, (PageId)3L, JournalRecordType.PageDelta, [], out _);
        journal.AppendRollback(4);

        // Act
        var analysis = journal.ReadRecoveryAnalysis(default, out _);

        // Assert: promoting an older record cannot replace a later image's position.
        analysis.Committed.Order().ShouldBe([1L, 3L]);
        analysis.MaxSequence.ShouldBe(4);
        analysis.Pages[1].LastLsn.ShouldBe(laterImage);
        analysis.Pages[1].Committed.ShouldBeTrue();
        analysis.Pages[2].LastLsn.ShouldBe(committedImage);
        analysis.Pages[2].Committed.ShouldBeTrue();
        analysis.Pages[3].LastLsn.ShouldBe(lastDelta);
        analysis.Pages[3].Committed.ShouldBeTrue();

        // An append invalidates the cached analysis and makes the pending delta applicable.
        journal.AppendCommit(4);
        journal.ReadRecoveryAnalysis(default, out _).Pages[2].LastLsn.ShouldBe(committedImage + 1);
    }

    [Fact]
    public void ReadSequential_EarlyDisposalRestoresPositionAndAllowsNextAppend()
    {
        using var stream = new MemoryStream();
        using var journal = StorageJournal.Create(stream, leaveOpen: true);
        journal.AppendBegin(7);
        journal.AppendOperation(7, new byte[8192]);
        journal.AppendCommit(7);
        journal.Flush();
        long position = stream.Position;

        // The scan reads the stream in chunks (#1253), so mid-enumeration its cursor may be
        // anywhere; disposing the enumeration early must still put it back.
        using (var records = journal.ReadSequential().GetEnumerator())
        {
            records.MoveNext().ShouldBeTrue();
            records.Current.Type.ShouldBe(JournalRecordType.BeginTransaction);
            stream.Position = 0;
        }
        stream.Position.ShouldBe(position);
        journal.AppendBegin(8).ShouldBe(4);
        journal.ReadSequential().Select(record => record.Lsn).ShouldBe(new long[] { 1, 2, 3, 4 });
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Journal: LSNs are sequential and records round-trip in order")]
    public void Journal_AppendedRecords_ShouldRoundTripInLsnOrder()
    {
        // Arrange
        using var stream = new MemoryStream();
        using var journal = StorageJournal.Create(stream, leaveOpen: true);

        // Act
        long begin = journal.AppendBegin(7);
        long operation = journal.AppendOperation(7, Encoding.UTF8.GetBytes("row-1"));
        long commit = journal.AppendCommit(7);

        var records = journal.ReadAll();

        // Assert
        new[] { begin, operation, commit }.ShouldBe(new[] { 1L, 2L, 3L });
        records.Count.ShouldBe(3);
        records.Select(x => x.Lsn).ShouldBe(new[] { 1L, 2L, 3L });
        records[0].Type.ShouldBe(JournalRecordType.BeginTransaction);
        records[0].TransactionSequence.ShouldBe(7L);
        records[1].Type.ShouldBe(JournalRecordType.Operation);
        Encoding.UTF8.GetString(records[1].Payload.Span).ShouldBe("row-1");
        records[2].Type.ShouldBe(JournalRecordType.CommitTransaction);
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Journal: page images round-trip with page id and payload, their zero bytes elided")]
    public void Journal_PageImage_ShouldRoundTrip()
    {
        // Arrange
        using var stream = new MemoryStream();
        using var journal = StorageJournal.Create(stream, leaveOpen: true);
        var image = new byte[Units.Page.Size];
        image[100] = 0xAB;
        image[Units.Page.LsnFieldOffset] = 0x07;

        // Act
        journal.AppendPageImage(3, (PageId)9L, JournalRecordType.FullPageImage, image);
        journal.AppendPageImage(3, (PageId)9L, JournalRecordType.CommittedPageImage, image);
        var records = journal.ReadAll();
        var restored = new byte[Units.Page.Size];
        string? problem = Internal.PageImageCodec.TryApplyImage(records[0].Payload.Span, restored);

        // Assert: one run of one byte; the LSN field never travels, but a committed image names it
        // as its base.
        records.Count.ShouldBe(2);
        records[0].Type.ShouldBe(JournalRecordType.FullPageImage);
        ((long)records[0].PageId).ShouldBe(9L);
        records[0].Payload.Length.ShouldBe(Internal.PageImageCodec.RunHeaderSize + 1);
        problem.ShouldBeNull();
        restored[100].ShouldBe((byte)0xAB);
        restored[Units.Page.LsnFieldOffset].ShouldBe((byte)0);
        records[1].Type.ShouldBe(JournalRecordType.CommittedPageImage);
        System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(records[1].Payload.Span).ShouldBe(7L);
        records[1].Payload.Length.ShouldBe(Internal.PageImageCodec.BaseLsnSize + Internal.PageImageCodec.RunHeaderSize + 1);
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Journal: page-image append rejects other record types and images that are not a page")]
    public void Journal_AppendPageImage_NonImageType_ShouldThrow()
    {
        // Arrange
        using var stream = new MemoryStream();
        using var journal = StorageJournal.Create(stream, leaveOpen: true);

        // Act / Assert
        Should.Throw<ArgumentOutOfRangeException>(
            () => journal.AppendPageImage(1, (PageId)1L, JournalRecordType.CommitTransaction, new byte[Units.Page.Size]));
        Should.Throw<ArgumentOutOfRangeException>(
            () => journal.AppendPageImage(1, (PageId)1L, JournalRecordType.PageDelta, new byte[Units.Page.Size]));
        Should.Throw<ArgumentException>(
            () => journal.AppendPageImage(1, (PageId)1L, JournalRecordType.FullPageImage, new byte[8]));
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Journal: unsupported EnsureDurable cannot advance the durable LSN")]
    public void Journal_EnsureDurable_ShouldAdvanceDurableLsn()
    {
        // Arrange
        using var stream = new MemoryStream();
        using var journal = StorageJournal.Create(stream, leaveOpen: true);

        long lsn = journal.AppendBegin(1);
        journal.DurableLsn.ShouldBe(0L);

        // Act
        // #1018: a bare memory stream cannot acknowledge a durable flush.
        Should.Throw<NotSupportedException>(() => journal.EnsureDurable(lsn));

        // Assert
        journal.DurableLsn.ShouldBe(0L);
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Journal: unsupported durable flush fails before reopening live bytes")]
    public void Journal_Reopen_ShouldContinueLsnSequence()
    {
        // Arrange
        using var stream = new MemoryStream();

        using (var journal = StorageJournal.Create(stream, leaveOpen: true))
        {
            journal.AppendBegin(1);
            journal.AppendCommit(1);
            // #1018: reopening live bytes does not prove that a memory stream was durable.
            Should.Throw<NotSupportedException>(() => journal.Flush(forceDurable: true));
            journal.DurableLsn.ShouldBe(0L);
        }

        // Act
        using var reopened = StorageJournal.Create(stream, leaveOpen: true);
        long next = reopened.AppendBegin(2);

        // Assert
        next.ShouldBe(3L);
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Journal: a torn tail record is ignored on read")]
    public void Journal_TornTail_ShouldBeIgnored()
    {
        // Arrange: write two full records, then truncate the stream mid-record.
        using var stream = new MemoryStream();

        using (var journal = StorageJournal.Create(stream, leaveOpen: true))
        {
            journal.AppendBegin(1);
            journal.AppendOperation(1, Encoding.UTF8.GetBytes("keep"));
            journal.AppendOperation(1, Encoding.UTF8.GetBytes("torn-away"));
            journal.Flush();
        }

        stream.SetLength(stream.Length - 5); // tear the last frame

        // Act
        using var reopened = StorageJournal.Create(stream, leaveOpen: true);
        var records = reopened.ReadAll();

        // Assert
        records.Count.ShouldBe(2);
        Encoding.UTF8.GetString(records[1].Payload.Span).ShouldBe("keep");
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Journal: a corrupted record terminates the scan")]
    public void Journal_CorruptedRecord_ShouldTerminateScan()
    {
        // Arrange
        using var stream = new MemoryStream();

        using (var journal = StorageJournal.Create(stream, leaveOpen: true))
        {
            journal.AppendBegin(1);
            journal.AppendOperation(1, Encoding.UTF8.GetBytes("payload"));
            journal.Flush();
        }

        // Flip a byte inside the second record's body.
        var buffer = stream.GetBuffer();
        buffer[(int)stream.Length - 6] ^= 0xFF;

        // Act
        using var reopened = StorageJournal.Create(stream, leaveOpen: true);
        var records = reopened.ReadAll();

        // Assert
        records.Count.ShouldBe(1);
        records[0].Type.ShouldBe(JournalRecordType.BeginTransaction);
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Journal: unsupported checkpoint durability fails without acknowledgment")]
    public void Journal_Checkpoint_ShouldTruncateAndPreserveLsnMonotonicity()
    {
        // Arrange
        using var stream = new MemoryStream();
        using var journal = StorageJournal.Create(stream, leaveOpen: true);

        journal.AppendBegin(1);
        journal.AppendCommit(1);
        long lastBefore = journal.LastLsn;

        // Act
        // #1018: checkpoint must fail if its backing cannot honor durability.
        Should.Throw<NotSupportedException>(() => journal.Checkpoint(ReadOnlySpan<long>.Empty));
        long checkpointLsn = journal.LastLsn;
        var records = journal.ReadAll();

        // Assert: only the checkpoint record remains and its LSN continues the sequence.
        checkpointLsn.ShouldBe(lastBefore + 1);
        records.Count.ShouldBe(1);
        records[0].Type.ShouldBe(JournalRecordType.Checkpoint);
        records[0].Lsn.ShouldBe(checkpointLsn);
        journal.DurableLsn.ShouldBe(0L);
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Journal: active checkpoint payload does not imply supported durability")]
    public void Journal_Checkpoint_ShouldCarryActiveTransactions()
    {
        // Arrange
        using var stream = new MemoryStream();
        using var journal = StorageJournal.Create(stream, leaveOpen: true);

        // Act
        // #1018: a correctly encoded checkpoint still needs an explicit durable backing.
        Should.Throw<NotSupportedException>(() => journal.Checkpoint(new long[] { 5L, 9L }));
        var records = journal.ReadAll();

        // Assert
        records.Count.ShouldBe(1);
        records[0].Payload.Length.ShouldBe(2 * sizeof(long));
        BitConverter.ToInt64(records[0].Payload.Span).ShouldBe(5L);
        BitConverter.ToInt64(records[0].Payload.Span[8..]).ShouldBe(9L);
        journal.DurableLsn.ShouldBe(0L);
    }

    /// <summary>
    /// The journal's construction surface is its static factories (rule 1, owner decision 27 of
    /// 2026-10-06): no public constructor, and each <c>Create</c> overload refuses a null medium
    /// and a stream it cannot read, write and seek before it builds anything.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Storage] - Journal: the factories are the only way in and validate their medium")]
    public void Create_InvalidMedium_ShouldThrowAndExposeNoPublicConstructor()
    {
        // Arrange
        using var forwardOnly = new ForwardOnlyStream();

        // Act / Assert
        typeof(StorageJournal).GetConstructors().ShouldBeEmpty();
        Should.Throw<ArgumentNullException>(() => StorageJournal.Create((Stream)null!)).ParamName.ShouldBe("stream");
        Should.Throw<ArgumentNullException>(() => StorageJournal.Create((StorageStream)null!)).ParamName.ShouldBe("stream");
        Should.Throw<ArgumentNullException>(() => StorageJournal.Create((IFileSystemFileHandle)null!)).ParamName.ShouldBe("handle");
        Should.Throw<ArgumentException>(() => StorageJournal.Create(forwardOnly)).ParamName.ShouldBe("stream");
        Should.Throw<ArgumentNullException>(() => StorageJournal.FromFile("journal.log", null!)).ParamName.ShouldBe("fileSystem");
    }

    private sealed class ClosedFlushStream : MemoryStream
    {
        public override void Flush()
        {
            ObjectDisposedException.ThrowIf(!CanWrite, this);
            base.Flush();
        }
    }

    private sealed class ReportedLengthStream : MemoryStream
    {
        private readonly long _reportedLength;

        internal ReportedLengthStream(byte[] content, long reportedLength) : base(content)
        {
            _reportedLength = reportedLength;
        }

        public override long Length => _reportedLength;
    }

    private sealed class ForwardOnlyStream : MemoryStream
    {
        public override bool CanSeek => false;
    }
}
