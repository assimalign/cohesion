using System;
using System.IO;
using System.Linq;
using System.Text;
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
        using var journal = new StreamJournal(stream, leaveOpen: true);
        journal.AppendBegin(7);
        journal.AppendOperation(7, new byte[8192]);
        journal.AppendCommit(7);
        long position = stream.Position;

        using (var records = journal.ReadSequential().GetEnumerator())
        {
            records.MoveNext().ShouldBeTrue();
            records.Current.Type.ShouldBe(JournalRecordType.BeginTransaction);
            stream.Position.ShouldBeLessThan(stream.Length);
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
        using var journal = new StreamJournal(stream, leaveOpen: true);

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

    [Fact(DisplayName = "Cohesion Test [Storage] - Journal: page images round-trip with page id and payload")]
    public void Journal_PageImage_ShouldRoundTrip()
    {
        // Arrange
        using var stream = new MemoryStream();
        using var journal = new StreamJournal(stream, leaveOpen: true);
        var image = new byte[Units.Page.Size];
        image[100] = 0xAB;

        // Act
        journal.AppendPageImage(3, (PageId)9L, JournalRecordType.BeforePageImage, image);
        var records = journal.ReadAll();

        // Assert
        records.Count.ShouldBe(1);
        records[0].Type.ShouldBe(JournalRecordType.BeforePageImage);
        ((long)records[0].PageId).ShouldBe(9L);
        records[0].Payload.Length.ShouldBe(Units.Page.Size);
        records[0].Payload.Span[100].ShouldBe((byte)0xAB);
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Journal: page-image append rejects non-image record types")]
    public void Journal_AppendPageImage_NonImageType_ShouldThrow()
    {
        // Arrange
        using var stream = new MemoryStream();
        using var journal = new StreamJournal(stream, leaveOpen: true);

        // Act / Assert
        Should.Throw<ArgumentOutOfRangeException>(
            () => journal.AppendPageImage(1, (PageId)1L, JournalRecordType.CommitTransaction, new byte[8]));
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Journal: unsupported EnsureDurable cannot advance the durable LSN")]
    public void Journal_EnsureDurable_ShouldAdvanceDurableLsn()
    {
        // Arrange
        using var stream = new MemoryStream();
        using var journal = new StreamJournal(stream, leaveOpen: true);

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

        using (var journal = new StreamJournal(stream, leaveOpen: true))
        {
            journal.AppendBegin(1);
            journal.AppendCommit(1);
            // #1018: reopening live bytes does not prove that a memory stream was durable.
            Should.Throw<NotSupportedException>(() => journal.Flush(forceDurable: true));
            journal.DurableLsn.ShouldBe(0L);
        }

        // Act
        using var reopened = new StreamJournal(stream, leaveOpen: true);
        long next = reopened.AppendBegin(2);

        // Assert
        next.ShouldBe(3L);
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Journal: a torn tail record is ignored on read")]
    public void Journal_TornTail_ShouldBeIgnored()
    {
        // Arrange: write two full records, then truncate the stream mid-record.
        using var stream = new MemoryStream();

        using (var journal = new StreamJournal(stream, leaveOpen: true))
        {
            journal.AppendBegin(1);
            journal.AppendOperation(1, Encoding.UTF8.GetBytes("keep"));
            journal.AppendOperation(1, Encoding.UTF8.GetBytes("torn-away"));
            journal.Flush();
        }

        stream.SetLength(stream.Length - 5); // tear the last frame

        // Act
        using var reopened = new StreamJournal(stream, leaveOpen: true);
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

        using (var journal = new StreamJournal(stream, leaveOpen: true))
        {
            journal.AppendBegin(1);
            journal.AppendOperation(1, Encoding.UTF8.GetBytes("payload"));
            journal.Flush();
        }

        // Flip a byte inside the second record's body.
        var buffer = stream.GetBuffer();
        buffer[(int)stream.Length - 6] ^= 0xFF;

        // Act
        using var reopened = new StreamJournal(stream, leaveOpen: true);
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
        using var journal = new StreamJournal(stream, leaveOpen: true);

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
        using var journal = new StreamJournal(stream, leaveOpen: true);

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
}
