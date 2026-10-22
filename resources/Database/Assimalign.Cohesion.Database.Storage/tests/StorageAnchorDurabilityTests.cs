using System;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Storage.Internal;
using Assimalign.Cohesion.Database.Storage.Tests.TestObjects;

namespace Assimalign.Cohesion.Database.Storage.Tests;

[Collection(StorageRaceCollection.Name)]
public sealed class StorageAnchorDurabilityTests
{
    [Fact(DisplayName = "Cohesion Test [Storage] - Flush: a retired anchor returns to allocation only after its replacement slot is durable")]
    public void Flush_ShrinkingAnchorSucceeds_ShouldReleaseOnlyAfterSlotIsDurable()
    {
        // Arrange
        using var storage = TornStorage.Create();
        long[] writers = [.. Enumerable.Range(1, StorageHeaderPage.InlineAnchorCapacity + 1).Select(i => (long)i)];
        storage.Checkpoint(writers);
        long retired = storage.AnchorChainPages.Single();
        storage.Checkpoint([]);
        int writes = 0;
        storage.DataFaults.OnWriteAt = (StorageHeaderPage.SlotOffset(1 - storage.HeaderState.Slot), () =>
        {
            storage.FreeSpaceMap.IsAllocated((PageId)retired).ShouldBeTrue();
            writes++;
        });

        // Act
        storage.FlushHeader();

        // Assert
        writes.ShouldBe(1);
        storage.FreeSpaceMap.IsAllocated((PageId)retired).ShouldBeFalse();
        storage.FillPages(1).ShouldBe([retired]);
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Flush: failed slot durability keeps a retired anchor allocated")]
    public void Flush_ShrinkingAnchorFlushFails_ShouldKeepRetiredPageAllocated()
    {
        // Arrange
        using var storage = TornStorage.Create();
        long[] writers = [.. Enumerable.Range(1, StorageHeaderPage.InlineAnchorCapacity + 1).Select(i => (long)i)];
        storage.Checkpoint(writers);
        long retired = storage.AnchorChainPages.Single();
        storage.Checkpoint([]);
        int slot = storage.HeaderState.Slot;
        storage.DataFaults.FailFlushAfterWriteAt = StorageHeaderPage.SlotOffset(1 - slot);

        // Act
        Should.Throw<StorageOfflineException>(() => storage.FlushHeader());

        // Assert
        storage.DataFaults.FailedFlushes.ShouldBe(1);
        storage.HeaderState.Slot.ShouldBe(slot);
        storage.FreeSpaceMap.IsAllocated((PageId)retired).ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Checkpoint: failed anchor initialization returns its allocation")]
    public void Checkpoint_AnchorImageAppendFails_ShouldReleaseUnchainedPage()
    {
        // Arrange: force the image append to drain a full journal buffer and fail.
        using var storage = TornStorage.Create();
        storage.Log.Flush(forceDurable: true);
        storage.Log.MaximumBufferBytes = 64;
        storage.Log.AppendBegin(0);
        long allocatedBefore = storage.PageManager.PageCount - storage.PageManager.FreePageCount;
        storage.JournalFaults.FailNextWrite();
        long[] writers = [.. Enumerable.Range(1, StorageHeaderPage.InlineAnchorCapacity + 1).Select(i => (long)i)];

        // Act
        Should.Throw<StorageOfflineException>(() => storage.Checkpoint(writers));

        // Assert
        storage.IsOffline.ShouldBeTrue();
        storage.AnchorChainPages.ShouldBeEmpty();
        storage.PageManager.PageCount.ShouldBe(allocatedBefore + 1);
        (storage.PageManager.PageCount - storage.PageManager.FreePageCount).ShouldBe(allocatedBefore);
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Flush: a concurrent committed allocation survives interruption before a shrinking header slot write")]
    public void Flush_ShrinkingAnchorInterruptedAfterConcurrentCommit_ShouldRecoverCommittedRecord()
    {
        // Arrange
        var duration = Stopwatch.StartNew();
        int interruptions = 0;

        // Act: force the interleaving each time, for at least a quarter second and three commits.
        do
        {
            InterruptShrinkingHeaderAfterCommit();
            interruptions++;
        }
        while ((duration.Elapsed < TimeSpan.FromMilliseconds(250) || interruptions < 3)
            && duration.Elapsed < TimeSpan.FromSeconds(30));

        // Assert
        interruptions.ShouldBeGreaterThanOrEqualTo(3);
        duration.Elapsed.ShouldBeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(250));
    }

    private static void InterruptShrinkingHeaderAfterCommit()
    {
        // Arrange: slot 1 owns an overflow page; slot 0 carries the smaller checkpoint anchor.
        var point = new CrashPoint();
        var storage = TornStorage.Create(point, journalWriteThrough: false);
        long[] writers = [.. Enumerable.Range(1, StorageHeaderPage.InlineAnchorCapacity + 1).Select(i => (long)i)];
        storage.Checkpoint(writers);
        long retired = storage.AnchorChainPages.Single();
        storage.Checkpoint([]);
        long slotOffset = StorageHeaderPage.SlotOffset(1 - storage.HeaderState.Slot);
        (PageId PageId, int SlotIndex) location = default;
        long committedSequence = 0;
        int commits = 0;
        storage.DataFaults.OnWriteAt = (slotOffset, () =>
        {
            // Flush holds no transaction lock: allocate and durably commit on another thread
            // after it shrinks the target chain, before any byte of the new slot can land.
            (location, committedSequence) = DedicatedTestTask.Run(() =>
            {
                using var transaction = storage.BeginTransaction();
                var inserted = storage.Insert(transaction, owner: 9, Encoding.UTF8.GetBytes("concurrent commit"));
                transaction.Commit();
                return (inserted, transaction.Sequence);
            }).WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            commits++;
        });
        point.CrashWhen = (stream, operation, offset, _) => stream == "data" && operation == "Write" && offset == slotOffset;

        // Act: no slot sector lands, so recovery must still protect its older anchor chain.
        SimulatedPowerLossException.ShouldBeThrownBy(() => storage.FlushHeader());
        using var reopened = TornStorage.Open(storage.CaptureDurable());

        // Assert: the acknowledged page must be replayed, and a later header must not overwrite it.
        commits.ShouldBe(1);
        point.HasCrashed.ShouldBeTrue();
        reopened.Log.ReadAll().ShouldContain(record => record.Type == JournalRecordType.CommitTransaction
            && record.TransactionSequence == committedSequence);
        reopened.Read(location.PageId, location.SlotIndex).ShouldBe("concurrent commit");
        location.PageId.ShouldNotBe((PageId)retired);
        storage.FreeSpaceMap.IsAllocated((PageId)retired).ShouldBeTrue();
        reopened.Checkpoint();
        using var reopenedAgain = TornStorage.Open(reopened.CaptureDurable());
        reopenedAgain.Read(location.PageId, location.SlotIndex).ShouldBe("concurrent commit");
    }
}
