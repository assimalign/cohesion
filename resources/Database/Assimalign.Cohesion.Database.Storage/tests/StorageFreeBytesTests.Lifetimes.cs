using Assimalign.Cohesion.Database.Storage.Tests.TestObjects;
using Assimalign.Cohesion.Database.Storage.Units;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Storage.Tests;

public sealed partial class StorageFreeBytesTests
{
    [Fact(DisplayName = "Cohesion Test [Storage] - Free bytes: shrinking an older record increases its reusable capacity (#1288)")]
    public void UpdateRecord_ShrinkingOlderRecord_ShouldAdvertiseItsNewCapacity()
    {
        using var storage = TornStorage.Create();
        var target = Insert(storage, Filled(6000, 0x11));
        Insert(storage, Filled(7000, 0x33));
        using (var bracket = storage.BeginTransaction())
        {
            storage.Update(bracket, target.PageId, target.SlotIndex, Filled(1000, 0x55));
            bracket.Commit();
        }
        Insert(storage, Filled(4000, 0x77)).PageId.ShouldBe(target.PageId);
        storage.ReadBytes(target.PageId, target.SlotIndex).ShouldBe(Filled(1000, 0x55));
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Free bytes: reopen rebuilds older fragmented candidates from recovered pages (#1288)")]
    public void OpenExisting_DeletedOlderSpace_ShouldBeReusable()
    {
        using var storage = TornStorage.Create();
        var deleted = Insert(storage, Filled(4000, 0x11));
        var kept = Insert(storage, Filled(3000, 0x33));
        Insert(storage, Filled(7000, 0x55));
        using (var purge = storage.BeginTransaction())
        {
            storage.Delete(purge, deleted.PageId, deleted.SlotIndex);
            purge.Commit();
        }
        using var recovered = TornStorage.Open(storage.CaptureDurable());
        var next = Insert(recovered, Filled(1500, 0x77));
        next.PageId.ShouldBe(kept.PageId);
        recovered.ReadBytes(kept.PageId, kept.SlotIndex).ShouldBe(Filled(3000, 0x33));
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Free bytes: rollback makes restored older-page capacity discoverable (#1288)")]
    public void Rollback_GrowingOlderRecord_ShouldRestoreTheCapacityHint()
    {
        using var storage = TornStorage.Create();
        var target = Insert(storage, Filled(2000, 0x11));
        Insert(storage, Filled(6500, 0x33));
        using (var bracket = storage.BeginTransaction())
        {
            storage.Update(bracket, target.PageId, target.SlotIndex, Filled(5000, 0x55));
            bracket.Rollback();
        }
        var next = Insert(storage, Filled(4000, 0x77));
        next.PageId.ShouldBe(target.PageId);
        storage.ReadBytes(target.PageId, target.SlotIndex).ShouldBe(Filled(2000, 0x11));
    }

    [Theory(DisplayName = "Cohesion Test [Storage] - Free bytes: raw writable handles invalidate older-page capacity (#1288)")]
    [InlineData(false)]
    [InlineData(true)]
    public void OpenPageForWrite_RawShrink_ShouldMakeTheNewCapacityDiscoverable(bool latched)
    {
        using var storage = TornStorage.Create();
        var target = Insert(storage, Filled(6000, 0x11));
        Insert(storage, Filled(7000, 0x33));
        var latch = new StoragePageLatch();
        using (var bracket = storage.BeginTransaction())
        {
            if (latched)
            {
                latch.EnterWrite();
                try
                {
                    using var handle = storage.OpenPageForWrite(bracket, target.PageId, latch);
                    new SlottedPage(handle.Page).UpdateSlot(target.SlotIndex, Filled(1000, 0x55));
                    handle.MarkDirty();
                }
                finally { latch.ExitWrite(); }
            }
            else
            {
                using var handle = storage.OpenPageForWrite(bracket, target.PageId);
                new SlottedPage(handle.Page).UpdateSlot(target.SlotIndex, Filled(1000, 0x55));
                handle.MarkDirty();
            }
            bracket.Commit();
        }
        Insert(storage, Filled(4000, 0x77)).PageId.ShouldBe(target.PageId);
        storage.ReadBytes(target.PageId, target.SlotIndex).ShouldBe(Filled(1000, 0x55));
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Free bytes: a nested probe cannot make an outer writer's future geometry stale (#1288)")]
    public void InsertRecord_NestedRawWriter_ShouldLeaveCapacityUnmeasuredUntilTheOuterWriterCloses()
    {
        using var storage = TornStorage.Create();
        var target = Insert(storage, Filled(6000, 0x11));
        var kept = Insert(storage, Filled(1000, 0x33));
        kept.PageId.ShouldBe(target.PageId);
        Insert(storage, Filled(7000, 0x55));
        using (var bracket = storage.BeginTransaction())
        {
            using (var handle = storage.OpenPageForWrite(bracket, target.PageId))
            {
                storage.Insert(bracket, Owner, Filled(1500, 0x66)).PageId.ShouldNotBe(target.PageId);
                new SlottedPage(handle.Page).UpdateSlot(target.SlotIndex, Filled(1000, 0x22));
                handle.MarkDirty();
            }
            bracket.Commit();
        }
        Insert(storage, Filled(5000, 0x77));
        Insert(storage, Filled(4000, 0x88)).PageId.ShouldBe(target.PageId);
        storage.ReadBytes(kept.PageId, kept.SlotIndex).ShouldBe(Filled(1000, 0x33));
    }
}
