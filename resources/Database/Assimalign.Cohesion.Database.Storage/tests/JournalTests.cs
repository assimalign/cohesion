using System;
using System.IO;
using System.Linq;
using System.Text;

using Assimalign.Cohesion.FileSystem;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Storage.Tests;

/// <summary>
/// Tests for the write-ahead log: LSN ordering, record round-trips, durability
/// tracking, torn-tail tolerance, and checkpoint truncation (#160).
/// </summary>
public sealed class JournalTests
{
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

    private sealed class ForwardOnlyStream : MemoryStream
    {
        public override bool CanSeek => false;
    }
}
