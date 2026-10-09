using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Tracing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Storage.Tests;
using Assimalign.Cohesion.Database.Storage.Tests.TestObjects;
using Assimalign.Cohesion.Database.Transactions.Internal;

namespace Assimalign.Cohesion.Database.Transactions.Tests;

/// <summary>
/// Serializes the tests that observe the Transactions event source: the source and its counters
/// are process-wide.
/// </summary>
[CollectionDefinition(nameof(TransactionEventSourceCollection), DisableParallelization = true)]
public class TransactionEventSourceCollection
{
}

/// <summary>
/// The transaction kernel's event source against the repository's EventSource convention
/// (<c>.claude/rules/event-source.md</c>) and the plan of record's catalog
/// (<c>docs/programs/DATABASE_EVENT_SOURCES_PLAN.md</c>, §4.3): each event is raised once by a real
/// operation on a real coordinator, storage and journal, with its declared payload; the counters
/// publish and the transaction gauge returns to where it started; and an uncontended lock request
/// allocates nothing more for the instrumentation.
/// </summary>
/// <remarks>
/// Every database here has its own name, and the assertions read only that database's events, so
/// a stray event of another test cannot satisfy or break them.
/// </remarks>
[Collection(nameof(TransactionEventSourceCollection))]
public sealed class TransactionEventSourceTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static readonly LockResource Row = LockResource.Entry(7, 7);

    private readonly ITestOutputHelper _output;

    public TransactionEventSourceTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact(DisplayName = "Cohesion Test [Database.Transactions] - TransactionEventSource: Should be named for its assembly")]
    public void GetName_TransactionEventSource_ShouldEqualAssemblyName()
    {
        // Act
        string name = EventSource.GetName(typeof(TransactionEventSource));

        // Assert
        name.ShouldBe(typeof(TransactionEventSource).Assembly.GetName().Name);
        name.ShouldBe("Assimalign.Cohesion.Database.Transactions");
    }

    [Fact(DisplayName = "Cohesion Test [Database.Transactions] - TransactionEventSource: Should generate a manifest in strict mode")]
    [UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode", Justification = "Test-only manifest validation reflects over the event source; test assemblies are not trimmed.")]
    [UnconditionalSuppressMessage("Trimming", "IL2111:DynamicallyAccessedMembers", Justification = "Test-only manifest validation reflects over the event source; test assemblies are not trimmed.")]
    public void GenerateManifest_StrictMode_ShouldSucceed()
    {
        // Act
        string? manifest = EventSource.GenerateManifest(typeof(TransactionEventSource), assemblyPathToIncludeInManifest: null, EventManifestOptions.Strict);

        // Assert
        manifest.ShouldNotBeNull();
        manifest.ShouldContain("Assimalign.Cohesion.Database.Transactions", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Transactions] - TransactionEventSource: Should report begin, commit and rollback once each, and return the transaction gauge")]
    public async Task BeginCommitRollback_UnderListener_ShouldReportEachOnceAndReturnTheGauge()
    {
        // Arrange
        string name = UniqueName();
        var source = TransactionEventSource.Log;
        using var storage = EventStorage.Create(name);
        using var recorder = new TransactionEventRecorder(EventLevel.Verbose);
        long current = source.CurrentTransactions;
        long total = source.TotalTransactions;
        long commits = source.TotalCommits;
        long rollbacks = source.TotalRollbacks;
        TransactionContext committed;
        TransactionContext rolledBack;
        long currentWhileOpen;

        // Act
        await using (var coordinator = new TransactionCoordinator(storage, storage.Log, storage.Records))
        {
            committed = await coordinator.BeginAsync(IsolationLevel.Snapshot);
            rolledBack = await coordinator.BeginAsync(IsolationLevel.ReadCommitted);
            currentWhileOpen = source.CurrentTransactions;
            await InsertAsync(coordinator, storage, committed);
            await coordinator.CommitAsync(committed);
            await coordinator.RollbackAsync(rolledBack);
        }

        // Assert
        recorder.ShouldHaveNoInstrumentationError();
        var events = recorder.For(name);
        events.Select(e => e.EventName).ShouldBe(["TransactionBegun", "TransactionBegun", "TransactionCommitted", "TransactionRolledBack"]);

        events[0].EventId.ShouldBe(1);
        events[0].Level.ShouldBe(EventLevel.Verbose);
        Declared(events[0].Keywords).ShouldBe(TransactionEventSource.Keywords.Transactions);
        events[0].PayloadNames.ShouldBe(["database", "transactionSequence", "isolationLevel"]);
        events[0].Payload.ShouldBe([name, Sequence(committed), nameof(IsolationLevel.Snapshot)]);
        events[1].Payload.ShouldBe([name, Sequence(rolledBack), nameof(IsolationLevel.ReadCommitted)]);

        events[2].EventId.ShouldBe(2);
        events[2].PayloadNames.ShouldBe(["database", "transactionSequence"]);
        events[2].Payload.ShouldBe([name, Sequence(committed)]);

        events[3].EventId.ShouldBe(3);
        events[3].PayloadNames.ShouldBe(["database", "transactionSequence"]);
        events[3].Payload.ShouldBe([name, Sequence(rolledBack)]);

        currentWhileOpen.ShouldBe(current + 2);
        source.CurrentTransactions.ShouldBe(current);
        source.TotalTransactions.ShouldBe(total + 2);
        source.TotalCommits.ShouldBe(commits + 1);
        source.TotalRollbacks.ShouldBe(rollbacks + 1);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Transactions] - TransactionEventSource: Should return the transaction gauge for a transaction the coordinator's disposal aborts")]
    public async Task DisposeAsync_TransactionStillOpen_ShouldReturnTheGauge()
    {
        // Arrange
        var source = TransactionEventSource.Log;
        using var storage = EventStorage.Create(UniqueName());
        long current = source.CurrentTransactions;
        long rollbacks = source.TotalRollbacks;
        var coordinator = new TransactionCoordinator(storage, storage.Log, storage.Records);
        var open = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        await InsertAsync(coordinator, storage, open);

        // Act
        await coordinator.DisposeAsync();

        // Assert
        open.State.ShouldBe(TransactionState.Faulted);
        source.CurrentTransactions.ShouldBe(current);
        source.TotalRollbacks.ShouldBe(rollbacks + 1);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Transactions] - TransactionEventSource: Should report a deadlock victim once, and the wait it broke as one start and one stop")]
    public async Task AcquireAsync_TwoTransactionsDeadlock_ShouldReportTheVictimAndTheWait()
    {
        // Arrange: each transaction holds the row the other is about to request.
        string name = UniqueName();
        var source = TransactionEventSource.Log;
        using var storage = EventStorage.Create(name);
        await using var coordinator = new TransactionCoordinator(storage, storage.Log, storage.Records);
        var locks = coordinator.LockManager;
        var first = LockResource.Entry(1, 1);
        var second = LockResource.Entry(1, 2);
        var waiter = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        var victim = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        await locks.AcquireAsync(waiter.Sequence, first, LockMode.Exclusive);
        await locks.AcquireAsync(victim.Sequence, second, LockMode.Exclusive);
        long deadlocks = source.TotalDeadlocks;
        long waits = source.TotalLockWaits;
        using var recorder = new TransactionEventRecorder(EventLevel.Verbose);

        // Act: the waiter queues for the victim's row; the victim's request would close the cycle.
        var waiting = locks.AcquireAsync(waiter.Sequence, second, LockMode.Exclusive).AsTask();
        await Should.ThrowAsync<TransactionDeadlockException>(async () => await locks.AcquireAsync(victim.Sequence, first, LockMode.Exclusive));
        await coordinator.RollbackAsync(victim);
        await waiting.WaitAsync(Timeout);
        await coordinator.CommitAsync(waiter);

        // Assert
        recorder.ShouldHaveNoInstrumentationError();
        var events = recorder.For(name);

        var deadlock = events.Where(e => e.EventName == "DeadlockDetected").ShouldHaveSingleItem();
        deadlock.EventId.ShouldBe(6);
        deadlock.Level.ShouldBe(EventLevel.Warning);
        deadlock.PayloadNames.ShouldBe(["database", "transactionSequence", "resource", "mode"]);
        deadlock.Payload.ShouldBe([name, Sequence(victim), "Entry:1", nameof(LockMode.Exclusive)]);

        var start = events.Where(e => e.EventName == "LockWaitStart").ShouldHaveSingleItem();
        start.EventId.ShouldBe(7);
        start.Level.ShouldBe(EventLevel.Verbose);
        Declared(start.Keywords).ShouldBe(TransactionEventSource.Keywords.Locks);
        start.PayloadNames.ShouldBe(["database", "transactionSequence", "resource", "mode"]);
        start.Payload.ShouldBe([name, Sequence(waiter), "Entry:1", nameof(LockMode.Exclusive)]);

        var stop = events.Where(e => e.EventName == "LockWaitStop").ShouldHaveSingleItem();
        stop.EventId.ShouldBe(8);
        Declared(stop.Keywords).ShouldBe(TransactionEventSource.Keywords.Locks);
        stop.PayloadNames.ShouldBe(["database", "transactionSequence", "outcome", "durationMilliseconds"]);
        stop.Payload!.Take(3).ShouldBe([name, Sequence(waiter), "Granted"]);
        ((double)stop.Payload![3]!).ShouldBeGreaterThanOrEqualTo(0);

        source.TotalDeadlocks.ShouldBe(deadlocks + 1);
        source.TotalLockWaits.ShouldBe(waits + 1);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Transactions] - TransactionEventSource: Should report every wait as slow under a 0 ms threshold argument, before naming how it ended")]
    public async Task AcquireAsync_ZeroThresholdArgument_ShouldReportSlowWaitsAndTheirOutcomes()
    {
        // Arrange: one transaction holds the row; another's wait for it is canceled, a third's is granted.
        string name = UniqueName();
        var source = TransactionEventSource.Log;
        using var storage = EventStorage.Create(name);
        await using var coordinator = new TransactionCoordinator(storage, storage.Log, storage.Records);
        var locks = coordinator.LockManager;
        var holder = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        var canceled = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        var granted = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        await locks.AcquireAsync(holder.Sequence, Row, LockMode.Exclusive);
        double thresholdWhileEnabled;
        double thresholdAfterAnotherEnable;

        using (var recorder = new TransactionEventRecorder(
            EventLevel.Verbose,
            new Dictionary<string, string?> { [TransactionEventSource.SlowLockWaitThresholdArgument] = "0" }))
        {
            thresholdWhileEnabled = source.SlowLockWaitThresholdMilliseconds;

            // Act
            using (var cancellation = new CancellationTokenSource())
            {
                var waiting = locks.AcquireAsync(canceled.Sequence, Row, LockMode.Exclusive, cancellation.Token).AsTask();
                await cancellation.CancelAsync();
                await Should.ThrowAsync<OperationCanceledException>(async () => await waiting.WaitAsync(Timeout));
            }

            var grantedWait = locks.AcquireAsync(granted.Sequence, Row, LockMode.Exclusive).AsTask();
            await coordinator.CommitAsync(holder);
            await grantedWait.WaitAsync(Timeout);
            await coordinator.CommitAsync(granted);
            await coordinator.RollbackAsync(canceled);

            // Assert
            recorder.ShouldHaveNoInstrumentationError();
            var events = recorder.For(name).Where(e => e.EventName is "SlowLockWait" or "LockWaitStop").ToArray();
            events.Select(e => (e.EventName, Sequence: (long)e.Payload![1]!)).ShouldBe(
            [
                ("SlowLockWait", Sequence(canceled)),
                ("LockWaitStop", Sequence(canceled)),
                ("SlowLockWait", Sequence(granted)),
                ("LockWaitStop", Sequence(granted)),
            ]);

            var slow = events[2];
            slow.EventId.ShouldBe(9);
            slow.Level.ShouldBe(EventLevel.Warning);
            slow.PayloadNames.ShouldBe(["database", "transactionSequence", "resource", "mode", "durationMilliseconds", "thresholdMilliseconds"]);
            slow.Payload!.Take(4).ShouldBe([name, Sequence(granted), "Entry:7", nameof(LockMode.Exclusive)]);
            ((double)slow.Payload![4]!).ShouldBeGreaterThanOrEqualTo(0);
            slow.Payload![5].ShouldBe(0d);

            events[1].Payload![2].ShouldBe("Cancelled");
            events[3].Payload![2].ShouldBe("Granted");
        }

        // A later session that passes no argument restores the default (plan D7).
        using (new TransactionEventRecorder(EventLevel.Warning))
        {
            thresholdAfterAnotherEnable = source.SlowLockWaitThresholdMilliseconds;
        }

        thresholdWhileEnabled.ShouldBe(0);
        thresholdAfterAnotherEnable.ShouldBe(TransactionEventSource.DefaultSlowLockWaitThresholdMilliseconds);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Transactions] - TransactionEventSource: Should restore the default slow-lock-wait threshold when the session that set it disables the source")]
    public void OnEventCommand_ThresholdSessionDisables_ShouldRestoreTheDefaultForTheRemainingSession()
    {
        // Arrange: a forwarder-like session with no arguments, then a tool session that sets 0 ms.
        var source = TransactionEventSource.Log;
        using var forwarder = new TransactionEventRecorder(EventLevel.Warning);
        double whileToolEnabled;
        double afterToolDisabled;
        using (var tool = new TransactionEventRecorder(
            EventLevel.Verbose,
            new Dictionary<string, string?> { [TransactionEventSource.SlowLockWaitThresholdArgument] = "0" }))
        {
            whileToolEnabled = source.SlowLockWaitThresholdMilliseconds;

            // Act: the tool session ends while the forwarder stays enabled.
            tool.DisableEvents(source);
            afterToolDisabled = source.SlowLockWaitThresholdMilliseconds;
        }

        // Assert
        whileToolEnabled.ShouldBe(0);
        afterToolDisabled.ShouldBe(TransactionEventSource.DefaultSlowLockWaitThresholdMilliseconds);
        source.IsEnabled(EventLevel.Warning, EventKeywords.None).ShouldBeTrue();
    }

    [Theory(DisplayName = "Cohesion Test [Database.Transactions] - TransactionEventSource: Should never write an entry lock's id, which for a key lock is the key's hash")]
    [InlineData(LockResourceKind.Database, 0UL, 0UL, "Database")]
    [InlineData(LockResourceKind.Object, 12UL, 0UL, "Object:12")]
    [InlineData(LockResourceKind.Entry, 12UL, 0xDEAD_BEEF_CAFE_F00DUL, "Entry:12")]
    public void DescribeResource_AnyKind_ShouldNameTheKindAndObjectOnly(LockResourceKind kind, ulong objectId, ulong entryId, string expected)
    {
        // Arrange
        var resource = new LockResource(kind, objectId, entryId);

        // Act
        string described = TransactionEventSource.DescribeResource(resource);

        // Assert
        described.ShouldBe(expected);
        if (entryId != 0)
        {
            described.ShouldNotContain(entryId.ToString(CultureInfo.InvariantCulture), Case.Sensitive);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Database.Transactions] - TransactionEventSource: Should write a key lock's wait by its object only, never the key's hash")]
    public async Task AcquireAsync_KeyHashEntryWait_ShouldWriteTheObjectOnly()
    {
        // Arrange: an entry lock whose id is a key's 64-bit hash, as a unique index or a key space takes it.
        string name = UniqueName();
        using var storage = EventStorage.Create(name);
        await using var coordinator = new TransactionCoordinator(storage, storage.Log, storage.Records);
        var locks = coordinator.LockManager;
        var keyLock = LockResource.Entry(31, 0x9E37_79B9_7F4A_7C15UL);
        var holder = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        var waiter = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        await locks.AcquireAsync(holder.Sequence, keyLock, LockMode.Exclusive);
        using var recorder = new TransactionEventRecorder(EventLevel.Verbose);

        // Act
        var waiting = locks.AcquireAsync(waiter.Sequence, keyLock, LockMode.Exclusive).AsTask();
        await coordinator.CommitAsync(holder);
        await waiting.WaitAsync(Timeout);
        await coordinator.CommitAsync(waiter);

        // Assert
        recorder.ShouldHaveNoInstrumentationError();
        var start = recorder.For(name).Where(e => e.EventName == "LockWaitStart").ShouldHaveSingleItem();
        start.Payload![2].ShouldBe("Entry:31");
        recorder.For(name).ShouldNotContain(e => e.Payload!.OfType<string>().Any(
            text => text.Contains(keyLock.EntryId.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)));
    }

    [Fact(DisplayName = "Cohesion Test [Database.Transactions] - TransactionEventSource: Should report the waits a storage going offline abandons, once, and end each wait as abandoned")]
    public async Task AbandonLockWaits_StorageWentOffline_ShouldReportOnceAndEndTheWaitAsAbandoned()
    {
        // Arrange: a writer holds the row and another transaction waits for it; a failed checkpoint
        // drain takes the storage offline (#1243).
        string name = UniqueName();
        using var storage = EventStorage.Create(name);
        await using var coordinator = new TransactionCoordinator(storage, storage.Log, storage.Records);
        var holder = await BeginWriterAsync(coordinator, storage);
        var waiter = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        using var recorder = new TransactionEventRecorder(EventLevel.Verbose);
        var waiting = coordinator.LockManager.AcquireAsync(waiter.Sequence, Row, LockMode.Exclusive).AsTask();
        storage.Log.Flush();
        storage.JournalStream.FailWrites = 1;
        var offline = Should.Throw<StorageOfflineException>(() => coordinator.Checkpoint());

        // Act: the engine's offline hook abandons the waits, and a second call changes nothing.
        coordinator.AbandonLockWaits(offline);
        coordinator.AbandonLockWaits(offline);
        await Should.ThrowAsync<TransactionAbortedException>(async () => await waiting.WaitAsync(Timeout));

        // Assert
        recorder.ShouldHaveNoInstrumentationError();
        var events = recorder.For(name);

        var abandoned = events.Where(e => e.EventName == "LockWaitsAbandoned").ShouldHaveSingleItem();
        abandoned.EventId.ShouldBe(10);
        abandoned.Level.ShouldBe(EventLevel.Warning);
        abandoned.PayloadNames.ShouldBe(["database", "cause"]);
        abandoned.Payload.ShouldBe([name, offline.Cause.ToString()]);

        var stop = events.Where(e => e.EventName == "LockWaitStop").ShouldHaveSingleItem();
        stop.Payload!.Take(3).ShouldBe([name, Sequence(waiter), "Abandoned"]);
        holder.State.ShouldBe(TransactionState.Active);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Transactions] - TransactionEventSource: Should end a wait whose own transaction ended while it waited as ended, once")]
    public async Task AcquireAsync_WaitersTransactionRolledBackWhileWaiting_ShouldEndTheWaitAsEnded()
    {
        // Arrange: one transaction holds the row, and another queues for it.
        string name = UniqueName();
        using var storage = EventStorage.Create(name);
        await using var coordinator = new TransactionCoordinator(storage, storage.Log, storage.Records);
        var locks = coordinator.LockManager;
        var holder = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        var waiter = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        await locks.AcquireAsync(holder.Sequence, Row, LockMode.Exclusive);
        using var recorder = new TransactionEventRecorder(EventLevel.Verbose);
        var waiting = locks.AcquireAsync(waiter.Sequence, Row, LockMode.Exclusive).AsTask();

        // Act: the waiter's own transaction ends, which fails its queued request.
        await coordinator.RollbackAsync(waiter);
        await Should.ThrowAsync<TransactionAbortedException>(async () => await waiting.WaitAsync(Timeout));
        await coordinator.CommitAsync(holder);

        // Assert
        recorder.ShouldHaveNoInstrumentationError();
        var events = recorder.For(name);
        events.Where(e => e.EventName == "LockWaitStart").ShouldHaveSingleItem().Payload![1].ShouldBe(Sequence(waiter));
        var stop = events.Where(e => e.EventName == "LockWaitStop").ShouldHaveSingleItem();
        stop.Payload!.Take(3).ShouldBe([name, Sequence(waiter), "Ended"]);
        ((double)stop.Payload![3]!).ShouldBeGreaterThanOrEqualTo(0);
        waiter.State.ShouldBe(TransactionState.RolledBack);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Transactions] - TransactionEventSource: Should report a commit whose record could not be made durable once, with the flush failure")]
    public async Task CommitAsync_CommitRecordFlushFails_ShouldReportTheUnconfirmedCommitOnce()
    {
        // Arrange: a writer whose statement records wait in the journal's append buffer.
        string name = UniqueName();
        var source = TransactionEventSource.Log;
        using var storage = EventStorage.Create(name);
        await using var coordinator = new TransactionCoordinator(storage, storage.Log, storage.Records);
        var writer = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        storage.Log.Flush();
        await InsertAsync(coordinator, storage, writer);
        long commits = source.TotalCommits;
        using var recorder = new TransactionEventRecorder(EventLevel.Verbose);
        storage.JournalStream.FailWrites = 1;

        // Act
        var failure = await Should.ThrowAsync<TransactionCommitUnconfirmedException>(async () => await coordinator.CommitAsync(writer));

        // Assert: the error, not the verbose commit; the transaction still ended committed.
        recorder.ShouldHaveNoInstrumentationError();
        var events = recorder.For(name);
        events.ShouldNotContain(e => e.EventName == "TransactionCommitted");

        var unconfirmed = events.Where(e => e.EventName == "CommitUnconfirmed").ShouldHaveSingleItem();
        unconfirmed.EventId.ShouldBe(5);
        unconfirmed.Level.ShouldBe(EventLevel.Error);
        unconfirmed.PayloadNames.ShouldBe(["database", "transactionSequence", "exceptionMessage"]);
        unconfirmed.Payload.ShouldBe([name, Sequence(writer), failure.InnerException.ShouldNotBeNull().Message]);
        writer.State.ShouldBe(TransactionState.Committed);
        source.TotalCommits.ShouldBe(commits + 1);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Transactions] - TransactionEventSource: Should report a transaction the kernel aborted because its commit record could not be written, once")]
    public async Task CommitAsync_CommitRecordCannotBeWritten_ShouldReportTheAbortOnce()
    {
        // Arrange: a manager over a log that refuses commit records, named as a coordinator names it.
        string name = UniqueName();
        await using var manager = new TransactionManager(
            new FailingCommitLog(), LockManager.Create(), VersionStore.CreateInMemory(), database: name);
        var context = await manager.BeginAsync(IsolationLevel.Snapshot);
        using var recorder = new TransactionEventRecorder(EventLevel.Warning);

        // Act
        await Should.ThrowAsync<TransactionAbortedException>(async () => await manager.CommitAsync(context));

        // Assert
        recorder.ShouldHaveNoInstrumentationError();
        var aborted = recorder.For(name).ShouldHaveSingleItem();
        aborted.EventName.ShouldBe("CommitRecordWriteFailed");
        aborted.EventId.ShouldBe(4);
        aborted.Level.ShouldBe(EventLevel.Warning);
        aborted.PayloadNames.ShouldBe(["database", "transactionSequence", "exceptionType", "exceptionMessage"]);
        aborted.Payload.ShouldBe([name, Sequence(context), typeof(InvalidOperationException).FullName, FailingCommitLog.Message]);
        context.State.ShouldBe(TransactionState.Faulted);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Transactions] - TransactionEventSource: Should report a deferred undo, its completion by the purge pass, and the pass, once each")]
    public async Task RollbackAsync_UndoFailsThenThePurgePassCompletesIt_ShouldReportEachOnce()
    {
        // Arrange: a writer whose index undo fails once.
        string name = UniqueName();
        using var storage = EventStorage.Create(name);
        await using var coordinator = new TransactionCoordinator(storage, storage.Log, storage.Records);
        var writer = await BeginWriterAsync(coordinator, storage, new FailingIndex(failures: 1));
        using var recorder = new TransactionEventRecorder(EventLevel.Verbose);

        // Act
        await coordinator.RollbackAsync(writer);
        long purged = coordinator.RunVersionPurgePass(CancellationToken.None);

        // Assert
        recorder.ShouldHaveNoInstrumentationError();
        var events = recorder.For(name);
        events.Select(e => e.EventName).ShouldBe(["UndoDeferred", "TransactionRolledBack", "DeferredUndoCompleted", "VersionPurgePass"]);

        var deferred = events[0];
        deferred.EventId.ShouldBe(11);
        deferred.Level.ShouldBe(EventLevel.Warning);
        deferred.PayloadNames.ShouldBe(["database", "transactionSequence", "exceptionType", "exceptionMessage"]);
        deferred.Payload.ShouldBe([name, Sequence(writer), typeof(IOException).FullName, FailingIndex.Message]);

        var completed = events[2];
        completed.EventId.ShouldBe(12);
        completed.Level.ShouldBe(EventLevel.Informational);
        completed.PayloadNames.ShouldBe(["database", "transactionSequence", "undone"]);
        completed.Payload!.Take(2).ShouldBe([name, Sequence(writer)]);
        ((long)completed.Payload![2]!).ShouldBeGreaterThan(0);

        var pass = events[3];
        pass.EventId.ShouldBe(18);
        pass.Level.ShouldBe(EventLevel.Verbose);
        Declared(pass.Keywords).ShouldBe(TransactionEventSource.Keywords.Purge);
        pass.PayloadNames.ShouldBe(["database", "versionsPurged", "durationMilliseconds"]);
        pass.Payload!.Take(2).ShouldBe([name, purged]);
        ((double)pass.Payload![2]!).ShouldBeGreaterThanOrEqualTo(0);
        purged.ShouldBeGreaterThanOrEqualTo((long)completed.Payload![2]!);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Transactions] - TransactionEventSource: Should report an abort record the journal rejected, once")]
    public async Task RollbackAsync_AbortRecordRejected_ShouldReportTheLostRecordOnce()
    {
        // Arrange: the coordinator's abort-record hook rejects the record, as a failed append would.
        string name = UniqueName();
        using var storage = EventStorage.Create(name);
        await using var coordinator = new TransactionCoordinator(storage, storage.Log, storage.Records);
        var writer = await BeginWriterAsync(coordinator, storage);
        coordinator.BeforeAbortRecord = _ => throw new IOException(AbortRecordFailure);
        using var recorder = new TransactionEventRecorder(EventLevel.Warning);

        // Act
        await coordinator.RollbackAsync(writer);

        // Assert: the rollback still ended the transaction; the lost record is reported.
        recorder.ShouldHaveNoInstrumentationError();
        var lost = recorder.For(name).ShouldHaveSingleItem();
        lost.EventName.ShouldBe("AbortRecordWriteFailed");
        lost.EventId.ShouldBe(13);
        lost.Level.ShouldBe(EventLevel.Warning);
        lost.PayloadNames.ShouldBe(["database", "transactionSequence", "exceptionType", "exceptionMessage"]);
        lost.Payload.ShouldBe([name, Sequence(writer), typeof(IOException).FullName, AbortRecordFailure]);
        writer.State.ShouldBe(TransactionState.RolledBack);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Transactions] - TransactionEventSource: Should report the open-time recovery analysis once, with the plan's counts")]
    public async Task AnalyzeAndScrub_JournalWithCommittedAndAbortedWriters_ShouldReportTheAnalysisOnce()
    {
        // Arrange: one committed writer and one still in flight when the process stops.
        string name = UniqueName();
        using var storage = EventStorage.Create(name);
        var coordinator = new TransactionCoordinator(storage, storage.Log, storage.Records);
        var committed = await BeginWriterAsync(coordinator, storage);
        await coordinator.CommitAsync(committed);
        var inFlight = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        await InsertAsync(coordinator, storage, inFlight);
        var images = storage.CaptureImages();
        using var reopened = EventStorage.Open(images.Data, images.Journal);
        await using var recovered = new TransactionCoordinator(reopened, reopened.Log, reopened.Records);
        using var recorder = new TransactionEventRecorder(EventLevel.Informational);

        // Act
        var plan = recovered.AnalyzeAndScrub();
        recovered.CompleteRecovery();

        // Assert
        recorder.ShouldHaveNoInstrumentationError();
        ((string)reopened.Name).ShouldBe(name);
        plan.Committed.ShouldContain(committed.Sequence);
        plan.Aborted.ShouldContain(inFlight.Sequence);
        var analyzed = recorder.For(name).ShouldHaveSingleItem();
        analyzed.EventName.ShouldBe("RecoveryAnalyzed");
        analyzed.EventId.ShouldBe(14);
        analyzed.Level.ShouldBe(EventLevel.Informational);
        analyzed.PayloadNames.ShouldBe(["database", "committed", "aborted", "maxSequence"]);
        analyzed.Payload.ShouldBe([name, plan.Committed.Count, plan.Aborted.Count, (long)plan.MaxSequence.Value]);
        await coordinator.DisposeAsync();
    }

    [Fact(DisplayName = "Cohesion Test [Database.Transactions] - TransactionEventSource: Should report a checkpoint deferred to a statement and the deferred checkpoint's failure, once each")]
    public async Task TryCheckpoint_GateHeldAndTheDeferredCheckpointFails_ShouldReportTheDeferralAndTheFailureOnce()
    {
        // Arrange: a statement holds the apply gate; the checkpoint it will run fails.
        string name = UniqueName();
        using var storage = EventStorage.Create(name);
        await using var coordinator = new TransactionCoordinator(storage, storage.Log, storage.Records);
        var holder = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var statement = await HoldApplyGateAsync(coordinator, holder, release.Task);
        using var recorder = new TransactionEventRecorder(EventLevel.Verbose);
        coordinator.BeforeCheckpoint = _ => throw new InvalidOperationException(CheckpointFailure);

        // Act
        bool ran = coordinator.TryCheckpoint(TimeSpan.Zero, CancellationToken.None);
        release.TrySetResult();
        await statement.WaitAsync(Timeout);
        coordinator.BeforeCheckpoint = null;
        var held = Should.Throw<InvalidOperationException>(() => coordinator.TryCheckpoint(TimeSpan.Zero, CancellationToken.None));
        await coordinator.CommitAsync(holder);

        // Assert
        recorder.ShouldHaveNoInstrumentationError();
        ran.ShouldBeFalse();
        held.Message.ShouldBe(CheckpointFailure);
        var events = recorder.For(name).Where(e => e.EventName is "CheckpointDeferred" or "DeferredCheckpointSkipped" or "DeferredCheckpointFailed").ToArray();
        events.Select(e => e.EventName).ShouldBe(["CheckpointDeferred", "DeferredCheckpointFailed"]);

        events[0].EventId.ShouldBe(15);
        events[0].Level.ShouldBe(EventLevel.Verbose);
        Declared(events[0].Keywords).ShouldBe(TransactionEventSource.Keywords.Checkpoints);
        events[0].PayloadNames.ShouldBe(["database"]);
        events[0].Payload.ShouldBe([name]);

        events[1].EventId.ShouldBe(17);
        events[1].Level.ShouldBe(EventLevel.Warning);
        events[1].PayloadNames.ShouldBe(["database", "exceptionType", "exceptionMessage"]);
        events[1].Payload.ShouldBe([name, typeof(InvalidOperationException).FullName, CheckpointFailure]);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Transactions] - TransactionEventSource: Should report a deferred checkpoint a storage bracket outside the gate kept from running, once")]
    public async Task TryCheckpoint_GateHeldAndABracketOpenOutsideIt_ShouldReportTheSkipOnce()
    {
        // Arrange: a statement holds the apply gate, and a storage bracket is open outside it.
        string name = UniqueName();
        using var storage = EventStorage.Create(name);
        await using var coordinator = new TransactionCoordinator(storage, storage.Log, storage.Records);
        var holder = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var statement = await HoldApplyGateAsync(coordinator, holder, release.Task);
        using var recorder = new TransactionEventRecorder(EventLevel.Verbose);
        using var outside = storage.BeginTransaction();

        // Act
        bool ran = coordinator.TryCheckpoint(TimeSpan.Zero, CancellationToken.None);
        release.TrySetResult();
        await statement.WaitAsync(Timeout);
        outside.Rollback();
        await coordinator.CommitAsync(holder);

        // Assert
        recorder.ShouldHaveNoInstrumentationError();
        ran.ShouldBeFalse();
        var skipped = recorder.For(name).Where(e => e.EventName == "DeferredCheckpointSkipped").ShouldHaveSingleItem();
        skipped.EventId.ShouldBe(16);
        skipped.Level.ShouldBe(EventLevel.Verbose);
        Declared(skipped.Keywords).ShouldBe(TransactionEventSource.Keywords.Checkpoints);
        skipped.PayloadNames.ShouldBe(["database", "reason"]);
        skipped.Payload.ShouldBe([name, "BracketOpen"]);
        recorder.For(name).ShouldNotContain(e => e.EventName == "DeferredCheckpointFailed");
    }

    [Fact(DisplayName = "Cohesion Test [Database.Transactions] - TransactionEventSource: Should report a deferred checkpoint an offline storage dropped, once")]
    public async Task TryCheckpoint_GateHeldAndTheDeferredCheckpointTakesTheStorageOffline_ShouldReportTheSkipOnce()
    {
        // Arrange: a writer's statement holds the apply gate over a dirty page, and the journal
        // device fails the next write, so the deferred checkpoint's drain takes the storage
        // offline (#1243).
        string name = UniqueName();
        using var storage = EventStorage.Create(name);
        await using var coordinator = new TransactionCoordinator(storage, storage.Log, storage.Records);
        var writer = await BeginWriterAsync(coordinator, storage);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var statement = await HoldApplyGateAsync(coordinator, writer, release.Task);
        using var recorder = new TransactionEventRecorder(EventLevel.Verbose);

        // Act: the checkpoint defers to the statement, which runs it as it ends.
        bool ran = coordinator.TryCheckpoint(TimeSpan.Zero, CancellationToken.None);
        storage.Log.Flush();
        storage.JournalStream.FailWrites = 1;
        release.TrySetResult();
        await statement.WaitAsync(Timeout);

        // Assert: a skip, not a failure held for the next TryCheckpoint to throw.
        recorder.ShouldHaveNoInstrumentationError();
        ran.ShouldBeFalse();
        var events = recorder.For(name).Where(e => e.EventName is "CheckpointDeferred" or "DeferredCheckpointSkipped" or "DeferredCheckpointFailed").ToArray();
        events.Select(e => e.EventName).ShouldBe(["CheckpointDeferred", "DeferredCheckpointSkipped"]);

        var skipped = events[1];
        skipped.EventId.ShouldBe(16);
        skipped.Level.ShouldBe(EventLevel.Verbose);
        Declared(skipped.Keywords).ShouldBe(TransactionEventSource.Keywords.Checkpoints);
        skipped.PayloadNames.ShouldBe(["database", "reason"]);
        skipped.Payload.ShouldBe([name, "StorageOffline"]);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Transactions] - TransactionEventSource: Should write nothing below its enabled level")]
    public async Task BeginAsync_WarningListener_ShouldNotWriteVerboseEvents()
    {
        // Arrange
        string name = UniqueName();
        using var storage = EventStorage.Create(name);
        await using var coordinator = new TransactionCoordinator(storage, storage.Log, storage.Records);
        using var recorder = new TransactionEventRecorder(EventLevel.Warning);

        // Act
        var context = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        await coordinator.CommitAsync(context);

        // Assert
        recorder.For(name).ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Database.Transactions] - TransactionEventSource: Should publish its transaction and lock counters")]
    public async Task Counters_EnabledWithInterval_ShouldPublishEveryCounter()
    {
        // Arrange
        string[] counters = ["current-transactions", "transactions-per-second", "commits-per-second", "rollbacks-per-second", "total-deadlocks", "lock-waits-per-second"];
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // Act
        using var recorder = new TransactionEventRecorder(EventLevel.LogAlways, counterInterval: TimeSpan.FromMilliseconds(100));
        await recorder.WaitForCountersAsync(counters, cancellation.Token);

        // Assert
        recorder.CounterNames.ShouldBeSubsetOf(counters);
        counters.ShouldBeSubsetOf(recorder.CounterNames);
    }

    /// <summary>
    /// The production lock path: every engine shares its coordinator's lock manager, in engine mode,
    /// and its uncontended request is a <see cref="LockManager.TryAcquire"/> under the hood. With no
    /// listener a granted request must allocate exactly what it did before the event source existed:
    /// nothing for a re-grant, and only the lock table's own entry for a new resource.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Transactions] - TransactionEventSource: An uncontended lock request in engine mode allocates nothing more with no listener")]
    public async Task AcquireAsync_UncontendedInEngineModeWithNoListener_ShouldAllocateNothingMore()
    {
        // Arrange
        using var storage = EventStorage.Create(UniqueName());
        await using var coordinator = new TransactionCoordinator(storage, storage.Log, storage.Records);
        var locks = coordinator.LockManager;
        var owner = new TransactionSequence(1_000_000_007);
        var held = LockResource.Entry(9, 1);
        var fresh = LockResource.Entry(9, 2);
        TransactionEventSource.Log.IsEnabled().ShouldBeFalse("A listener is attached; the check measures the disabled path.");
        Granted(locks.AcquireAsync(owner, held, LockMode.Exclusive));

        // Act: the minimum of several rounds, so a one-off runtime allocation does not count.
        const int Operations = 1_000;
        long regrant = long.MaxValue;
        long acquireNew = long.MaxValue;
        long tryAcquireNew = long.MaxValue;
        var other = new TransactionSequence(1_000_000_009);
        for (int round = 0; round < 5; round++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < Operations; i++)
            {
                Granted(locks.AcquireAsync(owner, held, LockMode.Exclusive));
            }

            regrant = Math.Min(regrant, GC.GetAllocatedBytesForCurrentThread() - before);

            before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < Operations; i++)
            {
                Granted(locks.AcquireAsync(other, fresh, LockMode.Exclusive));
                locks.ReleaseAll(other);
            }

            acquireNew = Math.Min(acquireNew, GC.GetAllocatedBytesForCurrentThread() - before);

            before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < Operations; i++)
            {
                locks.TryAcquire(other, fresh, LockMode.Exclusive);
                locks.ReleaseAll(other);
            }

            tryAcquireNew = Math.Min(tryAcquireNew, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        locks.ReleaseAll(owner);
        _output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"engine mode, {Operations} ops: re-grant {regrant} B; new resource + ReleaseAll: AcquireAsync {acquireNew} B, TryAcquire {tryAcquireNew} B"));

        // Assert
        regrant.ShouldBe(0, "An uncontended re-grant allocated.");
        acquireNew.ShouldBe(tryAcquireNew, "An uncontended AcquireAsync allocated more than the TryAcquire it runs.");
    }

    /// <summary>
    /// The standalone lock path (<see cref="LockManager.Create"/>): an uncontended request runs
    /// <c>AcquireCoreAsync</c>, the method the lock-wait and deadlock events are written from, and is
    /// granted before it reaches any of them. With no listener it must allocate only what it did
    /// before the event source existed: the closure of its cancellation registration, which the
    /// compiler creates at the method's entry because the closure captures a parameter. In an
    /// optimized build that is the only allocation, and its size is fixed: an object header and
    /// method table (16 bytes), <c>this</c> and the waiter (8 each), and the 24-byte
    /// <see cref="LockResource"/>, 56 bytes in a 64-bit process. An unoptimized (Debug) build also
    /// allocates the async state machine, which the compiler emits as a class there; the bound is
    /// checked only where it holds, and CI tests the Release build.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Transactions] - TransactionEventSource: An uncontended standalone lock request allocates only its registration closure with no listener")]
    public void AcquireAsync_UncontendedStandaloneWithNoListener_ShouldAllocateOnlyTheRegistrationClosure()
    {
        // Arrange
        const long RegistrationClosureBytes = 56;
        var locks = LockManager.Create();
        var owner = new TransactionSequence(1_000_000_011);
        var other = new TransactionSequence(1_000_000_013);
        var held = LockResource.Entry(9, 1);
        var fresh = LockResource.Entry(9, 2);
        bool optimized = typeof(LockManager).Assembly.GetCustomAttribute<DebuggableAttribute>() is not { IsJITOptimizerDisabled: true };
        TransactionEventSource.Log.IsEnabled().ShouldBeFalse("A listener is attached; the check measures the disabled path.");
        Granted(locks.AcquireAsync(owner, held, LockMode.Exclusive));

        // Act: the minimum of several rounds, so a one-off runtime allocation does not count.
        const int Operations = 1_000;
        long regrant = long.MaxValue;
        long acquireNew = long.MaxValue;
        long tryAcquireNew = long.MaxValue;
        for (int round = 0; round < 5; round++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < Operations; i++)
            {
                Granted(locks.AcquireAsync(owner, held, LockMode.Exclusive));
            }

            regrant = Math.Min(regrant, GC.GetAllocatedBytesForCurrentThread() - before);

            before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < Operations; i++)
            {
                Granted(locks.AcquireAsync(other, fresh, LockMode.Exclusive));
                locks.ReleaseAll(other);
            }

            acquireNew = Math.Min(acquireNew, GC.GetAllocatedBytesForCurrentThread() - before);

            before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < Operations; i++)
            {
                locks.TryAcquire(other, fresh, LockMode.Exclusive);
                locks.ReleaseAll(other);
            }

            tryAcquireNew = Math.Min(tryAcquireNew, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        locks.ReleaseAll(owner);
        _output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"standalone, {(optimized ? "optimized" : "unoptimized")} build, {Operations} ops: re-grant {regrant} B; new resource + ReleaseAll: AcquireAsync {acquireNew} B, TryAcquire {tryAcquireNew} B"));

        // Assert: a request costs the same over the TryAcquire it matches, granted anew or again.
        (acquireNew - tryAcquireNew).ShouldBe(regrant, "An uncontended AcquireAsync on a new resource allocated differently from a re-grant.");
        if (optimized)
        {
            Environment.Is64BitProcess.ShouldBeTrue("The closure's size is the 64-bit layout.");
            regrant.ShouldBeLessThanOrEqualTo(RegistrationClosureBytes * Operations, "An uncontended standalone re-grant allocated more than its registration closure.");
        }
    }

    private static string UniqueName() => "tx-events-" + Guid.NewGuid().ToString("N");

    /// <summary>
    /// The keywords an event declares: a listener's events also carry the runtime's reserved
    /// session bits (0xF000_0000_0000 and above), which no source declares.
    /// </summary>
    private static EventKeywords Declared(EventKeywords keywords) => (EventKeywords)((long)keywords & 0x0000_0FFF_FFFF_FFFF);

    /// <summary>
    /// Checks that a lock request was granted at once, without blocking on it: an uncontended
    /// request completes synchronously, and the measured path must not wait.
    /// </summary>
    private static void Granted(ValueTask request)
    {
        if (!request.IsCompletedSuccessfully)
        {
            throw new InvalidOperationException("The uncontended lock request did not complete synchronously.");
        }
    }

    private static long Sequence(TransactionContext context) => (long)context.Sequence.Value;

    private const string AbortRecordFailure = "Injected abort-record failure.";
    private const string CheckpointFailure = "Injected checkpoint failure.";

    /// <summary>Starts a statement of <paramref name="holder"/> that holds the apply gate until <paramref name="release"/> completes.</summary>
    private static async Task<Task> HoldApplyGateAsync(TransactionCoordinator coordinator, TransactionContext holder, Task release)
    {
        var gateHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var statement = coordinator.ApplyStatementAsync<int>(holder, async _ =>
        {
            gateHeld.TrySetResult();
            await release.ConfigureAwait(false);
            return 0;
        }).AsTask();
        await gateHeld.Task.WaitAsync(Timeout);
        return statement;
    }

    /// <summary>
    /// Begins a transaction that inserted one stamped record (and, given an index, tombstoned one
    /// index entry) and holds the row lock.
    /// </summary>
    private static async Task<TransactionContext> BeginWriterAsync(
        TransactionCoordinator coordinator, EventStorage storage, RecordVersionIndex? index = null)
    {
        var writer = await coordinator.BeginAsync(IsolationLevel.Snapshot);
        await InsertAsync(coordinator, storage, writer);
        if (index is not null)
        {
            var (pageId, slotIndex) = storage.LastInserted;
            coordinator.VersionStore.RecordIndexEntryTombstoned(writer.Sequence, index, new byte[] { 1 }, storage.PackLocation(pageId, slotIndex));
        }

        await coordinator.LockManager.AcquireAsync(writer.Sequence, Row, LockMode.Exclusive);
        return writer;
    }

    private static ValueTask<int> InsertAsync(TransactionCoordinator coordinator, EventStorage storage, TransactionContext context)
        => coordinator.ApplyStatementAsync(context, bracket =>
        {
            byte[] record = new byte[RecordVersionStamp.HeaderSize + 1];
            RecordVersionStamp.WriteWriter(record, context.Sequence);
            record[RecordVersionStamp.HeaderSize] = 42;
            var (pageId, slotIndex) = storage.Insert(bracket, record);
            coordinator.VersionStore.RecordCreated(context.Sequence, pageId, slotIndex);
            return 0;
        });

    /// <summary>Records the events and the counter names the Transactions event source writes.</summary>
    private sealed class TransactionEventRecorder : EventListener
    {
        private readonly ConcurrentQueue<EventWrittenEventArgs> _events = new();
        private readonly ConcurrentDictionary<string, bool> _counterNames = new(StringComparer.Ordinal);

        public TransactionEventRecorder(EventLevel level, IDictionary<string, string?>? arguments = null, TimeSpan? counterInterval = null)
        {
            if (counterInterval is { } interval)
            {
                arguments = new Dictionary<string, string?>(arguments ?? new Dictionary<string, string?>())
                {
                    ["EventCounterIntervalSec"] = interval.TotalSeconds.ToString(CultureInfo.InvariantCulture),
                };
            }

            EnableEvents(TransactionEventSource.Log, level, EventKeywords.All, arguments);
        }

        public IReadOnlyList<EventWrittenEventArgs> Events => _events.ToArray();

        public IReadOnlyCollection<string> CounterNames => _counterNames.Keys.ToArray();

        /// <summary>The events whose <c>database</c> payload is <paramref name="database"/>, in the order written.</summary>
        public EventWrittenEventArgs[] For(string database)
            => Events.Where(e => e.PayloadNames is { Count: > 0 } names && names[0] == "database" && Equals(e.Payload?[0], database)).ToArray();

        public void ShouldHaveNoInstrumentationError()
            => Events.ShouldNotContain(e => e.EventId == 0, "EventSource reported an instrumentation error.");

        /// <summary>Waits until every named counter has published at least one payload.</summary>
        public async Task WaitForCountersAsync(IReadOnlyCollection<string> counterNames, CancellationToken cancellationToken)
        {
            while (!counterNames.All(_counterNames.ContainsKey))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
            }
        }

        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            if (!ReferenceEquals(eventData.EventSource, TransactionEventSource.Log))
            {
                return;
            }

            if (string.Equals(eventData.EventName, "EventCounters", StringComparison.Ordinal))
            {
                if (eventData.Payload is [IDictionary<string, object?> counter, ..]
                    && counter.TryGetValue("Name", out object? counterName)
                    && counterName is string text)
                {
                    _counterNames[text] = true;
                }

                return;
            }

            _events.Enqueue(eventData);
        }
    }

    /// <summary>A transaction log whose commit records are refused, as a failed journal append would refuse them.</summary>
    private sealed class FailingCommitLog : TransactionLog
    {
        internal const string Message = "Injected commit-record failure.";

        public override ValueTask AppendBeginAsync(TransactionSequence sequence, CancellationToken cancellationToken = default) => default;

        public override ValueTask AppendCommitAsync(TransactionSequence sequence, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException(Message);

        public override ValueTask AppendAbortAsync(TransactionSequence sequence, CancellationToken cancellationToken = default) => default;
    }

    /// <summary>An index whose entry undo fails a set number of times.</summary>
    private sealed class FailingIndex : RecordVersionIndex
    {
        internal const string Message = "Injected index undo failure.";
        private int _failures;

        internal FailingIndex(int failures)
        {
            _failures = failures;
        }

        protected override ValueTask EraseCoreAsync(StorageTransaction transaction, ReadOnlyMemory<byte> key, ulong entryReference, TransactionSequence writer, CancellationToken cancellationToken)
            => default;

        protected override ValueTask ClearDeleterCoreAsync(StorageTransaction transaction, ReadOnlyMemory<byte> key, ulong entryReference, TransactionSequence writer, CancellationToken cancellationToken)
        {
            if (_failures > 0)
            {
                _failures--;
                throw new IOException(Message);
            }

            return default;
        }
    }

    // Only the record-space boundary is a test double: pages, records, brackets, durability,
    // journal replay and truncation are real Storage. Each instance is named by its test, so the
    // events of one test's database are told apart from any other's.
    private sealed class EventStorage : Storage.Storage
    {
        private readonly MemoryStream _data;
        private readonly FaultingMemoryStream _journal;
        private RecordSpace? _records;

        private EventStorage(MemoryStream data, FaultingMemoryStream journal, string? name)
            : base(StorageModel.KeyValue, new StorageStream(new SimulatedDurableFileHandle(data)), new StorageStream(new SimulatedDurableFileHandle(journal)), new StorageStream(new MemoryStream()))
        {
            _data = data;
            _journal = journal;

            // Chosen before the storage is initialized: an initialized storage never enters or
            // leaves None (owner decision 26 of 2026-10-06).
            CommitDurability = StorageCommitDurability.Synchronous;
            if (name is null)
            {
                OpenExisting(checkpointOnOpen: false);
            }
            else
            {
                InitializeNew((Name)name);
            }
        }

        internal StorageJournal Log => WriteAheadLog;

        /// <summary>Gets the journal's backing stream, whose writes a test can fail.</summary>
        internal FaultingMemoryStream JournalStream => _journal;

        internal (PageId PageId, int SlotIndex) LastInserted { get; private set; }

        internal TransactionRecordSpace Records => _records ??= new RecordSpace(this);

        internal static EventStorage Create(string name) => new(new MemoryStream(), new FaultingMemoryStream(), name);

        internal static EventStorage Open(byte[] data, byte[] journal) => new(Copy(data), Copy(journal), name: null);

        internal (byte[] Data, byte[] Journal) CaptureImages()
        {
            Flush();
            return (_data.ToArray(), _journal.ToArray());
        }

        internal (PageId PageId, int SlotIndex) Insert(StorageTransaction bracket, ReadOnlySpan<byte> data)
            => LastInserted = InsertRecord(bracket, data);

        internal ulong PackLocation(PageId pageId, int slotIndex)
            => ((ulong)(long)pageId << 16) | (ushort)slotIndex;

        private (PageId PageId, int SlotIndex) UnpackLocation(ulong location)
            => ((PageId)(long)(location >> 16), (int)(location & 0xFFFF));

        private static FaultingMemoryStream Copy(byte[] bytes)
        {
            var stream = new FaultingMemoryStream();
            stream.Write(bytes);
            stream.Position = 0;
            return stream;
        }

        private sealed class RecordSpace : TransactionRecordSpace
        {
            private readonly EventStorage _storage;

            internal RecordSpace(EventStorage storage)
            {
                _storage = storage;
            }

            protected override ReadOnlyMemory<byte> ReadCore(PageId pageId, int slotIndex) => _storage.ReadRecord(pageId, slotIndex);

            protected override void UpdateCore(StorageTransaction transaction, PageId pageId, int slotIndex, ReadOnlySpan<byte> record)
                => _storage.UpdateRecord(transaction, pageId, slotIndex, record);

            protected override void DeleteCore(StorageTransaction transaction, PageId pageId, int slotIndex)
                => _storage.DeleteRecord(transaction, pageId, slotIndex);

            protected override ulong PackLocationCore(PageId pageId, int slotIndex) => _storage.PackLocation(pageId, slotIndex);

            protected override (PageId PageId, int SlotIndex) UnpackLocationCore(ulong location) => _storage.UnpackLocation(location);
        }
    }
}
