using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Indexing;
using Assimalign.Cohesion.Database.KeyValuePair.Internal;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.KeyValuePair.Tests;

using static KeyValueTestHarness;

/// <summary>
/// Runs the read-committed stress probe alone. It keeps four readers, two writers and a purge
/// thread busy, so beside it a timing-sensitive test elsewhere in the suite (a server handshake or
/// idle timeout) can starve on a small CI runner.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class KeyValueReadCommittedStressCollection
{
    /// <summary>
    /// The collection name.
    /// </summary>
    public const string Name = "KeyValuePair read-committed stress";
}

/// <summary>
/// The snapshot pin of a command in a read-committed transaction (#1363). The command reads
/// through the snapshot captured when it started, and that snapshot can keep a floor below every
/// active sequence: a writer that began before the transaction was still in flight then. A
/// read-committed transaction holds the version purge's bound only at its own sequence, so once
/// that writer committed, a purge pass reclaimed the versions it tombstoned while the command still
/// had to read them: a scan skipped those keys, and a write that waited for a key's lock found no
/// version of the key at all. The session now begins a snapshot transaction before the command
/// captures its snapshot and ends it with the command, as the Documents, Graph and Blob operations
/// do; the command itself still runs under the transaction's own context.
/// </summary>
[Collection(KeyValueReadCommittedStressCollection.Name)]
public sealed class KeyValueReadCommittedSnapshotPinTests
{
    private readonly ITestOutputHelper _output;

    public KeyValueReadCommittedSnapshotPinTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// A delete resolves the key through its snapshot after it waited for the key's lock, so the
    /// lock holds the command there while the older writer commits and the purge runs. Before the
    /// fix the pass reclaimed the version the command's snapshot sees, the command found no version
    /// of a key that exists, and it deleted nothing without reporting the conflict.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - Read committed: A command that waits for its key's lock keeps the version its snapshot sees while an older writer commits and the purge runs (#1363)")]
    public async Task Delete_WaitsWhileAnOlderWriterCommitsAndThePurgeRuns_ShouldReportTheConflict()
    {
        // Arrange: the writer replaces the key and stays in flight; the reader's read-committed
        // transaction begins after it.
        var (engine, database) = await CreateAsync();
        await using var _ = engine;
        var coordinator = database.Coordinator;
        await using var writerSession = await database.CreateSessionAsync();
        await using var readerSession = await database.CreateSessionAsync();
        await database.PutAsync(writerSession, Bytes("k"), Bytes("v1"), cancellationToken: TestTimeout.Token());
        var writer = await writerSession.BeginTransactionAsync(IsolationLevel.Snapshot, TestTimeout.Token());
        await database.PutAsync(writerSession, Bytes("k"), Bytes("v2"), cancellationToken: TestTimeout.Token());
        var reader = await readerSession.BeginTransactionAsync(IsolationLevel.ReadCommitted, TestTimeout.Token());

        // A read-committed context queues for the key's lock behind the writer, so the writer's
        // commit grants it the lock rather than the reader's delete queued after it. It adds only
        // its own sequence to the prune bound.
        var blocker = await coordinator.BeginAsync(IsolationLevel.ReadCommitted, TestTimeout.Token());
        var blockerGranted = coordinator.LockManager.AcquireAsync(blocker.Sequence, KeyLock("k"), LockMode.Exclusive, TestTimeout.Token()).AsTask();
        blockerGranted.IsCompleted.ShouldBeFalse();
        int openBefore = coordinator.GetOpenContexts().Count;
        var pending = database.TryDeleteAsync(readerSession, Bytes("k"), cancellationToken: TestTimeout.Token()).AsTask();
        pending.IsCompleted.ShouldBeFalse();
        int openWhileWaiting = coordinator.GetOpenContexts().Count;

        // Act: the writer commits and a purge pass runs while the delete waits for the lock.
        await writer.CommitAsync(TestTimeout.Token());
        await blockerGranted;
        long prunedWhileWaiting = coordinator.RunVersionPurgePass(CancellationToken.None);
        await coordinator.RollbackAsync(blocker, CancellationToken.None);
        var failure = await Should.ThrowAsync<Exception>(() => pending);
        int openAfter = coordinator.GetOpenContexts().Count;
        long prunedAfterTheCommand = coordinator.RunVersionPurgePass(CancellationToken.None);
        var refreshed = await database.GetAsync(readerSession, Bytes("k"), TestTimeout.Token());
        var stateAfter = reader.State;
        await reader.CommitAsync(TestTimeout.Token());

        // Assert: the command still found the version the writer replaced, so it reported the
        // writer's commit as the conflict it is (first-updater-wins) instead of finding no key;
        // its pin held that version until it ended, and the transaction, still active, did not
        // hold it after that.
        failure.ShouldBeOfType<DatabaseTransactionAbortedException>().Message.ShouldContain("first-updater-wins", Case.Sensitive);
        prunedWhileWaiting.ShouldBe(0);
        openWhileWaiting.ShouldBe(openBefore + 1);
        openAfter.ShouldBe(openBefore - 2);
        prunedAfterTheCommand.ShouldBe(1);
        Text(refreshed.ShouldNotBeNull().Value).ShouldBe("v2");
        stateAfter.ShouldBe(TransactionState.Active);
    }

    /// <summary>
    /// The pin ends with the command whether it completes or is canceled, and only a
    /// read-committed command begins one. (A failed command ends it too: the conflict above.)
    /// </summary>
    /// <param name="isolationLevel">The explicit transaction's isolation level.</param>
    /// <param name="canceled">Whether the command is canceled while it waits for the key's lock.</param>
    [Theory(DisplayName = "Cohesion Test [Database.KeyValuePair] - Read committed: A command's snapshot pin ends with the command on every path (#1363)")]
    [InlineData(IsolationLevel.ReadCommitted, false)]
    [InlineData(IsolationLevel.ReadCommitted, true)]
    [InlineData(IsolationLevel.Snapshot, false)]
    [InlineData(IsolationLevel.Snapshot, true)]
    public async Task Command_EndsAnyWay_ShouldEndItsSnapshotPin(IsolationLevel isolationLevel, bool canceled)
    {
        // Arrange: a lock on the key holds the command after any pin began.
        var (engine, database) = await CreateAsync();
        await using var _ = engine;
        var coordinator = database.Coordinator;
        await using var session = await database.CreateSessionAsync();
        var transaction = await session.BeginTransactionAsync(isolationLevel, TestTimeout.Token());
        var blocker = await coordinator.BeginAsync(IsolationLevel.ReadCommitted, TestTimeout.Token());
        await coordinator.LockManager.AcquireAsync(blocker.Sequence, KeyLock("k"), LockMode.Exclusive, TestTimeout.Token());
        int openBefore = coordinator.GetOpenContexts().Count;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        // Act
        var pending = database.PutAsync(session, Bytes("k"), Bytes("v"), cancellationToken: cancellation.Token).AsTask();
        pending.IsCompleted.ShouldBeFalse();
        int openWhileWaiting = coordinator.GetOpenContexts().Count;
        if (canceled)
        {
            cancellation.Cancel();
        }
        else
        {
            await coordinator.RollbackAsync(blocker, CancellationToken.None);
        }

        Exception? failure = null;
        try
        {
            await pending;
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        int openAfter = coordinator.GetOpenContexts().Count;
        if (blocker.State == TransactionState.Active)
        {
            await coordinator.RollbackAsync(blocker, CancellationToken.None);
        }

        var stateAfter = transaction.State;
        await transaction.CommitAsync(TestTimeout.Token());

        // Assert: one pin while a read-committed command ran, none after it, whatever ended it.
        openWhileWaiting.ShouldBe(isolationLevel == IsolationLevel.ReadCommitted ? openBefore + 1 : openBefore);
        openAfter.ShouldBe(canceled ? openBefore : openBefore - 1);
        if (canceled)
        {
            failure.ShouldBeAssignableTo<OperationCanceledException>();
        }
        else
        {
            failure.ShouldBeNull();
        }

        stateAfter.ShouldBe(TransactionState.Active);
    }

    /// <summary>
    /// The adversarial probe of #1363 as a bounded guard: read-committed range scans and point
    /// reads under writers that replace and delete keys and a purge loop. Before the fix a
    /// five-second run lost 566 keys in 177 scans. Set <c>COHESION_DATABASE_RC_PROBE_SECONDS</c>
    /// to run it longer.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.KeyValuePair] - Read committed: Commands under older writers and a purge loop never lose or repeat a key (#1363)")]
    public async Task Commands_UnderOlderWritersAndAPurgeLoop_ShouldNeverLoseOrRepeatAKey()
    {
        // Arrange
        double seconds = double.TryParse(Environment.GetEnvironmentVariable("COHESION_DATABASE_RC_PROBE_SECONDS"), out double configured) && configured > 0
            ? configured
            : 2;

        // Act
        var counts = await KeyValueReadCommittedProbe.RunAsync(TimeSpan.FromSeconds(seconds));
        string report = $"KeyValuePair read-committed probe, {seconds} s:{Environment.NewLine}{counts}";
        _output.WriteLine(report);
        if (Environment.GetEnvironmentVariable("COHESION_DATABASE_RC_PROBE_REPORT") is { Length: > 0 } path)
        {
            File.AppendAllText(path, report + Environment.NewLine);
        }

        // Assert: the purge did reclaim while the commands ran, and no command lost a key.
        counts.Reclaimed.ShouldBeGreaterThan(0, report);
        counts.Anomalies.ShouldBe(0, report);
    }

    private static LockResource KeyLock(string key)
        => LockResource.Entry(KeyValueOperationExecutor.KeySpaceObjectId, new IndexKey(Bytes(key)).Hash());
}
