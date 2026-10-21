using System;
using System.Diagnostics.Tracing;
using System.IO;
using System.Linq;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Storage.Internal;
using Assimalign.Cohesion.Database.Storage.Tests.TestObjects;
using Assimalign.Cohesion.Testing;

namespace Assimalign.Cohesion.Database.Storage.Tests;

[Collection(nameof(StorageEventSourceCollection))]
public sealed class StorageHeaderReadTests
{
    [Theory(DisplayName = "Cohesion Test [Storage] - Header read: an unreadable 4 KiB block opens from the other slot")]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    public void Open_UnreadableHeaderHalf_ShouldRecoverFromOtherSlot(int block, bool incompleteRead)
    {
        // Arrange: both slots carry the same anchor, with durable rows in the journal.
        var source = TornStorage.Create(name: "read-half");
        source.Checkpoint([17, 19]);
        source.FlushHeader();
        source.Insert("durable row");
        var images = source.CaptureDurable();
        source.Dispose();
        using var data = new CrashSimulationStream(images.Data, writeThrough: true);
        using var journal = new CrashSimulationStream(images.Journal, writeThrough: true);
        var faults = new DeviceFaults
        {
            UnreadableHeaderBlock = block, HeaderReadReturnsZero = incompleteRead,
            HeaderReadPrefixBytes = 113, MaximumDataReadSize = 113,
        };

        // Act
        using var opened = TornStorage.Open(data, journal, faults: faults);
        bool repairPending = opened.IdentityRepairPending;
        faults.Clear();
        opened.FlushHeader();
        opened.FlushHeader();

        // Assert: identity, checkpoint anchor and journal replay all survived the read error.
        faults.HeaderReadFailures.ShouldBe(1);
        ((string)opened.Name).ShouldBe("read-half");
        opened.CheckpointActiveTransactions.ShouldBe([17L, 19L]);
        opened.ScanText().ShouldBe(["durable row"]);
        repairPending.ShouldBe(block == 0);
        opened.IdentityRepairPending.ShouldBeFalse();
        StorageHeaderPage.VerifyIdentity(StorageHeaderPage.IdentityBlock(opened.CaptureDurable().Data)).ShouldBeTrue();
    }

    [Theory(DisplayName = "Cohesion Test [Storage] - Header read: an unreadable half and checksum-damaged other slot is coded page-0 corruption")]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    public void Open_UnreadableHalfAndNoValidSlot_ShouldThrowCorruptionWithoutWriting(int block, bool incompleteRead)
    {
        // Arrange
        var source = TornStorage.Create();
        source.FlushHeader();
        var images = source.CaptureDurable();
        source.Dispose();
        images.Data[StorageHeaderPage.SlotOffset(1 - block)] ^= 0x5A;
        using var data = new CrashSimulationStream(images.Data, writeThrough: true);
        using var journal = new CrashSimulationStream(images.Journal, writeThrough: true);
        var faults = new DeviceFaults { UnreadableHeaderBlock = block, HeaderReadReturnsZero = incompleteRead };

        // Act
        var error = Should.Throw<StorageCorruptionException>(() =>
        {
            using var opened = TornStorage.Open(data, journal, faults: faults);
        });

        // Assert
        error.PageId.ShouldBe((PageId)0L);
        error.Code.ShouldBe("COHDBS003");
        if (incompleteRead) { error.InnerException.ShouldBeOfType<StorageIOException>(); }
        else { error.InnerException.ShouldBeOfType<IOException>(); }
        StorageCorruptionException.Find(new AggregateException(error)).ShouldBeSameAs(error);
        faults.HeaderReadFailures.ShouldBe(1);
        data.CaptureLive().ShouldBe(images.Data);
        journal.CaptureLive().ShouldBe(images.Journal);
    }

    [Theory(DisplayName = "Cohesion Test [Storage] - Header read: an unreadable newest generation after journal truncation refuses to roll back")]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    public void Open_UnreadableNewestSlotWithoutJournalCoverage_ShouldRefuseAsIoWithoutWriting(int block, bool emptyJournal)
    {
        // Arrange: checkpoint between the two generations removes the older slot's evidence.
        var source = TornStorage.Create(name: "uncovered-header");
        if (block == 1) { source.FlushHeader(); }
        source.Insert("older row");
        source.Checkpoint([17, 19]);
        var older = source.HeaderState;
        source.Insert("newer row");
        source.Checkpoint([23, 29]);
        var newer = source.HeaderState;
        var images = source.CaptureDurable();
        StorageHeaderPage.TryReadSlot(images.Data.AsSpan(StorageHeaderPage.SlotOffset(newer.Slot)), out var newest).ShouldBeTrue();
        source.Dispose();
        if (emptyJournal) { images.Journal = []; }
        using var data = new CrashSimulationStream(images.Data, writeThrough: true);
        using var journal = new CrashSimulationStream(images.Journal, writeThrough: true);
        var faults = new DeviceFaults { UnreadableHeaderBlock = block };
        using var recorder = new EventSourceRecorder(StorageEventSource.Log, EventLevel.Warning);

        // Act
        var error = Should.Throw<IOException>(() =>
        {
            using var opened = TornStorage.Open(data, journal, faults: faults);
        });

        // Assert: readable bytes have no checksum damage; missing coverage is an I/O refusal.
        newer.Slot.ShouldBe(block);
        newer.Generation.ShouldBeGreaterThan(older.Generation);
        newer.LsnFloor.ShouldBeGreaterThan(older.LsnFloor);
        newest!.SequenceFloor.ShouldBeGreaterThan(1);
        StorageCorruptionException.Find(error).ShouldBeNull();
        faults.HeaderReadFailures.ShouldBe(1);
        data.CaptureLive().ShouldBe(images.Data);
        journal.CaptureLive().ShouldBe(images.Journal);
        recorder.Events.ShouldNotContain(e => e.EventId == 18 || e.EventId == 0);
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Header read: two unreadable halves retain the I/O classification without checksum evidence")]
    public void Open_BothHalvesUnreadable_ShouldPreserveIoWithoutWriting()
    {
        // Arrange
        var source = TornStorage.Create();
        source.FlushHeader();
        var images = source.CaptureDurable();
        source.Dispose();
        using var data = new CrashSimulationStream(images.Data, writeThrough: true);
        using var journal = new CrashSimulationStream(images.Journal, writeThrough: true);
        var faults = new DeviceFaults { FailAllHeaderReads = true };

        // Act
        var error = Should.Throw<IOException>(() => TornStorage.Open(data, journal, faults: faults));

        // Assert
        StorageCorruptionException.Find(error).ShouldBeNull();
        faults.HeaderReadFailures.ShouldBe(2);
        data.CaptureLive().ShouldBe(images.Data);
        journal.CaptureLive().ShouldBe(images.Journal);
    }

    [Theory(DisplayName = "Cohesion Test [Storage] - Header read: a checkpoint with the expected LSN but a different anchor proves no coverage")]
    [InlineData(0)]
    [InlineData(1)]
    public void Open_CheckpointWithDifferentAnchor_ShouldRefuseIoWithoutWriting(int block)
    {
        // Arrange
        var source = TornStorage.Create();
        if (block == 1) { source.FlushHeader(); }
        source.Insert("row");
        source.Checkpoint([17, 19]);
        long olderFloor = source.HeaderState.LsnFloor;
        source.FlushHeader();
        var images = source.CaptureDurable();
        source.Dispose();
        using (var replacement = new MemoryStream())
        {
            using var log = StorageJournal.Create(replacement, leaveOpen: true);
            log.RaiseLsnFloor(olderFloor);
            log.Checkpoint([17, 23], forceDurable: false).ShouldBe(olderFloor + 1);
            images.Journal = replacement.ToArray();
        }
        using var data = new CrashSimulationStream(images.Data, writeThrough: true);
        using var journal = new CrashSimulationStream(images.Journal, writeThrough: true);
        var faults = new DeviceFaults { UnreadableHeaderBlock = block };

        // Act
        var error = Should.Throw<IOException>(() =>
        {
            using var opened = TornStorage.Open(data, journal, faults: faults);
        });

        // Assert
        StorageCorruptionException.Find(error).ShouldBeNull();
        faults.HeaderReadFailures.ShouldBe(1);
        data.CaptureLive().ShouldBe(images.Data);
        journal.CaptureLive().ShouldBe(images.Journal);
    }

    [Theory(DisplayName = "Cohesion Test [Storage] - Header read: covered older generations recover rows and sequence floors and warn on every fallback")]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    public void Open_UnreadableNewestSlotWithJournalCoverage_ShouldRecoverAndWarn(int block, bool checkpointProof)
    {
        // Arrange: either an untruncated prefix or this generation's checkpoint proves coverage.
        string name = "covered-header-" + Guid.NewGuid().ToString("N");
        var source = TornStorage.Create(name: name);
        if (block == 1) { source.FlushHeader(); }
        source.Insert("older row");
        if (checkpointProof) { source.Checkpoint([17, 19]); } else { source.FlushHeader(); }
        var older = source.HeaderState;
        source.Insert("newer row");
        source.FlushHeader();
        var newer = source.HeaderState;
        var images = source.CaptureDurable();
        StorageHeaderPage.TryReadSlot(images.Data.AsSpan(StorageHeaderPage.SlotOffset(newer.Slot)), out var newest).ShouldBeTrue();
        source.Dispose();
        using var recorder = new EventSourceRecorder(StorageEventSource.Log, EventLevel.Warning);

        // Act: two separate opens must each report their accepted fallback, even back-to-back.
        for (int attempt = 0; attempt < 2; attempt++)
        {
            using var data = new CrashSimulationStream(images.Data, writeThrough: true);
            using var journal = new CrashSimulationStream(images.Journal, writeThrough: true);
            var faults = new DeviceFaults { UnreadableHeaderBlock = block, HeaderReadPrefixBytes = 113, MaximumDataReadSize = 113 };
            using var opened = TornStorage.Open(data, journal, faults: faults);

            // Assert
            opened.HeaderState.Generation.ShouldBe(older.Generation);
            opened.ScanText().ShouldBe(["older row", "newer row"]);
            opened.CheckpointActiveTransactions.ShouldBe(checkpointProof ? [17L, 19L] : []);
            using var next = opened.BeginTransaction();
            next.Sequence.ShouldBeGreaterThan(newest!.SequenceFloor);
            next.Rollback();
            faults.HeaderReadFailures.ShouldBe(1);
        }

        var warnings = recorder.Events.Where(e => e.EventId == 18).ToArray();
        warnings.Length.ShouldBe(2);
        foreach (var warning in warnings)
        {
            warning.EventName.ShouldBe("HeaderReadFallback");
            warning.Level.ShouldBe(EventLevel.Warning);
            warning.PayloadNames.ShouldBe(["database", "slot", "generation", "lsnFloor", "exceptionType", "exceptionMessage"]);
            warning.Payload!.ToArray().ShouldBe(new object?[]
            {
                name, older.Slot, older.Generation, older.LsnFloor,
                typeof(IOException).FullName, "Injected header block read failure.",
            });
        }
        recorder.Events.ShouldNotContain(e => e.EventId == 0);
    }
}
