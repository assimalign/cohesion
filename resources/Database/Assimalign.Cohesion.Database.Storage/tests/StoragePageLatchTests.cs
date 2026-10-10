using System;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Storage.Tests.TestObjects;
using Assimalign.Cohesion.Database.Storage.Units;

namespace Assimalign.Cohesion.Database.Storage.Tests;

/// <summary>
/// A rollback restores each page it changed in place, clearing it and writing its pre-image back.
/// A structure whose readers take no page write lock (a B-tree) changes its pages under its
/// <see cref="StoragePageLatch"/>, which the storage enlists with the transaction, and the rollback
/// restores while it holds every enlisted latch exclusively (#1371). Each test here holds a latch
/// shared on the test thread, as a reader does, and rolls back on another.
/// </summary>
public sealed class StoragePageLatchTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    [Fact(DisplayName = "Cohesion Test [Storage] - Page latch: a rollback restores a latched page only once the latch's reader has left (#1371)")]
    public async Task Rollback_WhileAReaderHoldsTheLatch_ShouldRestoreOnlyAfterTheReaderLeaves()
    {
        // Arrange: a committed latched page holding 0x11s, changed to 0x22s by an open bracket.
        using var storage = TornStorage.Create();
        var latch = StoragePageLatch.Create();
        var pageId = CommitLatchedPage(storage, latch, 0x11);
        var transaction = storage.BeginTransaction();
        Write(storage, transaction, latch, pageId, 0x22);

        // Act: the reader holds the latch while the rollback starts.
        var (heldByte, waited, completedWhileHeld, rollback) = RollBackWhileReading(storage, latch, transaction, pageId);
        await rollback.WaitAsync(Wait);

        // Assert: the reader saw the page as the bracket left it; the restore came after.
        waited.ShouldBeTrue("the rollback waits for the latch");
        completedWhileHeld.ShouldBeFalse();
        heldByte.ShouldBe((byte)0x22);
        Body(storage, pageId)[0].ShouldBe((byte)0x11);
        transaction.IsActive.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Page latch: two rollbacks that enlisted the same latches in opposite orders take them in creation order (#1371)")]
    public async Task Rollback_TwoTransactionsEnlistingLatchesInOppositeOrders_ShouldTakeThemInCreationOrder()
    {
        // Arrange: first is created before second. One bracket changes a page of each in the order
        // first, second; the other in the order second, first.
        using var storage = TornStorage.Create();
        var first = StoragePageLatch.Create();
        var second = StoragePageLatch.Create();
        var firstPages = (CommitLatchedPage(storage, first, 0x11), CommitLatchedPage(storage, first, 0x12));
        var secondPages = (CommitLatchedPage(storage, second, 0x21), CommitLatchedPage(storage, second, 0x22));
        var forward = storage.BeginTransaction();
        Write(storage, forward, first, firstPages.Item1, 0x31);
        Write(storage, forward, second, secondPages.Item1, 0x32);
        var backward = storage.BeginTransaction();
        Write(storage, backward, second, secondPages.Item2, 0x41);
        Write(storage, backward, first, firstPages.Item2, 0x42);

        // Act: a reader holds first while both roll back. Taken in creation order, both wait for
        // first and neither holds second; taken in enlistment order, the backward rollback would
        // hold second while it waits, and could then deadlock with the forward one.
        var (bothWaited, secondFree, rollbacks) = RollBackBothWhileReading(first, second, forward, backward);
        await Task.WhenAll(rollbacks).WaitAsync(Wait);

        // Assert
        bothWaited.ShouldBeTrue("both rollbacks wait for the first latch");
        secondFree.ShouldBeTrue("no rollback holds the second latch while it waits for the first");
        Body(storage, firstPages.Item1)[0].ShouldBe((byte)0x11);
        Body(storage, firstPages.Item2)[0].ShouldBe((byte)0x12);
        Body(storage, secondPages.Item1)[0].ShouldBe((byte)0x21);
        Body(storage, secondPages.Item2)[0].ShouldBe((byte)0x22);
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Page latch: a rollback on the thread that holds the latch exclusively restores without waiting for itself (#1371)")]
    public void Rollback_OnTheThreadHoldingTheLatchExclusively_ShouldRestoreAndKeepTheHold()
    {
        // Arrange
        using var storage = TornStorage.Create();
        var latch = StoragePageLatch.Create();
        var pageId = CommitLatchedPage(storage, latch, 0x11);
        var transaction = storage.BeginTransaction();

        // Act
        latch.EnterWrite();
        bool heldAfter;
        try
        {
            using (var handle = storage.OpenPageForWrite(transaction, pageId, latch))
            {
                handle.Page.AsBodySpan()[..64].Fill(0x22);
                handle.MarkDirty();
            }

            transaction.Rollback();
            heldAfter = latch.IsWriteHeld;
        }
        finally
        {
            latch.ExitWrite();
        }

        // Assert
        heldAfter.ShouldBeTrue();
        Body(storage, pageId)[0].ShouldBe((byte)0x11);
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Page latch: a latched page write without the latch held exclusively is refused and touches nothing (#1371)")]
    public void OpenAndAllocatePageForWrite_WithoutTheLatchHeldExclusively_ShouldBeRefused()
    {
        // Arrange
        using var storage = TornStorage.Create();
        var latch = StoragePageLatch.Create();
        var pageId = CommitLatchedPage(storage, latch, 0x11);
        long pageCount = storage.PageManager.PageCount;
        using var transaction = storage.BeginTransaction();

        // Act
        var open = Should.Throw<InvalidOperationException>(() => storage.OpenPageForWrite(transaction, pageId, latch));
        latch.EnterRead();
        InvalidOperationException allocate;
        try
        {
            allocate = Should.Throw<InvalidOperationException>(() => storage.AllocatePageForWrite(transaction, PageType.Index, latch));
        }
        finally
        {
            latch.ExitRead();
        }

        // Assert: neither call write-locked, imaged or allocated a page, so the bracket still
        // takes the page under the latch and commits.
        open.Message.ShouldContain("without holding the structure's latch exclusively", Case.Sensitive);
        allocate.Message.ShouldContain("without holding the structure's latch exclusively", Case.Sensitive);
        transaction.PreImages.Count.ShouldBe(0);
        transaction.Latches.Count.ShouldBe(0);
        storage.PageManager.PageCount.ShouldBe(pageCount);
        Write(storage, transaction, latch, pageId, 0x22);
        transaction.Commit();
        Body(storage, pageId)[0].ShouldBe((byte)0x22);
    }

    /// <summary>
    /// Holds the latch shared, as a reader does, starts the rollback on another thread, waits until
    /// the rollback waits for the latch, and reads the page under the hold. Synchronous, so the hold
    /// is taken and released on one thread.
    /// </summary>
    private static (byte HeldByte, bool Waited, bool CompletedWhileHeld, Task Rollback) RollBackWhileReading(
        TornStorage storage, StoragePageLatch latch, StorageTransaction transaction, PageId pageId)
    {
        latch.EnterRead();
        try
        {
            var rollback = Task.Run(transaction.Rollback);
            bool waited = SpinWait.SpinUntil(() => latch.WaitingWriters == 1 || rollback.IsCompleted, Wait) && !rollback.IsCompleted;
            return (Body(storage, pageId)[0], waited, rollback.IsCompleted, rollback);
        }
        finally
        {
            latch.ExitRead();
        }
    }

    /// <summary>
    /// Holds <paramref name="first"/> shared while both transactions roll back, waits until both
    /// wait for it, and checks from another thread that neither holds <paramref name="second"/>.
    /// </summary>
    private static (bool BothWaited, bool SecondFree, Task[] Rollbacks) RollBackBothWhileReading(
        StoragePageLatch first, StoragePageLatch second, StorageTransaction forward, StorageTransaction backward)
    {
        first.EnterRead();
        try
        {
            Task[] rollbacks = [Task.Run(forward.Rollback), Task.Run(backward.Rollback)];
            bool bothWaited = SpinWait.SpinUntil(() => first.WaitingWriters == 2, Wait);
            var probe = Task.Run(() =>
            {
                second.EnterRead();
                second.ExitRead();
            });
            bool secondFree = SpinWait.SpinUntil(() => probe.IsCompleted, Wait);
            return (bothWaited, secondFree, rollbacks);
        }
        finally
        {
            first.ExitRead();
        }
    }

    /// <summary>Allocates a page under the latch, fills the start of its body and commits it.</summary>
    private static PageId CommitLatchedPage(TornStorage storage, StoragePageLatch latch, byte fill)
    {
        using var transaction = storage.BeginTransaction();
        PageId pageId;
        latch.EnterWrite();
        try
        {
            using var handle = storage.AllocatePageForWrite(transaction, PageType.Index, latch);
            handle.Page.AsBodySpan()[..64].Fill(fill);
            handle.MarkDirty();
            pageId = handle.Id;
        }
        finally
        {
            latch.ExitWrite();
        }

        transaction.Latches.ShouldBe([latch]);
        transaction.Commit();
        return pageId;
    }

    /// <summary>Fills the start of a latched page's body inside a transaction, under the latch.</summary>
    private static void Write(TornStorage storage, StorageTransaction transaction, StoragePageLatch latch, PageId pageId, byte fill)
    {
        latch.EnterWrite();
        try
        {
            using var handle = storage.OpenPageForWrite(transaction, pageId, latch);
            handle.Page.AsBodySpan()[..64].Fill(fill);
            handle.MarkDirty();
        }
        finally
        {
            latch.ExitWrite();
        }
    }

    private static byte[] Body(TornStorage storage, PageId pageId) => storage.PageBytes(pageId).AsSpan(Page.HeaderSize).ToArray();
}
