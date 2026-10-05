using System;
using System.IO;
using System.Threading;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Storage.Tests.TestObjects;
using Assimalign.Cohesion.Database.Storage.Units;

namespace Assimalign.Cohesion.Database.Storage.Tests;

/// <summary>
/// The journal-size checkpoint trigger with its time backstop, and the buffer pool's capacity
/// (#1254). PostgreSQL requests a checkpoint once the WAL since the last redo point passes its
/// share of <c>max_wal_size</c> (<c>XLogCheckpointNeeded</c>, <c>xlog.c:2358-2367</c>) and forces one
/// after <c>checkpoint_timeout</c> (<c>checkpointer.c:405-412</c>).
/// </summary>
public sealed class StorageCheckpointTriggerTests
{
    [Fact(DisplayName = "Cohesion Test [Storage] - Checkpoint trigger: the journal length counts verified frames and restarts at a checkpoint")]
    public void JournalLength_AppendsCheckpointsAndReopen_ShouldMatchTheJournalBytes()
    {
        // Arrange
        var storage = TornStorage.Create(journalWriteThrough: true);
        long empty = storage.JournalLength;

        // Act
        storage.Insert("one");
        storage.Insert("two");
        long afterInserts = storage.JournalLength;
        long journalBytes = storage.CaptureLive().Journal.Length;
        storage.Checkpoint();
        long afterCheckpoint = storage.JournalLength;
        long checkpointBytes = storage.CaptureLive().Journal.Length;
        storage.Insert("three");
        var images = storage.CaptureDurable();
        using var reopened = TornStorage.Open(images);

        // Assert: every count is the journal's own byte length.
        empty.ShouldBe(0);
        afterInserts.ShouldBe(journalBytes);
        afterInserts.ShouldBeGreaterThan(afterCheckpoint);
        afterCheckpoint.ShouldBe(checkpointBytes);
        afterCheckpoint.ShouldBeLessThan(100);
        reopened.JournalLength.ShouldBe(images.Journal.Length);
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Checkpoint trigger: crossing the size asks for one checkpoint per cycle")]
    public void OnCheckpointNeeded_JournalCrossesTheSize_ShouldBeInvokedOncePerCheckpointCycle()
    {
        // Arrange: a 2,000-byte row per committed single-page bracket, whose page delta carries
        // it (#1253: a bracket no longer journals two 8 KiB images of its page).
        var storage = TornStorage.Create();
        int signals = 0;
        storage.CheckpointJournalSize = 64 * 1024;
        storage.OnCheckpointNeeded = () => Interlocked.Increment(ref signals);

        // Act
        int inserts = 0;
        while (Volatile.Read(ref signals) == 0)
        {
            storage.Insert(Row("row " + inserts++));
            inserts.ShouldBeLessThan(100);
        }

        bool dueAtTheSignal = storage.IsCheckpointDue(TimeSpan.FromHours(1));
        for (int i = 0; i < 10; i++)
        {
            storage.Insert(Row("past the size " + i));
        }

        int signalsBeforeTheCheckpoint = Volatile.Read(ref signals);
        storage.Checkpoint();
        bool dueAfterTheCheckpoint = storage.IsCheckpointDue(TimeSpan.FromHours(1));
        while (Volatile.Read(ref signals) == signalsBeforeTheCheckpoint)
        {
            storage.Insert(Row("next cycle " + inserts++));
            inserts.ShouldBeLessThan(200);
        }

        // Assert
        dueAtTheSignal.ShouldBeTrue();
        signalsBeforeTheCheckpoint.ShouldBe(1);
        dueAfterTheCheckpoint.ShouldBeFalse();
        Volatile.Read(ref signals).ShouldBe(2);
        storage.JournalLength.ShouldBeGreaterThanOrEqualTo(storage.CheckpointJournalSize);
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Checkpoint trigger: the time backstop skips an idle journal")]
    public void IsCheckpointDue_TimeBackstop_ShouldRequireJournalActivity()
    {
        // Arrange
        var storage = TornStorage.Create();
        storage.Checkpoint();

        // Act
        bool idleDue = storage.IsCheckpointDue(TimeSpan.Zero);
        storage.Insert("activity");
        bool activeDue = storage.IsCheckpointDue(TimeSpan.Zero);
        bool activeDueLater = storage.IsCheckpointDue(TimeSpan.FromHours(1));
        storage.Checkpoint();
        bool dueAfterTheCheckpoint = storage.IsCheckpointDue(TimeSpan.Zero);

        // Assert
        idleDue.ShouldBeFalse();
        activeDue.ShouldBeTrue();
        activeDueLater.ShouldBeFalse();
        dueAfterTheCheckpoint.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Checkpoint trigger: an offline storage is never due and the size is validated")]
    public void IsCheckpointDue_OfflineOrInvalidSize_ShouldNotBeDueAndShouldRefuseNegativeSizes()
    {
        // Arrange
        var storage = TornStorage.Create(journalWriteThrough: false);
        storage.CheckpointJournalSize = 1;
        storage.Insert("over the size");
        bool dueOnline = storage.IsCheckpointDue(TimeSpan.Zero);
        var transaction = storage.BeginTransaction();
        storage.Insert(transaction, "unconfirmed");
        storage.JournalFaults.FailNextFlush();
        Should.Throw<StorageOfflineException>(() => transaction.Commit());

        // Act & Assert
        dueOnline.ShouldBeTrue();
        storage.IsCheckpointDue(TimeSpan.Zero).ShouldBeFalse();
        Should.Throw<ArgumentOutOfRangeException>(() => storage.CheckpointJournalSize = -1);
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Buffer pool: the default capacity is 4,096 pages (32 MiB)")]
    public void BufferPoolCapacity_Default_ShouldBe32MiB()
    {
        // Arrange
        using var storage = DefaultStorage.Create();

        // Act
        int capacity = storage.BufferPoolCapacity;

        // Assert
        capacity.ShouldBe(Storage.DefaultBufferPoolCapacity);
        ((long)capacity * Page.Size).ShouldBe(32L * 1024 * 1024);
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Buffer pool: shrinking writes dirty pages back through the gate and keeps every committed row")]
    public void BufferPoolCapacity_Shrink_ShouldEvictThroughTheWriteAheadGate()
    {
        // Arrange: 20 committed pages resident and dirty in a 64-page pool.
        var storage = TornStorage.Create(poolCapacity: 64);
        var pages = storage.FillPages(20);
        int residentBefore = storage.BufferPool.Count;

        // Act
        storage.BufferPoolCapacity = 8;
        int residentAfter = storage.BufferPool.Count;
        storage.BufferPoolCapacity = 128;
        int grown = storage.BufferPoolCapacity;
        var images = storage.CaptureDurable();
        using var reopened = TornStorage.Open(images, checkpointOnOpen: true);

        // Assert: the evicted pages reached the data file only after their journal records.
        residentBefore.ShouldBeGreaterThan(8);
        residentAfter.ShouldBeLessThanOrEqualTo(8);
        grown.ShouldBe(128);
        reopened.CountRecords(7).ShouldBe(pages.Length);
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - Buffer pool: a capacity below the pinned pages or below one page is refused")]
    public void BufferPoolCapacity_InvalidOrBelowThePinnedPages_ShouldBeRefusedAndLeaveThePoolUnchanged()
    {
        // Arrange
        var storage = TornStorage.Create(poolCapacity: 16);
        var pages = storage.FillPages(4);
        using var first = storage.PageManager.GetPage((PageId)pages[0]);
        using var second = storage.PageManager.GetPage((PageId)pages[1]);

        // Act
        var invalid = Should.Throw<ArgumentOutOfRangeException>(() => storage.BufferPoolCapacity = 0);
        var pinned = Should.Throw<StorageIOException>(() => storage.BufferPoolCapacity = 1);

        // Assert
        invalid.ParamName.ShouldBe("capacity");
        pinned.Message.ShouldContain("2 pages are pinned");
        storage.BufferPoolCapacity.ShouldBe(16);
    }

    /// <summary>A row padded to 2,000 bytes, so its page delta carries a measurable payload (#1253).</summary>
    private static string Row(string text) => text.PadRight(2000, '.');

    /// <summary>A storage built with the constructor's default pool capacity.</summary>
    private sealed class DefaultStorage : Storage
    {
        private DefaultStorage()
            : base(StorageModel.Custom, new StorageStream(new SimulatedDurableFileHandle()), new StorageStream(new SimulatedDurableFileHandle()), new StorageStream(new MemoryStream()))
        {
        }

        public static DefaultStorage Create()
        {
            var storage = new DefaultStorage();
            storage.InitializeNew((Name)"default-pool");
            return storage;
        }
    }
}
