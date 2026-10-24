using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Storage.Internal;
using Assimalign.Cohesion.Database.Storage.Tests.TestObjects;
using Assimalign.Cohesion.Database.Storage.Units;
using Shouldly;
using Xunit;
using Xunit.Abstractions;

namespace Assimalign.Cohesion.Database.Storage.Tests;

[Collection(StorageRaceCollection.Name)]
public sealed class StorageReviewTests
{
    private readonly ITestOutputHelper _output;

    public StorageReviewTests(ITestOutputHelper output) => _output = output;

    [Fact(DisplayName = "Cohesion Test [Storage] - Writer rendezvous: repeat all read paths and serialized continuation")]
    public async Task OpenPageForWrite_RepeatedThreadChanges_ShouldExerciseEveryReadPath()
    {
        // Arrange
        var elapsed = Stopwatch.StartNew();
        var counts = new int[5];

        // Act
        do
        {
            elapsed.Elapsed.ShouldBeLessThan(TimeSpan.FromMinutes(2));
            await OpenPageForWrite_CrossThreadNestedScope_ShouldRejectBeforeAllReadPaths("direct"); counts[0]++;
            await OpenPageForWrite_CrossThreadNestedScope_ShouldRejectBeforeAllReadPaths("try"); counts[1]++;
            await OpenPageForWrite_CrossThreadNestedScope_ShouldRejectBeforeAllReadPaths("scan"); counts[2]++;
            await OpenPageForWrite_ContinuationThread_ShouldTransferItsReadPermission(true); counts[3]++;
            await OpenPageForWrite_ContinuationThread_ShouldTransferItsReadPermission(false); counts[4]++;
        }
        while (elapsed.Elapsed < TimeSpan.FromSeconds(1) || counts[0] < 5);

        // Assert
        counts.ShouldAllBe(count => count >= 5);
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Reclaim: completing an earlier write cannot clean later header changes")]
    public void DeleteRecord_EarlierImageCompletesBeforeReclaim_ShouldOweTheReclaimedImage()
    {
        // Arrange
        using var storage = TornStorage.Create();
        (PageId PageId, int SlotIndex) target;
        using (var seed = storage.BeginTransaction())
        {
            target = storage.Insert(seed, 7, new byte[64]);
            seed.Commit();
        }
        using var pin = storage.PageManager.GetPage(target.PageId);
        int completions = 0;
        storage.RecordPageReclaiming = page =>
        {
            page.ShouldBe(target.PageId);
            // Finish the clean-version publication of an already captured image.
            pin.Entry.MarkClean(Volatile.Read(ref pin.Entry.Version));
            completions++;
        };
        using var transaction = storage.BeginTransaction();

        // Act
        storage.Delete(transaction, target.PageId, target.SlotIndex);
        storage.RecordPageReclaiming = null;

        // Assert
        completions.ShouldBe(1);
        pin.Page.Type.ShouldBe(PageType.Reclaimed);
        pin.IsDirty.ShouldBeTrue();
        transaction.Commit();
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Reuse: a temporarily reserved identity keeps its capacity hint")]
    public void InsertRecord_CandidateTemporarilyReserved_ShouldRemainDiscoverableAfterPublication()
    {
        // Arrange: model the allocation publication window after directory registration.
        using var storage = TornStorage.Create();
        (PageId PageId, int SlotIndex) target;
        using (var seed = storage.BeginTransaction())
        {
            target = storage.Insert(seed, 7, new byte[2000]);
            storage.Insert(seed, 7, new byte[7000]);
            seed.Commit();
        }
        Reserved(storage.FreeSpaceMap).Add((long)target.PageId);
        try
        {
            using var insert = storage.BeginTransaction();
            storage.Insert(insert, 7, new byte[2000]).PageId.ShouldNotBe(target.PageId);
            // Fill the new current page, so the next insert needs an older candidate.
            storage.Insert(insert, 7, new byte[5000]);
            insert.Commit();
        }
        finally { storage.FreeSpaceMap.Publish(target.PageId); }

        // Act
        using var transaction = storage.BeginTransaction();
        var reused = storage.Insert(transaction, 7, new byte[2000]);

        // Assert
        reused.PageId.ShouldBe(target.PageId);
        transaction.Commit();
    }

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_reserved")]
    private static extern ref HashSet<long> Reserved(StorageFreeSpaceMap map);

    [Theory(DisplayName = "Cohesion Test [Storage] - Reopen: full candidates do not acquire write pre-images")]
    [InlineData(8)]
    [InlineData(32)]
    public void InsertRecord_AfterReopenAndRollback_ShouldTouchOnlyTheFittingPage(int count)
    {
        // Arrange
        using var seed = TornStorage.Create();
        seed.FillPages(count);
        seed.Checkpoint();
        using var storage = TornStorage.Open(seed.CaptureDurable());

        // Act / Assert: rollback invalidation must not restore the write amplification.
        for (int attempt = 0; attempt < 2; attempt++)
        {
            using var transaction = storage.BeginTransaction();
            long before = storage.Log.ReadAll().Last().Lsn;
            storage.Insert(transaction, 7, new byte[7000]);
            int images = storage.Log.ReadAll().Count(record => record.Lsn > before && record.Type == JournalRecordType.FullPageImage);
            int touched = transaction.PreImages.Count;
            transaction.Rollback();
            _output.WriteLine($"Full pages={count}; attempt={attempt}; pre-images={touched}; full-page images={images}.");
            touched.ShouldBeLessThanOrEqualTo(2);
            images.ShouldBeLessThanOrEqualTo(2);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Reuse: a rival bracket's candidate is skipped")]
    public void InsertRecord_CandidateLockedByAnotherBracket_ShouldSkipWithoutTouchingIt()
    {
        // Arrange
        using var storage = TornStorage.Create();
        (PageId PageId, int SlotIndex) target;
        using (var seed = storage.BeginTransaction())
        {
            target = storage.Insert(seed, 7, new byte[2000]);
            storage.Insert(seed, 7, new byte[7000]);
            seed.Commit();
        }
        using var rival = storage.BeginTransaction();
        using var writer = storage.OpenPageForWrite(rival, target.PageId);
        using var transaction = storage.BeginTransaction();

        // Act
        var inserted = storage.Insert(transaction, 7, new byte[2000]);

        // Assert
        inserted.PageId.ShouldNotBe(target.PageId);
        transaction.PreImages.ContainsKey((long)target.PageId).ShouldBeFalse();
        transaction.Commit();
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Compaction: throwing notification cannot leave the target absent")]
    public void UpdateRecord_CompactionNotificationThrows_ShouldKeepTheReplacementVisible()
    {
        // Arrange
        using var storage = TornStorage.Create();
        (PageId PageId, int SlotIndex) target;
        using (var seed = storage.BeginTransaction())
        {
            target = storage.Insert(seed, 7, new byte[1000]);
            storage.Insert(seed, 7, new byte[5800]);
            seed.Commit();
        }
        using var transaction = storage.BeginTransaction();
        storage.RecordPageCompacted = _ => throw new InvalidOperationException("notification");
        byte[] replacement = Enumerable.Repeat((byte)0x77, 2000).ToArray();

        // Act
        Should.Throw<InvalidOperationException>(() => storage.Update(transaction, target.PageId, target.SlotIndex, replacement));
        storage.RecordPageCompacted = null;

        // Assert: before rollback, the row still exists with complete bytes.
        storage.TryReadRecord(target.PageId, target.SlotIndex, 7, out var bytes).ShouldBeTrue();
        bytes.ToArray().ShouldBe(replacement);
        transaction.Rollback();
        storage.ReadBytes(target.PageId, target.SlotIndex).Length.ShouldBe(1000);
    }

    [Theory(DisplayName = "Cohesion Test [Storage] - Mutable scopes: a nested writer on another thread is refused before mutation")]
    [InlineData("direct")]
    [InlineData("try")]
    [InlineData("scan")]
    public async Task OpenPageForWrite_CrossThreadNestedScope_ShouldRejectBeforeAllReadPaths(string kind)
    {
        // Arrange
        using var storage = TornStorage.Create();
        (PageId PageId, int SlotIndex) target;
        using (var seed = storage.BeginTransaction())
        {
            target = storage.Insert(seed, 7, Enumerable.Repeat((byte)0x11, 64).ToArray());
            seed.Commit();
        }
        using var transaction = storage.BeginTransaction();
        using var outer = storage.OpenPageForWrite(transaction, target.PageId);
        using var entered = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        Exception? failure = null;
        var nested = DedicatedTestTask.Run(() =>
        {
            try
            {
                using var writer = storage.OpenPageForWrite(transaction, target.PageId);
                writer.Page.AsBodySpan().Slice(0, 32).Fill(0x22);
                entered.Set();
                if (!resume.Wait(TimeSpan.FromSeconds(30))) { throw new TimeoutException(); }
                writer.Page.AsBodySpan().Slice(32, 32).Fill(0x22);
                writer.MarkDirty();
            }
            catch (Exception exception) { failure = exception; }
            finally { entered.Set(); }
        });
        byte[] bytes;
        try
        {
            entered.Wait(TimeSpan.FromSeconds(30)).ShouldBeTrue();
            // Act: the outer thread must never accept the nested writer's odd sequence.
            if (kind == "direct") { bytes = storage.ReadBytes(target.PageId, target.SlotIndex); }
            else if (kind == "try")
            {
                storage.TryReadRecord(target.PageId, target.SlotIndex, 7, out var data).ShouldBeTrue();
                bytes = data.ToArray();
            }
            else { bytes = storage.ScanOwner(7).Single(); }
        }
        finally { resume.Set(); await nested.WaitAsync(TimeSpan.FromSeconds(30)); }

        // Assert
        _output.WriteLine($"Read {kind}: first={bytes[0]:X2}; last={bytes[^1]:X2}; nested failure={failure?.GetType().Name ?? "none"}.");
        bytes.ShouldAllBe(value => value == 0x11);
        failure.ShouldBeOfType<StorageTransactionException>();
    }

    [Theory(DisplayName = "Cohesion Test [Storage] - Mutable scopes: a serialized continuation can read through its handle scope")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OpenPageForWrite_ContinuationThread_ShouldTransferItsReadPermission(bool accessPage)
    {
        // Arrange
        using var storage = TornStorage.Create();
        (PageId PageId, int SlotIndex) target;
        using (var seed = storage.BeginTransaction())
        {
            target = storage.Insert(seed, 7, new byte[64]);
            seed.Commit();
        }
        using var transaction = storage.BeginTransaction();
        using var writer = storage.OpenPageForWrite(transaction, target.PageId);
        Task? continuation = null;
        try
        {
            // Act: guaranteed different thread, as after an await that moves the handle.
            continuation = DedicatedTestTask.Run(() =>
            {
                if (accessPage)
                {
                    writer.Page.AsBodySpan().Slice(0, 64).Fill(0x22);
                    writer.MarkDirty();
                }
                storage.ReadBytes(target.PageId, target.SlotIndex).ShouldAllBe(value => value == (accessPage ? 0x22 : 0));
            });
            await continuation.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            writer.Dispose();
            if (continuation is { IsCompleted: false }) { await continuation.WaitAsync(TimeSpan.FromSeconds(30)); }
        }

        // Assert
        transaction.Commit();
        storage.ReadBytes(target.PageId, target.SlotIndex).ShouldAllBe(value => value == (accessPage ? 0x22 : 0));
    }
}
