using System;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Storage.Tests.TestObjects;
using Assimalign.Cohesion.Database.Storage.Units;

namespace Assimalign.Cohesion.Database.Storage.Tests;

[Collection(StorageRaceCollection.Name)]
public sealed class StorageTransactionOwnershipTests
{
    [Theory(DisplayName = "Cohesion Test [Storage] - Ownership: every mutation refuses a foreign transaction even with the same sequence")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void Mutate_ForeignTransaction_ShouldRefuseWithoutTouchingEitherStorage(int operation)
    {
        // Arrange: B already holds its row under the same sequence A uses.
        using var first = TornStorage.Create(name: "first");
        using var second = TornStorage.Create(name: "second");
        first.Insert("first row");
        var row = second.Insert("second row");
        using var foreign = first.BeginTransaction();
        using var local = second.BeginTransaction();
        foreign.Sequence.ShouldBe(local.Sequence);
        using (second.OpenPageForWrite(local, row.PageId)) { }
        byte[] before = second.PageBytes(row.PageId);
        long length = second.JournalLength;
        long lastLsn = second.Log.LastLsn;
        long pages = second.PageManager.PageCount;
        long preImages = second.PreImageBytes;

        // Act
        var refusal = Should.Throw<StorageTransactionException>(() => Mutate(second, foreign, row.PageId, operation));

        // Assert
        refusal.Code.ShouldBe(StorageTransactionErrorCode.ForeignStorage);
        refusal.Message.ShouldContain("first", Case.Sensitive);
        refusal.Message.ShouldContain("second", Case.Sensitive);
        foreign.PreImages.Count.ShouldBe(0);
        local.PreImages.Count.ShouldBe(1);
        second.PreImageBytes.ShouldBe(preImages);
        second.PageManager.PageCount.ShouldBe(pages);
        second.PageBytes(row.PageId).ShouldBe(before);
        second.IsPageWriteLocked(row.PageId).ShouldBeTrue();
        second.JournalLength.ShouldBe(length);
        second.Log.LastLsn.ShouldBe(lastLsn);
        second.ActiveTransactions.ShouldBe(1);
        first.ActiveTransactions.ShouldBe(1);
        first.PreImageBytes.ShouldBe(0);

        // Completion routes only to A; none of B's state belongs to the refused bracket.
        foreign.Commit();
        second.PageBytes(row.PageId).ShouldBe(before);
        second.Log.LastLsn.ShouldBe(lastLsn);
        second.IsPageWriteLocked(row.PageId).ShouldBeTrue();
        second.Update(local, row.PageId, row.SlotIndex, "local row");
        local.Rollback();
        second.IsPageWriteLocked(row.PageId).ShouldBeFalse();
        second.Read(row.PageId, row.SlotIndex).ShouldBe("second row");
        second.PreImageBytes.ShouldBe(0);
        second.BufferPool.CheckInvariants();
    }

    [Theory(DisplayName = "Cohesion Test [Storage] - Ownership: refusal on an unlocked page leaves no foreign locks or pre-images")]
    [InlineData(false)]
    [InlineData(true)]
    public void OpenPageForWrite_ForeignTransactionCompletion_ShouldLeaveTargetUsable(bool commit)
    {
        // Arrange
        using var first = TornStorage.Create(name: "first");
        using var second = TornStorage.Create(name: "second");
        var row = second.Insert("second row");
        using var foreign = first.BeginTransaction();
        long lastLsn = second.Log.LastLsn;

        // Act
        Should.Throw<StorageTransactionException>(() => second.OpenPageForWrite(foreign, row.PageId));
        if (commit) { foreign.Commit(); } else { foreign.Rollback(); }

        // Assert
        second.Log.LastLsn.ShouldBe(lastLsn);
        foreign.PreImages.Count.ShouldBe(0);
        second.IsPageWriteLocked(row.PageId).ShouldBeFalse();
        second.PreImageBytes.ShouldBe(0);
        using var local = second.BeginTransaction();
        second.Update(local, row.PageId, row.SlotIndex, "local row");
        local.Commit();
        second.Read(row.PageId, row.SlotIndex).ShouldBe("local row");
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Ownership: foreign refusals race local commit, rollback and disposal without leaking locks")]
    public async Task Mutate_ForeignRefusalsBesideLocalWriter_ShouldLeaveNoStateBehind()
    {
        // Arrange
        using var first = TornStorage.Create(name: "first");
        using var second = TornStorage.Create(name: "second");
        var row = second.Insert("second row");
        using var foreign = first.BeginTransaction();
        using var stop = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        long writes = 0;
        long refusals = 0;
        long lockedRefusals = 0;
        var elapsed = Stopwatch.StartNew();

        // Act
        var writer = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                using var local = second.BeginTransaction();
                second.Update(local, row.PageId, row.SlotIndex, "local row!");
                switch (writes % 3)
                {
                    case 0: local.Commit(); break;
                    case 1: local.Rollback(); break;
                    // The third iteration rolls back through Dispose.
                }
                Interlocked.Increment(ref writes);
            }
        }, CancellationToken.None);
        try
        {
            do
            {
                // The timeout is only a hang guard; coverage is decided by milestones below.
                stop.Token.ThrowIfCancellationRequested();
                for (int operation = 0; operation < 9; operation++)
                {
                    if (second.IsPageWriteLocked(row.PageId)) { lockedRefusals++; }
                    Should.Throw<StorageTransactionException>(() => Mutate(second, foreign, row.PageId, operation));
                    Interlocked.Increment(ref refusals);
                }
                await Task.Yield();
            }
            while (elapsed.Elapsed < TimeSpan.FromSeconds(1)
                || Interlocked.Read(ref writes) < 100 || refusals < 100 || lockedRefusals < 20);
        }
        finally
        {
            stop.Cancel();
            await writer.WaitAsync(TimeSpan.FromMinutes(5));
        }

        // Assert
        writes.ShouldBeGreaterThanOrEqualTo(100);
        refusals.ShouldBeGreaterThanOrEqualTo(100);
        lockedRefusals.ShouldBeGreaterThanOrEqualTo(20);
        foreign.PreImages.Count.ShouldBe(0);
        second.PreImageBytes.ShouldBe(0);
        second.ActiveTransactions.ShouldBe(0);
        second.IsPageWriteLocked(row.PageId).ShouldBeFalse();
        second.BufferPool.CheckInvariants();
        second.Read(row.PageId, row.SlotIndex).ShouldBe("local row!");
    }

    private static void Mutate(TornStorage storage, StorageTransaction transaction, PageId pageId, int operation)
    {
        switch (operation)
        {
            case 0: storage.Insert(transaction, "foreign"); break;
            case 1: storage.Insert(transaction, 71, Encoding.UTF8.GetBytes("foreign")); break;
            case 2: storage.Update(transaction, pageId, 0, "foreign"); break;
            case 3: storage.Delete(transaction, pageId, 0); break;
            case 4: using (storage.OpenPageForWrite(transaction, pageId)) { } break;
            case 5: using (storage.AllocatePageForWrite(transaction, PageType.Index)) { } break;
            case 6: storage.FreeOwnerPages(transaction, 0); break;
            // Intentionally no write hold: ownership must be checked before latch enlistment too.
            case 7: using (storage.OpenPageForWrite(transaction, pageId, StoragePageLatch.Create())) { } break;
            case 8: using (storage.AllocatePageForWrite(transaction, PageType.Index, StoragePageLatch.Create())) { } break;
            default: throw new ArgumentOutOfRangeException(nameof(operation));
        }
    }
}
