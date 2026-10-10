using System;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Tracing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Storage.Internal;
using Assimalign.Cohesion.Database.Storage.Tests.TestObjects;
using Assimalign.Cohesion.Database.Storage.Units;

namespace Assimalign.Cohesion.Database.Storage.Tests;

/// <summary>
/// Serializes the tests that observe the storage event source: the source and its counters are
/// process-wide.
/// </summary>
[CollectionDefinition(nameof(StorageEventSourceCollection), DisableParallelization = true)]
public class StorageEventSourceCollection
{
}

/// <summary>
/// The storage kernel's event source against the repository's EventSource convention
/// (<c>.claude/rules/event-source.md</c>) and the catalog of the Database event-source plan
/// (§4.2): each lifecycle event once, with its declared payload, from a real storage over the
/// existing crash and fault-injection doubles; the counters; and the pin path's cost with nobody
/// listening.
/// </summary>
/// <remarks>
/// Every storage a test observes gets a name of its own, and the recorder reads only that name's
/// events, so a storage another test leaked cannot add to them. The collection runs alone, so the
/// process-wide counters move only by what the test does.
/// </remarks>
[Collection(nameof(StorageEventSourceCollection))]
public sealed class StorageEventSourceTests
{
    private readonly ITestOutputHelper _output;

    public StorageEventSourceTests(ITestOutputHelper output) => _output = output;

    [Fact(DisplayName = "Cohesion Test [Storage] - StorageEventSource: Should be named for its assembly")]
    public void GetName_StorageEventSource_ShouldEqualAssemblyName()
    {
        // Act
        string name = EventSource.GetName(typeof(StorageEventSource));

        // Assert
        name.ShouldBe(typeof(StorageEventSource).Assembly.GetName().Name);
        name.ShouldBe("Assimalign.Cohesion.Database.Storage");
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - StorageEventSource: Should generate a manifest in strict mode")]
    [UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode", Justification = "Test-only manifest validation reflects over the event source; test assemblies are not trimmed.")]
    [UnconditionalSuppressMessage("Trimming", "IL2111:DynamicallyAccessedMembers", Justification = "Test-only manifest validation reflects over the event source; test assemblies are not trimmed.")]
    public void GenerateManifest_StrictMode_ShouldSucceed()
    {
        // Act
        string? manifest = EventSource.GenerateManifest(typeof(StorageEventSource), assemblyPathToIncludeInManifest: null, EventManifestOptions.Strict);

        // Assert
        manifest.ShouldNotBeNull();
        manifest.ShouldContain("Assimalign.Cohesion.Database.Storage", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - StorageEventSource: Should report a storage's create, write-back, checkpoints and close once each")]
    public void Lifecycle_CreateWriteBackCheckpointClose_ShouldReportEachEventOnce()
    {
        // Arrange
        string name = UniqueName();
        using var recorder = new StorageEventRecorder(EventLevel.Verbose);
        long gauge = StorageEventSource.Log.CurrentStorages;
        long checkpoints = StorageEventSource.Log.Checkpoints;

        // Act: create, commit a row, write its page back, checkpoint, close (whose shutdown flush
        // checkpoints again).
        var storage = TornStorage.Create(name: name);
        long gaugeWhileOpen = StorageEventSource.Log.CurrentStorages;
        string storageId = storage.Id.ToString();
        long journalFlushes = StorageEventSource.Log.JournalFlushes;
        storage.Insert("v1");
        long journalFlushesForCommit = StorageEventSource.Log.JournalFlushes - journalFlushes;
        int writtenBack = storage.WriteBackDirtyPages(64);
        long journalLength = storage.JournalLength;
        storage.Checkpoint();
        storage.Dispose();
        storage.Dispose();

        // Assert
        recorder.Events.ShouldNotContain(e => e.EventId == 0, "EventSource reported an instrumentation error.");
        var events = recorder.For(name);
        events.Select(e => e.EventName).ShouldBe(
        [
            "StorageCreated",
            "PagesWrittenBack",
            "CheckpointStart",
            "CheckpointStop",
            "CheckpointStart",
            "CheckpointStop",
            "StorageClosed",
        ]);

        var created = events[0];
        created.EventId.ShouldBe(1);
        created.Level.ShouldBe(EventLevel.Informational);
        created.PayloadNames.ShouldBe(["database", "storageId", "model"]);
        created.Payload.ShouldBe([name, storageId, nameof(StorageModel.Custom)]);

        var writeBack = events[1];
        writeBack.EventId.ShouldBe(7);
        writeBack.Level.ShouldBe(EventLevel.Verbose);
        SourceKeywords(writeBack).ShouldBe(StorageEventSource.Keywords.WriteBack);
        writeBack.PayloadNames.ShouldBe(["database", "pages", "durationMilliseconds"]);
        writeBack.Payload![1].ShouldBe(writtenBack);
        writtenBack.ShouldBe(1);
        ((double)writeBack.Payload[2]!).ShouldBeGreaterThanOrEqualTo(0);

        var checkpointStart = events[2];
        checkpointStart.EventId.ShouldBe(5);
        checkpointStart.Opcode.ShouldBe(EventOpcode.Start);
        SourceKeywords(checkpointStart).ShouldBe(StorageEventSource.Keywords.Checkpoints);
        checkpointStart.PayloadNames.ShouldBe(["database", "activeTransactions", "journalLength"]);
        checkpointStart.Payload.ShouldBe([name, 0, journalLength]);

        var checkpointStop = events[3];
        checkpointStop.EventId.ShouldBe(6);
        checkpointStop.Opcode.ShouldBe(EventOpcode.Stop);
        checkpointStop.PayloadNames.ShouldBe(["database", "status", "checkpointLsn", "durationMilliseconds"]);
        checkpointStop.Payload![1].ShouldBe("Success");
        ((long)checkpointStop.Payload![2]!).ShouldBeGreaterThan(0);

        var closed = events[6];
        closed.EventId.ShouldBe(17);
        closed.Level.ShouldBe(EventLevel.Informational);
        closed.PayloadNames.ShouldBe(["database", "durationMilliseconds"]);

        gaugeWhileOpen.ShouldBe(gauge + 1);
        StorageEventSource.Log.CurrentStorages.ShouldBe(gauge);
        (StorageEventSource.Log.Checkpoints - checkpoints).ShouldBe(2);

        // A synchronous commit makes its record durable with one durable flush of the journal.
        journalFlushesForCommit.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - StorageEventSource: Should close a failed checkpoint's activity with a zero LSN")]
    public void Checkpoint_JournalFlushFails_ShouldWriteStopWithZeroLsn()
    {
        // Arrange: the fault-injection double fails the checkpoint's journal flush.
        string name = UniqueName();
        using var recorder = new StorageEventRecorder(EventLevel.Verbose);
        long gauge = StorageEventSource.Log.CurrentStorages;
        long checkpoints = StorageEventSource.Log.Checkpoints;
        var storage = TornStorage.Create(name: name);
        storage.Insert("v1");
        storage.JournalFaults.FailNextFlush();

        // Act
        Should.Throw<StorageOfflineException>(() => storage.Checkpoint());
        storage.Dispose();

        // Assert: the stop follows the offline transition the failure caused, once, with LSN zero;
        // the failed checkpoint is not counted.
        recorder.Events.ShouldNotContain(e => e.EventId == 0, "EventSource reported an instrumentation error.");
        var events = recorder.For(name);
        events.Select(e => e.EventName).ShouldBe(
            ["StorageCreated", "CheckpointStart", "StorageOffline", "CheckpointStop", "ShutdownFlushSkipped", "StorageClosed"]);

        var start = events[1];
        start.EventId.ShouldBe(5);
        start.Opcode.ShouldBe(EventOpcode.Start);

        var stop = events[3];
        stop.EventId.ShouldBe(6);
        stop.Opcode.ShouldBe(EventOpcode.Stop);
        stop.PayloadNames.ShouldBe(["database", "status", "checkpointLsn", "durationMilliseconds"]);
        stop.Payload![1].ShouldBe("Error");
        stop.Payload[2].ShouldBe(0L);

        (StorageEventSource.Log.Checkpoints - checkpoints).ShouldBe(0);
        StorageEventSource.Log.CurrentStorages.ShouldBe(gauge);
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - StorageEventSource: Should report the recovery of a reopened file set as one start and one stop")]
    public void Reopen_JournalWithRecords_ShouldReportRecoveryStartAndStopOnce()
    {
        // Arrange: a committed row whose page never reached the data file, then a crash; the file
        // set that created it is closed before anything is observed.
        string name = UniqueName();
        var created = TornStorage.Create(name: name);
        var (pageId, slot) = created.Insert("v1");
        var images = created.CaptureDurable();
        string storageId = created.Id.ToString();
        created.Dispose();
        using var recorder = new StorageEventRecorder(EventLevel.Verbose);
        long gauge = StorageEventSource.Log.CurrentStorages;

        // Act: reopen with the open-time checkpoint deferred, read, close.
        long redoLsn;
        string read;
        using (var reopened = TornStorage.Open(images))
        {
            redoLsn = reopened.RedoLsn;
            read = reopened.Read(pageId, slot);
        }

        // Assert
        recorder.Events.ShouldNotContain(e => e.EventId == 0, "EventSource reported an instrumentation error.");
        read.ShouldBe("v1");
        var events = recorder.For(name);
        events.Select(e => e.EventName).ShouldBe(["RecoveryStart", "RecoveryStop", "StorageClosed"]);

        var start = events[0];
        start.EventId.ShouldBe(2);
        start.Level.ShouldBe(EventLevel.Informational);
        start.Opcode.ShouldBe(EventOpcode.Start);
        start.PayloadNames.ShouldBe(["database", "storageId"]);
        start.Payload.ShouldBe([name, storageId]);

        var stop = events[1];
        stop.EventId.ShouldBe(3);
        stop.Opcode.ShouldBe(EventOpcode.Stop);
        stop.PayloadNames.ShouldBe(["database", "status", "rebuiltPages", "maxSequence", "redoLsn", "durationMilliseconds"]);
        stop.Payload![1].ShouldBe("Success");
        stop.Payload[2].ShouldBe(1);
        stop.Payload[3].ShouldBe(1L);
        stop.Payload[4].ShouldBe(redoLsn);

        StorageEventSource.Log.CurrentStorages.ShouldBe(gauge);
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - StorageEventSource: Should close a failed recovery's activity with zeros and count nothing open")]
    public void Reopen_RecoveryFails_ShouldWriteStopWithZeros()
    {
        // Arrange: the redo fixture of a committed delta whose full page image was cut out of the
        // journal, which recovery refuses as corruption.
        string name = UniqueName();
        var created = TornStorage.Create(name: name);
        created.Insert("v1");
        var images = created.CaptureDurable();
        created.Dispose();
        var image = JournalImage.Frames(images.Journal).Single(frame => frame.Type == JournalRecordType.FullPageImage);
        using var recorder = new StorageEventRecorder(EventLevel.Verbose);
        long gauge = StorageEventSource.Log.CurrentStorages;

        // Act
        Should.Throw<StorageCorruptionException>(() => TornStorage.Open((images.Data, JournalImage.Without(images.Journal, image))));

        // Assert: one start, one stop carrying zeros, and no close of a storage that never opened.
        recorder.Events.ShouldNotContain(e => e.EventId == 0, "EventSource reported an instrumentation error.");
        var events = recorder.For(name);
        events.Select(e => e.EventName).ShouldBe(["RecoveryStart", "RecoveryStop"]);

        var stop = events[1];
        stop.EventId.ShouldBe(3);
        stop.Opcode.ShouldBe(EventOpcode.Stop);
        stop.Payload![1].ShouldBe("Error");
        stop.Payload[2].ShouldBe(0);
        stop.Payload[3].ShouldBe(0L);
        stop.Payload[4].ShouldBe(0L);

        StorageEventSource.Log.CurrentStorages.ShouldBe(gauge);
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - StorageEventSource: Should report a page that outlived its journal records when the open moves the redo point")]
    public void Open_PageAboveTheDurableJournal_ShouldReportJournalTailLost()
    {
        // Arrange: the #1253 review fixture. A None-mode commit's page reaches the data file while
        // its journal records stay in the operating system's cache, then the power fails.
        string name = UniqueName();
        var storage = TornStorage.Create(journalWriteThrough: false, journalDurableFlushesOnly: true, durability: StorageCommitDurability.None, name: name);
        var (pageId, _) = storage.Insert("v1");
        storage.PageManager.FlushAll();
        long pageLsn = storage.PageLsn(pageId);
        var lost = storage.CaptureDurable();
        storage.Dispose();
        using var recorder = new StorageEventRecorder(EventLevel.Verbose);

        // Act
        long redoLsn;
        using (var reopened = TornStorage.Open(lost))
        {
            redoLsn = reopened.RedoLsn;
        }

        // Assert: the open moved the redo point from zero up to the stray page, once.
        recorder.Events.ShouldNotContain(e => e.EventId == 0, "EventSource reported an instrumentation error.");
        var events = recorder.For(name);
        events.Select(e => e.EventName).ShouldBe(["RecoveryStart", "JournalTailLost", "RecoveryStop", "StorageClosed"]);

        var tail = events[1];
        tail.EventId.ShouldBe(4);
        tail.Level.ShouldBe(EventLevel.Warning);
        tail.PayloadNames.ShouldBe(["database", "strayLsn", "redoLsn"]);
        tail.Payload.ShouldBe([name, pageLsn, 0L]);
        redoLsn.ShouldBe(pageLsn);
        events[2].Payload![4].ShouldBe(pageLsn);
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - StorageEventSource: Should report a grouped commit that missed its window and a flush worker's group flush")]
    public async Task GroupedCommit_WindowMissedAndWorkerFlush_ShouldReportEachOnce()
    {
        // Arrange
        string name = UniqueName();
        using var recorder = new StorageEventRecorder(EventLevel.Verbose);
        using var storage = TornStorage.Create(durability: StorageCommitDurability.Grouped, name: name);
        long selfFlushes = StorageEventSource.Log.GroupCommitSelfFlushes;

        // Act 1: a zero window, so the committer flushes inline at once.
        storage.GroupCommitWindow = TimeSpan.Zero;
        storage.Insert("inline");
        long inlineLsn = storage.Log.LastLsn;
        long selfFlushesAfterInline = StorageEventSource.Log.GroupCommitSelfFlushes;

        // Act 2: a window long enough that only the flush worker's pass can release the committer,
        // which runs on a thread of its own (GroupedCommitScenario explains why).
        storage.GroupCommitWindow = TimeSpan.FromSeconds(20);
        using var pending = new ManualResetEventSlim();
        storage.OnCommitPending = pending.Set;
        Task commit = Task.Factory.StartNew(() => storage.Insert("grouped"), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        WaitHandle.WaitAny([pending.WaitHandle, ((IAsyncResult)commit).AsyncWaitHandle], TimeSpan.FromSeconds(10));
        pending.IsSet.ShouldBeTrue("the grouped commit did not register on the gate within 10 s");
        storage.FlushPendingCommits().ShouldBeTrue();
        await commit.WaitAsync(TimeSpan.FromSeconds(10));
        long durableLsn = storage.Log.DurableLsn;

        // Assert
        recorder.Events.ShouldNotContain(e => e.EventId == 0, "EventSource reported an instrumentation error.");
        var events = recorder.For(name).Where(e => SourceKeywords(e) == StorageEventSource.Keywords.GroupCommit).ToArray();
        events.Select(e => e.EventName).ShouldBe(["GroupCommitWindowMissed", "PendingCommitsFlushed"]);

        var missed = events[0];
        missed.EventId.ShouldBe(9);
        missed.Level.ShouldBe(EventLevel.Verbose);
        missed.PayloadNames.ShouldBe(["database", "lsn", "windowMilliseconds"]);
        missed.Payload.ShouldBe([name, inlineLsn, 0d]);

        var flushed = events[1];
        flushed.EventId.ShouldBe(8);
        flushed.PayloadNames.ShouldBe(["database", "durableLsn"]);
        flushed.Payload.ShouldBe([name, durableLsn]);

        (selfFlushesAfterInline - selfFlushes).ShouldBe(1);
        (StorageEventSource.Log.GroupCommitSelfFlushes - selfFlushes).ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - StorageEventSource: Should report a missed window another flush covered without counting a self-flush")]
    public void AwaitDurable_WindowMissedAfterAnotherFlush_ShouldReportWithoutCountingASelfFlush()
    {
        // Arrange: a synchronous commit made its record durable, and a gate that never saw that
        // flush waits for the same LSN with a zero window: a committer whose window passed while
        // another committer's inline flush covered it.
        string name = UniqueName();
        using var storage = TornStorage.Create();
        storage.Insert("v1");
        long lsn = storage.Log.DurableLsn;
        var gate = new StorageGroupCommitGate { StorageName = name };
        using var recorder = new StorageEventRecorder(EventLevel.Verbose);
        long selfFlushes = StorageEventSource.Log.GroupCommitSelfFlushes;
        long journalFlushes = StorageEventSource.Log.JournalFlushes;

        // Act
        gate.AwaitDurable(lsn, TimeSpan.Zero, storage.Log);

        // Assert: the window was missed, but the committer's flush request reached no device.
        recorder.Events.ShouldNotContain(e => e.EventId == 0, "EventSource reported an instrumentation error.");
        var missed = recorder.For(name).ShouldHaveSingleItem();
        missed.EventName.ShouldBe("GroupCommitWindowMissed");
        missed.Payload.ShouldBe([name, lsn, 0d]);
        lsn.ShouldBeGreaterThan(0);
        (StorageEventSource.Log.GroupCommitSelfFlushes - selfFlushes).ShouldBe(0);
        (StorageEventSource.Log.JournalFlushes - journalFlushes).ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - StorageEventSource: Should report a storage a failing journal device took offline, its unconfirmed commit and its skipped shutdown flush")]
    public void Commit_JournalFlushFails_ShouldReportOfflineUnconfirmedCommitAndSkippedShutdownFlush()
    {
        // Arrange: the fault-injection double fails the durable flush of a commit's record.
        string name = UniqueName();
        using var recorder = new StorageEventRecorder(EventLevel.Verbose);
        long gauge = StorageEventSource.Log.CurrentStorages;
        var storage = TornStorage.Create(name: name);
        storage.Insert("kept");
        var transaction = storage.BeginTransaction();
        storage.Insert(transaction, "unconfirmed");
        storage.JournalFaults.FailNextFlush();

        // Act
        var error = Should.Throw<StorageOfflineException>(() => transaction.Commit());
        long commitLsn = transaction.CommitRecordLsn;
        transaction.Dispose();
        storage.Dispose();

        // Assert
        recorder.Events.ShouldNotContain(e => e.EventId == 0, "EventSource reported an instrumentation error.");
        var events = recorder.For(name);
        events.Select(e => e.EventName).ShouldBe(
            ["StorageCreated", "StorageOffline", "StorageCommitUnconfirmed", "ShutdownFlushSkipped", "StorageClosed"]);

        var offline = events[1];
        offline.EventId.ShouldBe(10);
        offline.Level.ShouldBe(EventLevel.Error);
        offline.PayloadNames.ShouldBe(["database", "cause", "exceptionType", "exceptionMessage"]);
        offline.Payload.ShouldBe([name, nameof(StorageOfflineCause.JournalFlush), typeof(IOException).FullName, error.InnerException!.Message]);

        var unconfirmed = events[2];
        unconfirmed.EventId.ShouldBe(11);
        unconfirmed.Level.ShouldBe(EventLevel.Error);
        unconfirmed.PayloadNames.ShouldBe(["database", "transactionSequence", "commitLsn", "exceptionMessage"]);
        // The same device failure's message as the offline event, so one query finds both.
        unconfirmed.Payload.ShouldBe([name, transaction.Sequence, commitLsn, error.InnerException!.Message]);
        commitLsn.ShouldBeGreaterThan(0);

        var skipped = events[3];
        skipped.EventId.ShouldBe(12);
        skipped.Level.ShouldBe(EventLevel.Warning);
        skipped.PayloadNames.ShouldBe(["database", "reason"]);
        skipped.Payload.ShouldBe([name, "Offline: " + nameof(StorageOfflineCause.JournalFlush)]);

        StorageEventSource.Log.CurrentStorages.ShouldBe(gauge);
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - StorageEventSource: Should report a buffer pool whose every page is pinned")]
    public void Pin_AllPagesPinnedAtCapacity_ShouldReportBufferPoolExhausted()
    {
        // Arrange
        string name = UniqueName();
        using var stream = StreamWithPages(3);
        using var pool = new StorageBufferPool(2) { StorageName = name };
        using var recorder = new StorageEventRecorder(EventLevel.Verbose);
        using var first = pool.Pin((PageId)0L, stream);
        using var second = pool.Pin((PageId)1L, stream);

        // Act
        Should.Throw<StorageIOException>(() => pool.Pin((PageId)2L, stream));

        // Assert
        recorder.Events.ShouldNotContain(e => e.EventId == 0, "EventSource reported an instrumentation error.");
        var exhausted = recorder.For(name).ShouldHaveSingleItem();
        exhausted.EventName.ShouldBe("BufferPoolExhausted");
        exhausted.EventId.ShouldBe(13);
        exhausted.Level.ShouldBe(EventLevel.Error);
        exhausted.PayloadNames.ShouldBe(["database", "capacity"]);
        exhausted.Payload.ShouldBe([name, 2]);
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - StorageEventSource: Should report a foreground eviction of a dirty page and a resize, and count the page I/O exactly")]
    public void Pin_DirtyPageEvictedThenResize_ShouldReportBothAndCountExactly()
    {
        // Arrange: a one-page pool whose only page is dirty.
        string name = UniqueName();
        using var stream = StreamWithPages(2);
        using var pool = new StorageBufferPool(1) { StorageName = name };
        using var recorder = new StorageEventRecorder(EventLevel.Verbose);
        long reads = StorageEventSource.Log.PageReads;
        long writes = StorageEventSource.Log.PageWrites;
        long foregroundWrites = StorageEventSource.Log.ForegroundPageWrites;
        using (var handle = pool.Pin((PageId)0L, stream))
        {
            var page = handle.Page;
            page.Lsn = 42;
            handle.MarkDirty();
        }

        // Act: the next page evicts the dirty one; the pool grows, then is set to the size it has.
        using (pool.Pin((PageId)1L, stream))
        {
        }

        pool.Resize(4, stream);
        pool.Resize(4, stream);

        // Assert
        recorder.Events.ShouldNotContain(e => e.EventId == 0, "EventSource reported an instrumentation error.");
        var events = recorder.For(name);
        events.Select(e => e.EventName).ShouldBe(["DirtyPageEvicted", "BufferPoolResized"]);

        var evicted = events[0];
        evicted.EventId.ShouldBe(14);
        evicted.Level.ShouldBe(EventLevel.Verbose);
        SourceKeywords(evicted).ShouldBe(StorageEventSource.Keywords.BufferPool);
        evicted.PayloadNames.ShouldBe(["database", "pageId", "pageLsn"]);
        evicted.Payload.ShouldBe([name, 0L, 42L]);

        var resized = events[1];
        resized.EventId.ShouldBe(15);
        resized.Level.ShouldBe(EventLevel.Informational);
        resized.PayloadNames.ShouldBe(["database", "oldCapacity", "newCapacity"]);
        resized.Payload.ShouldBe([name, 1, 4]);

        (StorageEventSource.Log.PageReads - reads).ShouldBe(2);
        (StorageEventSource.Log.PageWrites - writes).ShouldBe(1);
        (StorageEventSource.Log.ForegroundPageWrites - foregroundWrites).ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - StorageEventSource: Should report a page that failed its checksum, and no other corruption as one")]
    public unsafe void Pin_CorruptedChecksum_ShouldReportPageChecksumFailedOnly()
    {
        // Arrange: a stamped page with a flipped byte, and a stamped page whose header claims an
        // overflow area past its buffer (corrupt, but its checksum verifies).
        string name = UniqueName();
        var corruptedBytes = StampedPage(PageType.Data);
        corruptedBytes[Page.HeaderSize + 3] ^= 0xFF;
        var overflowBytes = new byte[Page.Size];
        fixed (byte* pointer = overflowBytes)
        {
            var page = new Page(pointer);
            page.Type = PageType.Data;
            page.Flags = PageFlags.Overflow;
            page.OverflowSize = Page.Size;
        }

        PageChecksum.Stamp(overflowBytes);
        using var corrupted = new StorageStream(new MemoryStream(corruptedBytes));
        using var overflowing = new StorageStream(new MemoryStream(overflowBytes));
        using var pool = new StorageBufferPool(2) { StorageName = name };
        using var recorder = new StorageEventRecorder(EventLevel.Verbose);

        // Act
        Should.Throw<StorageCorruptionException>(() => pool.Pin((PageId)0L, corrupted));
        Should.Throw<StorageCorruptionException>(() => pool.Pin((PageId)0L, overflowing));

        // Assert
        recorder.Events.ShouldNotContain(e => e.EventId == 0, "EventSource reported an instrumentation error.");
        var failed = recorder.For(name).ShouldHaveSingleItem();
        failed.EventName.ShouldBe("PageChecksumFailed");
        failed.EventId.ShouldBe(16);
        failed.Level.ShouldBe(EventLevel.Error);
        failed.PayloadNames.ShouldBe(["database", "pageId"]);
        failed.Payload.ShouldBe([name, 0L]);
        pool.Count.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - StorageEventSource: Should honor its keywords and levels")]
    public void Lifecycle_KeywordsAndLevels_ShouldFilterTheEvents()
    {
        // Arrange: one listener without the Checkpoints keyword, one at Warning.
        string name = UniqueName();
        using var withoutCheckpoints = new StorageEventRecorder(EventLevel.Verbose, StorageEventSource.Keywords.WriteBack);
        using var warnings = new StorageEventRecorder(EventLevel.Warning);

        // Act
        using (var storage = TornStorage.Create(name: name))
        {
            storage.Insert("v1");
            storage.WriteBackDirtyPages(64);
            storage.Checkpoint();
        }

        // Assert: events without a keyword are written whatever the keywords; nothing below Warning
        // reaches the second listener.
        withoutCheckpoints.For(name).Select(e => e.EventName).ShouldBe(["StorageCreated", "PagesWrittenBack", "StorageClosed"]);
        warnings.For(name).ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - StorageEventSource: Should publish every declared counter")]
    public async Task Counters_EnabledWithInterval_ShouldPublishEveryCounter()
    {
        // Arrange
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        string[] counters =
        [
            "current-storages",
            "checkpoints-per-second",
            "journal-flushes-per-second",
            "page-reads-per-second",
            "page-writes-per-second",
            "foreground-page-writes-per-second",
            "group-commit-self-flushes-per-second",
            "pre-images-spilled-per-second",
        ];

        // Act
        using var recorder = new StorageEventRecorder(EventLevel.LogAlways, counterInterval: TimeSpan.FromMilliseconds(100));

        // Assert
        await recorder.WaitForCountersAsync(counters, cancellation.Token);
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - StorageEventSource: Should count a pre-image spilled to the journal")]
    public void Touch_PreImageBudgetExceeded_ShouldCountTheSpill()
    {
        // Arrange: a budget of one byte, so every first touch spills.
        string name = UniqueName();
        using var storage = TornStorage.Create(name: name);
        storage.PreImageBudget = 1;
        long spilled = StorageEventSource.Log.PreImagesSpilled;
        long storageSpilled = storage.SpilledPreImages;

        // Act
        storage.Insert("spilled");

        // Assert: the process-wide counter moved exactly as the storage's own did.
        long delta = storage.SpilledPreImages - storageSpilled;
        delta.ShouldBeGreaterThan(0);
        (StorageEventSource.Log.PreImagesSpilled - spilled).ShouldBe(delta);
    }

    [Fact(DisplayName = "Cohesion Test [Storage] - StorageEventSource: Should allocate nothing but the handle on a pin hit while nobody listens")]
    public void Pin_HitWithoutListener_ShouldAllocateOnlyTheHandle()
    {
        // Arrange: a resident page, the hit path warmed up, and the size of one handle measured.
        const int Pins = 10_000;
        using var stream = StreamWithPages(1);
        using var pool = new StorageBufferPool(4) { StorageName = UniqueName() };
        StorageEventSource.Log.IsEnabled().ShouldBeFalse("a listener from another test is still attached");
        for (int i = 0; i < 100; i++)
        {
            pool.Pin((PageId)0L, stream).Dispose();
        }

        using var resident = pool.Pin((PageId)0L, stream);
        long before = GC.GetAllocatedBytesForCurrentThread();
        var probe = new StoragePageHandle((PageId)0L, resident.Entry, pool);
        long handleBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        GC.KeepAlive(probe);

        // Act: the fewest bytes of three rounds. A pin path that allocates does so in every round,
        // while the runtime can now and then charge an allocation of its own to this thread
        // (Windows CI once read 484,800 bytes for 480,000 of handles, a one-off of 4,800).
        long allocated = long.MaxValue;
        for (int round = 0; round < 3; round++)
        {
            before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < Pins; i++)
            {
                pool.Pin((PageId)0L, stream).Dispose();
            }

            long roundBytes = GC.GetAllocatedBytesForCurrentThread() - before;
            _output.WriteLine($"Round {round}: {Pins} pin hits allocated {roundBytes} bytes; one handle is {handleBytes} bytes.");
            allocated = Math.Min(allocated, roundBytes);
        }

        // Assert: exactly the handles, as before the event source existed.
        handleBytes.ShouldBeGreaterThan(0);
        allocated.ShouldBe(Pins * handleBytes);
    }

    private static string UniqueName() => "event-source-" + Guid.NewGuid().ToString("N");

    /// <summary>
    /// An event's own keywords: EventSource sets bits it reserves on written events (the session
    /// bits 44-47 and the top 16), which the source's <c>Keywords</c> never use.
    /// </summary>
    private static EventKeywords SourceKeywords(EventWrittenEventArgs eventData)
        => eventData.Keywords & (EventKeywords)0x0000_0FFF_FFFF_FFFF;

    private static StorageStream StreamWithPages(int pageCount)
    {
        var bytes = new byte[pageCount * Page.Size];
        for (int i = 0; i < pageCount; i++)
        {
            StampedPage(PageType.Data).CopyTo(bytes, i * Page.Size);
        }

        return new StorageStream(new MemoryStream(bytes));
    }

    private static unsafe byte[] StampedPage(PageType type)
    {
        var bytes = new byte[Page.Size];
        fixed (byte* pointer = bytes)
        {
            var page = new Page(pointer);
            page.Type = type;
        }

        PageChecksum.Stamp(bytes);
        return bytes;
    }
}
