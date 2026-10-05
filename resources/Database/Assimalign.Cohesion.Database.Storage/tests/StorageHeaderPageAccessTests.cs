using System;
using System.IO;
using System.Linq;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Storage.Internal;
using Assimalign.Cohesion.Database.Storage.Tests.TestObjects;
using Assimalign.Cohesion.Database.Storage.Units;

namespace Assimalign.Cohesion.Database.Storage.Tests;

/// <summary>
/// Page 0 is the file header (storage format 2). The storage rewrites its header slots in place,
/// outside the buffer pool, so a pooled copy of page 0 would be stale after the next header write,
/// and a write-back or a replayed journal image of one would roll both slots back. Nothing reaches
/// it as a data page: not a pin, an overwrite pin, a free, a transactional write, nor recovery.
/// </summary>
public sealed class StorageHeaderPageAccessTests
{
    [Fact(DisplayName = "Cohesion Test [Storage] - Page 0: the page manager refuses to pin the file header")]
    public void GetPage_HeaderPage_ShouldBeRefused()
    {
        // Arrange
        using var storage = TornStorage.Create();

        // Act
        var refusal = Should.Throw<StorageIOException>(() => storage.PageManager.GetPage((PageId)0L));

        // Assert
        refusal.Message.ShouldContain("Page 0 is the file header");
        storage.BufferPool.TryGet((PageId)0L, out _).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Page 0: the page manager refuses to pin the file header for an overwrite")]
    public void PinForOverwrite_HeaderPage_ShouldBeRefused()
    {
        // Arrange
        using var storage = TornStorage.Create();
        var manager = (StoragePageManager)storage.PageManager;

        // Act
        var refusal = Should.Throw<StorageIOException>(() => manager.PinForOverwrite((PageId)0L));

        // Assert
        refusal.Message.ShouldContain("Page 0 is the file header");
        storage.BufferPool.TryGet((PageId)0L, out _).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Page 0: the page manager refuses to free the file header, which stays reserved")]
    public void FreePage_HeaderPage_ShouldBeRefused()
    {
        // Arrange
        using var storage = TornStorage.Create();

        // Act
        var refusal = Should.Throw<StorageIOException>(() => storage.PageManager.FreePage((PageId)0L));
        using var allocated = storage.PageManager.AllocatePage(PageType.Data);

        // Assert
        refusal.Message.ShouldContain("Page 0 is the file header");
        storage.FreeSpaceMap.IsAllocated((PageId)0L).ShouldBeTrue();
        ((long)allocated.Id).ShouldBeGreaterThan(0L);
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Page 0: a transaction cannot open the file header for write, and stays usable")]
    public void OpenPageForWrite_HeaderPage_ShouldBeRefusedWithoutJournalingIt()
    {
        // Arrange
        var storage = TornStorage.Create();
        storage.Checkpoint([7, 8, 9]);
        int recordsBefore = storage.Log.ReadAll().Count;

        // Act
        StorageIOException refusal;
        using (var transaction = storage.BeginTransaction())
        {
            refusal = Should.Throw<StorageIOException>(() => storage.OpenPageForWrite(transaction, (PageId)0L));
            storage.Insert(transaction, "row");
            transaction.Commit();
        }

        var appended = storage.Log.ReadAll().Skip(recordsBefore).ToList();
        storage.Dispose();
        using var reopened = TornStorage.Open(storage.CaptureDurable());

        // Assert: the bracket journaled no image of page 0 and committed its own work.
        refusal.Message.ShouldContain("Page 0 is the file header");
        appended.ShouldNotContain(record => (long)record.PageId == 0L && record.Payload.Length == Page.Size);
        appended.ShouldContain(record => record.Type == JournalRecordType.CommitTransaction);
        reopened.ScanText().ShouldBe(["row"]);
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Page 0: a page manager over a fresh free-space map never hands out the file header")]
    public void AllocatePage_FreshFreeSpaceMap_ShouldNeverHandOutPageZero()
    {
        // Arrange
        using var stream = new StorageStream(new MemoryStream());
        var pool = new StorageBufferPool(4);
        var map = new StorageFreeSpaceMap();
        using var manager = new StoragePageManager(stream, pool, map);

        // Act
        using var first = manager.AllocatePage(PageType.Data);

        // Assert
        ((long)first.Id).ShouldBe(1L);
        map.IsAllocated((PageId)0L).ShouldBeTrue();
        pool.Dispose();
    }

    /// <summary>
    /// Nothing journals page 0, so an image of it in the journal is damage. Recovery reads page 0
    /// before it replays anything, so applying the image would not fail that open: it would roll
    /// both header slots back for the next one.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Storage] - Page 0: recovery never replays a journal image onto the file header")]
    public void Open_JournalHoldsACommittedImageOfPageZero_ShouldNotReplayIt()
    {
        // Arrange: a full page image and a committed image of page 0 full of garbage, then a power
        // loss. Recovery would restore the first whatever became of its transaction.
        var storage = TornStorage.Create(); // abandoned after its simulated power loss
        storage.Checkpoint([3]);
        var garbage = new byte[Page.Size];
        new Random(1251).NextBytes(garbage);
        storage.Log.AppendBegin(777);
        storage.Log.AppendPageImage(777, (PageId)0L, JournalRecordType.FullPageImage, garbage);
        storage.Log.AppendPageImage(777, (PageId)0L, JournalRecordType.CommittedPageImage, garbage);
        storage.Log.AppendCommit(777);
        var images = storage.CaptureDurable();
        var page0 = images.Data.AsSpan(0, Page.Size).ToArray();

        // Act
        byte[] afterRecovery;
        using (var reopened = TornStorage.Open(images))
        {
            afterRecovery = reopened.CaptureDurable().Data;
        }

        using var again = TornStorage.Open((afterRecovery, images.Journal));

        // Assert
        afterRecovery.AsSpan(0, Page.Size).SequenceEqual(page0).ShouldBeTrue();
        again.CheckpointActiveTransactions.ShouldBe([3L]);
    }
}
