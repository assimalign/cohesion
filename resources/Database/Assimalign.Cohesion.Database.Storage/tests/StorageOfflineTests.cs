using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Storage.Tests.TestObjects;
using Assimalign.Cohesion.Database.Storage.Units;

namespace Assimalign.Cohesion.Database.Storage.Tests;

/// <summary>
/// A failed durable flush takes the storage offline (#1243): the failing call and every later
/// write throw <see cref="StorageOfflineException"/>, nothing more reaches the journal or the data
/// file, closing writes nothing, and the reopen's recovery decides the outcome of the commit whose
/// flush failed. PostgreSQL raises PANIC on a failed WAL fsync (<c>issue_xlog_fsync</c>,
/// <c>xlog.c:9877-9937</c>) and, with <c>data_sync_retry</c> off, on a failed data-file fsync
/// (<c>data_sync_elevel</c>, <c>fd.c:3966-3987</c>).
/// </summary>
public sealed class StorageOfflineTests
{
    /// <summary>
    /// The commit record is appended, then its durable flush fails. The storage goes offline, the
    /// bracket ends committed in memory, and every later write is refused without reaching either
    /// file. Whether the commit survives is the reopen's recovery's decision: it does when the
    /// record's bytes reached the media anyway (a write-through journal), and it does not when they
    /// were lost with the failed flush (a flush-gated journal).
    /// </summary>
    /// <param name="recordReachedTheMedia">True for a journal whose writes reach the media without a flush.</param>
    [Theory(DisplayName = "Cohesion Test [Storage] - Offline: a commit whose journal flush fails takes the storage offline and recovery decides")]
    [InlineData(true)]
    [InlineData(false)]
    public void Commit_JournalFlushFails_ShouldTakeTheStorageOfflineAndLeaveTheOutcomeToRecovery(bool recordReachedTheMedia)
    {
        // Arrange
        var point = new CrashPoint();
        var storage = TornStorage.Create(point, journalWriteThrough: recordReachedTheMedia);
        var (keptPage, keptSlot) = storage.Insert("kept");
        var transaction = storage.BeginTransaction();
        storage.Insert(transaction, "unconfirmed");
        storage.JournalFaults.FailNextFlush();

        // Act
        var error = Should.Throw<StorageOfflineException>(() => transaction.Commit());
        int writesAtTheFailure = point.Writes;
        var liveAtTheFailure = storage.CaptureLive();
        bool activeAfterTheFailure = transaction.IsActive;
        var refusals = new Exception[]
        {
            Should.Throw<StorageOfflineException>(() => storage.BeginTransaction()),
            Should.Throw<StorageOfflineException>(() => storage.BeginTransaction(10_000)),
            Should.Throw<StorageOfflineException>(() => storage.ReserveTransactionSequence()),
            Should.Throw<StorageOfflineException>(() => storage.Checkpoint()),
            Should.Throw<StorageOfflineException>(() => storage.Log.AppendBegin(1_000)),
            Should.Throw<StorageOfflineException>(() => storage.Log.AppendCommit(1_000)),
            Should.Throw<StorageOfflineException>(() => storage.Log.Flush(forceDurable: true)),
            Should.Throw<StorageOfflineException>(() => storage.FlushHeader()),
        };
        int writtenBack = storage.WriteBackDirtyPages(64);
        bool flushedCommits = storage.FlushPendingCommits();
        transaction.Dispose();
        storage.Dispose();
        using var reopened = TornStorage.Open(storage.CaptureDurable(), checkpointOnOpen: true);

        // Assert: the first failure names the flush, every refusal carries the same cause, and
        // nothing reached either file after the failure, the close included.
        error.Message.ShouldStartWith(StorageOfflineException.ErrorCode, Case.Sensitive);
        error.InnerException.ShouldBeOfType<IOException>();
        error.CommitRecordWritten.ShouldBeTrue();
        activeAfterTheFailure.ShouldBeFalse();
        storage.IsOffline.ShouldBeTrue();
        storage.OfflineError.ShouldNotBeNull().InnerException.ShouldBeSameAs(error.InnerException);
        storage.OfflineError!.CommitRecordWritten.ShouldBeFalse();
        refusals.ShouldAllBe(refusal => refusal.Message.StartsWith(StorageOfflineException.ErrorCode, StringComparison.Ordinal)
            && refusal.InnerException == error.InnerException
            && !((StorageOfflineException)refusal).CommitRecordWritten);
        writtenBack.ShouldBe(0);
        flushedCommits.ShouldBeFalse();
        point.Writes.ShouldBe(writesAtTheFailure);
        storage.CaptureLive().Data.ShouldBe(liveAtTheFailure.Data);
        storage.CaptureLive().Journal.ShouldBe(liveAtTheFailure.Journal);
        reopened.Read(keptPage, keptSlot).ShouldBe("kept");
        reopened.ScanText().Contains("unconfirmed").ShouldBe(recordReachedTheMedia);
        reopened.IsOffline.ShouldBeFalse();
    }

    /// <summary>
    /// A durable flush of the data file fails during a checkpoint, after the dirty pages were
    /// written back and recorded clean. A retry used to flush nothing new, succeed, and truncate the
    /// journal, although the operating system may have dropped those write-backs (PostgreSQL's
    /// "fsyncgate"). The storage now goes offline instead: the retry is refused, the journal keeps
    /// every record, and the reopen rebuilds the committed rows from it.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Storage] - Offline: a data flush failure at a checkpoint keeps the journal for recovery")]
    public void Checkpoint_DataFlushFails_ShouldGoOfflineAndKeepTheJournal()
    {
        // Arrange: two committed rows on two pages the checkpoint will write back.
        var point = new CrashPoint();
        var storage = TornStorage.Create(point);
        var pages = storage.FillPages(2);
        long journalLength = storage.JournalLength;
        storage.DataFaults.FailFlushAfterWriteAt = pages[0] * Page.Size;

        // Act
        var error = Should.Throw<StorageOfflineException>(() => storage.Checkpoint());
        int writesAtTheFailure = point.Writes;
        var retry = Should.Throw<StorageOfflineException>(() => storage.Checkpoint());
        storage.Dispose();
        var images = storage.CaptureDurable();

        // The write-backs the failed flush reported on are lost, as the operating system may
        // have dropped them: only the journal can bring the rows back.
        images.Data.AsSpan((int)(pages[0] * Page.Size), Page.Size).Clear();
        images.Data.AsSpan((int)(pages[1] * Page.Size), Page.Size).Clear();
        using var reopened = TornStorage.Open(images, checkpointOnOpen: true);

        // Assert
        error.Message.ShouldContain("durable flush of the data file");
        retry.InnerException.ShouldBeSameAs(error.InnerException);
        storage.DataFaults.FailedFlushes.ShouldBe(1);
        point.Writes.ShouldBe(writesAtTheFailure);
        storage.JournalLength.ShouldBe(journalLength);
        reopened.CountRecords(7).ShouldBe(2);
    }

    /// <summary>
    /// An offline storage writes no page, not even one whose LSN the journal already made durable or
    /// one that carries no LSN at all, and grows no file: an eviction that would write a dirty page
    /// back is refused, and so is a record change that would allocate.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Storage] - Offline: an offline storage evicts no dirty page and extends no file")]
    public void BufferPool_StorageOffline_ShouldRefuseEveryWriteBackAndExtension()
    {
        // Arrange: committed pages fill most of a small pool; one more is dirty but durable.
        var point = new CrashPoint();
        var storage = TornStorage.Create(point, poolCapacity: 8);
        var pages = storage.FillPages(12);
        var open = storage.BeginTransaction();
        var transaction = storage.BeginTransaction();
        storage.Insert(transaction, "dirty");
        storage.JournalFaults.FailNextFlush();
        Should.Throw<StorageOfflineException>(() => transaction.Commit());
        int writesAtTheFailure = point.Writes;
        long dataLength = storage.Data.Length;

        // Act: read every page, which must evict dirty pages to make room.
        Exception? evictionRefusal = null;
        foreach (long page in pages)
        {
            try
            {
                using var handle = storage.PageManager.GetPage((PageId)page);
            }
            catch (StorageOfflineException exception)
            {
                evictionRefusal = exception;
                break;
            }
        }

        // A bracket begun before the failure would need a new page for this record.
        var growth = Should.Throw<StorageOfflineException>(() => storage.Insert(open, new string('x', 7_000)));

        // Assert
        evictionRefusal.ShouldNotBeNull();
        growth.Message.ShouldStartWith(StorageOfflineException.ErrorCode, Case.Sensitive);
        point.Writes.ShouldBe(writesAtTheFailure);
        storage.Data.Length.ShouldBe(dataLength);
    }

    /// <summary>
    /// Under grouped durability the flush worker's group flush fails. The committers waiting for it
    /// are released at once with the offline error, not after their self-help window.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Storage] - Offline: a failed group flush releases its waiting committers at once")]
    public async Task FlushPendingCommits_GroupFlushFails_ShouldReleaseWaitingCommittersAtOnce()
    {
        // Arrange: a window far longer than the test, so only the release can end the wait.
        var storage = TornStorage.Create(journalWriteThrough: false);
        storage.CommitDurability = StorageCommitDurability.Grouped;
        storage.GroupCommitWindow = TimeSpan.FromMinutes(5);
        using var pending = new ManualResetEventSlim();
        storage.OnCommitPending = pending.Set;
        var transaction = storage.BeginTransaction();
        storage.Insert(transaction, "grouped");
        var watch = Stopwatch.StartNew();
        var commit = Task.Run(() => transaction.Commit());
        pending.Wait(TimeSpan.FromSeconds(30)).ShouldBeTrue();
        storage.JournalFaults.FailNextFlush();

        // Act: the worker's pass.
        bool flushed = storage.FlushPendingCommits();
        var error = await Should.ThrowAsync<StorageOfflineException>(async () => await commit.WaitAsync(TimeSpan.FromSeconds(30)));
        watch.Stop();

        // Assert: released long before the window would have run out.
        flushed.ShouldBeFalse();
        error.InnerException.ShouldBeOfType<IOException>();
        (watch.Elapsed / storage.GroupCommitWindow).ShouldBeLessThan(0.1);
        storage.IsOffline.ShouldBeTrue();
    }

    /// <summary>
    /// An LSN that was durable before the failure is still confirmed: its durability was
    /// established, so a caller that asks for it is not told its commit is unconfirmed.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Storage] - Offline: an LSN durable before the failure is still confirmed")]
    public void EnsureDurable_LsnDurableBeforeTheFailure_ShouldStillBeConfirmed()
    {
        // Arrange
        var storage = TornStorage.Create(journalWriteThrough: false);
        long confirmed = storage.Log.AppendBegin(500);
        storage.Log.EnsureDurable(confirmed);
        long unconfirmed = storage.Log.AppendBegin(501);
        storage.JournalFaults.FailNextFlush();
        Should.Throw<StorageOfflineException>(() => storage.Log.EnsureDurable(unconfirmed));

        // Act
        storage.Log.EnsureDurable(confirmed);
        var refusal = Should.Throw<StorageOfflineException>(() => storage.Log.EnsureDurable(unconfirmed));

        // Assert
        refusal.Message.ShouldStartWith(StorageOfflineException.ErrorCode, Case.Sensitive);
        storage.Log.DurableLsn.ShouldBe(confirmed);
    }

    /// <summary>
    /// The offline error is found wherever an engine's layers wrapped it.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Storage] - Offline: Find locates the offline error in inner and aggregate exceptions")]
    public void Find_WrappedOfflineError_ShouldReturnIt()
    {
        // Arrange
        var storage = TornStorage.Create(journalWriteThrough: false);
        long lsn = storage.Log.AppendBegin(9);
        storage.JournalFaults.FailNextFlush();
        var offline = Should.Throw<StorageOfflineException>(() => storage.Log.EnsureDurable(lsn));
        var wrapped = new InvalidOperationException("outer", new AggregateException(new IOException("other"), new Exception("inner", offline)));

        // Act & Assert
        StorageOfflineException.Find(wrapped).ShouldBeSameAs(offline);
        StorageOfflineException.Find(new IOException("unrelated")).ShouldBeNull();
        StorageOfflineException.Find(null).ShouldBeNull();
    }

    /// <summary>
    /// Two storages of one database (a data and a catalog file set) are wired to take each other
    /// offline from <see cref="Storage.OnOffline"/>. A journal fsync of the first fails: the second
    /// is offline before the failing call returns, each hook runs exactly once, and the hook runs
    /// outside the failing journal's lock, so another thread can take that lock meanwhile (the
    /// handlers of two storages failing together cannot deadlock). Nothing more reaches the
    /// second storage's files (#1243 review).
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Storage] - Offline: OnOffline takes a second file set offline at once, once, outside the journal lock")]
    public void OnOffline_JournalFlushFails_ShouldTakeTheOtherFileSetOfflineAtOnce()
    {
        // Arrange: the second storage holds a dirty page its write-back would write.
        var firstPoint = new CrashPoint();
        var secondPoint = new CrashPoint();
        var first = TornStorage.Create(firstPoint, journalWriteThrough: false);
        var second = TornStorage.Create(secondPoint, journalWriteThrough: false);
        second.Insert("dirty");
        int firstRaised = 0;
        int secondRaised = 0;
        bool secondOfflineInTheHook = false;
        bool journalLockFreeInTheHook = false;
        first.OnOffline = error =>
        {
            firstRaised++;
            second.TakeOffline(error);
            secondOfflineInTheHook = second.IsOffline;
            // A flush takes the journal's lock (and is refused): it completes only if the hook
            // does not hold that lock.
            journalLockFreeInTheHook = Task.Run(() => Record.Exception(() => first.Log.Flush())).Wait(TimeSpan.FromSeconds(10));
        };
        second.OnOffline = error =>
        {
            secondRaised++;
            first.TakeOffline(error);
        };
        long lsn = first.Log.AppendBegin(7);
        first.JournalFaults.FailNextFlush();

        // Act
        var error = Should.Throw<StorageOfflineException>(() => first.Log.EnsureDurable(lsn));
        int secondWritesAtTheFailure = secondPoint.Writes;
        int writtenBack = second.WriteBackDirtyPages(64);
        var refusal = Should.Throw<StorageOfflineException>(() => second.BeginTransaction());
        second.Dispose();

        // Assert
        firstRaised.ShouldBe(1);
        secondRaised.ShouldBe(1);
        secondOfflineInTheHook.ShouldBeTrue();
        journalLockFreeInTheHook.ShouldBeTrue();
        second.OfflineError.ShouldBeSameAs(error);
        refusal.InnerException.ShouldBeSameAs(error.InnerException);
        writtenBack.ShouldBe(0);
        secondPoint.Writes.ShouldBe(secondWritesAtTheFailure);
    }

    /// <summary>
    /// A durable flush of the data file fails at a checkpoint: <see cref="Storage.OnOffline"/> is
    /// raised exactly once with that error, although the data-file path also latches the journal.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Storage] - Offline: OnOffline is raised once for a failed data-file flush")]
    public void OnOffline_DataFlushFails_ShouldBeRaisedOnce()
    {
        // Arrange
        var storage = TornStorage.Create(new CrashPoint());
        var pages = storage.FillPages(1);
        storage.DataFaults.FailFlushAfterWriteAt = pages[0] * Page.Size;
        var raised = new System.Collections.Generic.List<StorageOfflineException>();
        storage.OnOffline = raised.Add;

        // Act
        var error = Should.Throw<StorageOfflineException>(() => storage.Checkpoint());
        Should.Throw<StorageOfflineException>(() => storage.Checkpoint());
        storage.TakeOffline(error);
        storage.Dispose();

        // Assert
        raised.ShouldHaveSingleItem().ShouldBeSameAs(error);
        error.CommitRecordWritten.ShouldBeFalse();
    }
}
