using System;
using System.Threading;
using System.Threading.Tasks;
using Assimalign.Cohesion.Database.Storage.Tests.TestObjects;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Storage.Tests;

[Collection(StorageRaceCollection.Name)]
public sealed partial class StorageFreeBytesTests
{
    [Theory(DisplayName = "Cohesion Test [Storage] - Free bytes: reuse after candidate selection cannot grant a stale writer the replacement page (#1288)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InsertRecord_CandidateReusedByTheSameOwner_ShouldRejectItsOldGeneration(bool replacementCommitted)
    {
        using var storage = TornStorage.Create();
        var target = Insert(storage, Filled(2000, 0x11));
        var current = Insert(storage, Filled(6500, 0x33));
        using (var freeing = storage.BeginTransaction())
        {
            storage.Delete(freeing, current.PageId, current.SlotIndex);
            freeing.Commit();
        }
        // Consume that free page under another owner, leaving owner 7 without a current target.
        using (var other = storage.BeginTransaction())
        {
            storage.Insert(other, 99, Filled(7000, 0x55)).PageId.ShouldBe(current.PageId);
            other.Commit();
        }
        using var stale = storage.BeginTransaction();
        using var replacement = storage.BeginTransaction();
        // Thirty seconds is only a hang guard; the selection callback defines the interleaving.
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var resume = new ManualResetEventSlim();
        var selected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int selections = 0;
        storage.RecordPageSelectedForReuse = page =>
        {
            page.ShouldBe(target.PageId);
            storage.RecordPageSelectedForReuse = null;
            selections++;
            selected.SetResult();
            resume.Wait(stop.Token);
        };
        var writer = DedicatedTestTask.Run(() => storage.Insert(stale, Owner, Filled(2000, 0x77)));
        (PageId PageId, int SlotIndex) inserted = default;
        try
        {
            await selected.Task.WaitAsync(stop.Token);
            using (var freeing = storage.BeginTransaction())
            {
                storage.Delete(freeing, target.PageId, target.SlotIndex);
                freeing.Commit();
            }
            storage.Insert(replacement, Owner, [7]).PageId.ShouldBe(target.PageId);
            if (replacementCommitted) { replacement.Commit(); }
        }
        finally
        {
            storage.RecordPageSelectedForReuse = null;
            resume.Set();
            inserted = await writer.WaitAsync(TimeSpan.FromSeconds(30));
        }
        selections.ShouldBe(1);
        inserted.PageId.ShouldNotBe(target.PageId);
        stale.PreImages.ContainsKey((long)target.PageId).ShouldBeFalse();
        stale.Commit();
        storage.ReadBytes(target.PageId, 0).ShouldBe(new byte[] { 7 });
        if (!replacementCommitted) { replacement.Commit(); }
        using var update = storage.BeginTransaction();
        storage.Update(update, target.PageId, 0, new byte[] { 8 });
        update.Commit();
        storage.ReadBytes(target.PageId, 0).ShouldBe(new byte[] { 8 });
    }
}
