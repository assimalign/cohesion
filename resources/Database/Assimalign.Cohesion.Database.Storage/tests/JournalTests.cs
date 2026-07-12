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

    [Fact(DisplayName = "Cohesion Test [Storage] - Journal: EnsureDurable advances the durable LSN")]
    public void Journal_EnsureDurable_ShouldAdvanceDurableLsn()
    {
        // Arrange
        using var stream = new MemoryStream();
        using var journal = new StreamJournal(stream, leaveOpen: true);

        long lsn = journal.AppendBegin(1);
        journal.DurableLsn.ShouldBe(0L);

        // Act
        journal.EnsureDurable(lsn);

        // Assert
        journal.DurableLsn.ShouldBeGreaterThanOrEqualTo(lsn);
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Journal: reopened journal continues LSNs after the last record")]
    public void Journal_Reopen_ShouldContinueLsnSequence()
    {
        // Arrange
        using var stream = new MemoryStream();

        using (var journal = new StreamJournal(stream, leaveOpen: true))
        {
            journal.AppendBegin(1);
            journal.AppendCommit(1);
            journal.Flush(forceDurable: true);
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

    [Fact(DisplayName = "Cohesion Test [Storage] - Journal: checkpoint truncates and LSNs stay monotonic")]
    public void Journal_Checkpoint_ShouldTruncateAndPreserveLsnMonotonicity()
    {
        // Arrange
        using var stream = new MemoryStream();
        using var journal = new StreamJournal(stream, leaveOpen: true);

        journal.AppendBegin(1);
        journal.AppendCommit(1);
        long lastBefore = journal.LastLsn;

        // Act
        long checkpointLsn = journal.Checkpoint(ReadOnlySpan<long>.Empty);
        var records = journal.ReadAll();

        // Assert: only the checkpoint record remains and its LSN continues the sequence.
        checkpointLsn.ShouldBe(lastBefore + 1);
        records.Count.ShouldBe(1);
        records[0].Type.ShouldBe(JournalRecordType.Checkpoint);
        records[0].Lsn.ShouldBe(checkpointLsn);
        journal.DurableLsn.ShouldBe(checkpointLsn);
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Journal: checkpoint payload carries active transaction sequences")]
    public void Journal_Checkpoint_ShouldCarryActiveTransactions()
    {
        // Arrange
        using var stream = new MemoryStream();
        using var journal = new StreamJournal(stream, leaveOpen: true);

        // Act
        journal.Checkpoint(stackalloc long[] { 5L, 9L });
        var records = journal.ReadAll();

        // Assert
        records.Count.ShouldBe(1);
        records[0].Payload.Length.ShouldBe(2 * sizeof(long));
        BitConverter.ToInt64(records[0].Payload.Span).ShouldBe(5L);
        BitConverter.ToInt64(records[0].Payload.Span[8..]).ShouldBe(9L);
    }
}
