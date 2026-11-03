using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Storage.Tests.TestObjects;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Sql.Tests;

[Collection(SqlLockWaitCollection.Name)]
public sealed class SqlTableReclamationContextTests
{
    [Fact(DisplayName = "Cohesion Test [Sql] - Context barrier: waits while reclamation is queued at the data gate (#1283, #1764)")]
    public async Task CaptureOpenContextsForTesting_ReclamationQueuedAtGate_ShouldWaitWithoutLockInversion()
    {
        // Arrange: the holder owns only the data gate, on its own thread.
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var queued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var barrierWaiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var engine = SqlDatabaseEngine.Create("context-barrier-gate", new()
        {
            CheckpointJournalSize = 0,
            CheckpointInterval = TimeSpan.FromHours(1),
            MaintenanceInterval = TimeSpan.FromHours(1),
        });
        var database = await engine.CreateDatabaseAsync("db", guard.Token);
        await using var session = await database.CreateSessionAsync(guard.Token);
        await session.ExecuteAsync("CREATE TABLE retired(id INT)", cancellationToken: guard.Token);
        await session.ExecuteAsync("INSERT INTO retired VALUES(1)", cancellationToken: guard.Token);
        var reclamation = database.TableReclamation;
        int visits = 0;
        reclamation.BeforeBatch = (_, _) =>
        {
            if (Interlocked.Increment(ref visits) == 1)
            { SynchronizationContext.SetSynchronizationContext(new GateWaitSignal(queued)); }
        };
        var holder = DedicatedTestTask.Run(() =>
        {
            using var lease = TransactionCheckpointInterlock.ForStorage(database.DataStorage).Enter("Test", cancellationToken: guard.Token);
            held.Set();
            release.Wait(guard.Token);
        });
        Task? running = null;
        Task<IReadOnlyList<TransactionContext>>? capture = null;
        TransactionContext? application = null;
        try
        {
            held.Wait(guard.Token);
            await session.ExecuteAsync("DROP TABLE retired", cancellationToken: guard.Token);
            running = DedicatedTestTask.Run(() => reclamation.RunBatch(guard.Token));
            await queued.Task.WaitAsync(guard.Token);
            application = await database.Coordinator.BeginAsync(IsolationLevel.ReadCommitted, guard.Token);

            // Act: this thread holds neither the data gate nor a catalog monitor.
            capture = DedicatedTestTask.Run(() => reclamation.CaptureOpenContextsForTesting(() => barrierWaiting.TrySetResult()));
            await barrierWaiting.Task.WaitAsync(guard.Token);
            capture.IsCompleted.ShouldBeFalse();
            release.Set();
            var contexts = await capture.WaitAsync(guard.Token);

            // Assert: reclamation has left its gate/run lock and only the application remains.
            contexts.ShouldHaveSingleItem().ShouldBeSameAs(application);
            visits.ShouldBeGreaterThan(0);
        }
        finally
        {
            release.Set();
            await holder.WaitAsync(guard.Token);
            if (running is not null) { await running.WaitAsync(guard.Token); }
            if (capture is not null) { await capture.WaitAsync(guard.Token); }
            reclamation.BeforeBatch = null;
            if (application is not null) { await database.Coordinator.RollbackAsync(application, CancellationToken.None); }
        }
        reclamation.CaptureOpenContextsForTesting().ShouldBeEmpty();
    }

    // The next managed wait after BeforeBatch is data admission. The CLR reports
    // entry explicitly; restore the worker's original context before waiting.
    private sealed class GateWaitSignal : SynchronizationContext
    {
        private readonly TaskCompletionSource _queued;
        private readonly SynchronizationContext? _previous;

        internal GateWaitSignal(TaskCompletionSource queued)
        {
            _queued = queued;
            _previous = Current;
            SetWaitNotificationRequired();
        }

        public override int Wait(IntPtr[] waitHandles, bool waitAll, int millisecondsTimeout)
        {
            SetSynchronizationContext(_previous);
            _queued.TrySetResult();
            return base.Wait(waitHandles, waitAll, millisecondsTimeout);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Context barrier: waits for the discovery pin and retains application contexts")]
    public async Task CaptureOpenContextsForTesting_DiscoveryPinHeld_ShouldWaitAndRetainApplicationContexts()
    {
        // Arrange: hold discovery between opening its fence and taking the reader lock.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var releaseRun = new ManualResetEventSlim();
        using var releaseReaders = new ManualResetEventSlim();
        var runHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readersHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var engine = SqlDatabaseEngine.Create("context-barrier", new());
        var database = await engine.CreateDatabaseAsync("db", timeout.Token);
        await using var session = await database.CreateSessionAsync(timeout.Token);
        (await session.ExecuteAsync("CREATE TABLE retired(id INT)", cancellationToken: timeout.Token)).Status.ShouldBe(DatabaseResultStatus.Success);
        var reclamation = database.TableReclamation;
        var run = DedicatedTestTask.Run(() =>
        {
            lock (RunLock(reclamation))
            {
                runHeld.SetResult();
                releaseRun.Wait(timeout.Token);
            }
        });
        Task? readers = null;
        Task<IReadOnlyList<TransactionContext>>? capture = null;
        TransactionContext? application = null;
        TransactionContext? statementPin = null;
        try
        {
            await runHeld.Task.WaitAsync(timeout.Token);
            (await session.ExecuteAsync("DROP TABLE retired", cancellationToken: timeout.Token)).Status.ShouldBe(DatabaseResultStatus.Success);
            readers = DedicatedTestTask.Run(() =>
            {
                lock (ReadersLock(reclamation))
                {
                    readersHeld.SetResult();
                    releaseReaders.Wait(timeout.Token);
                }
            });
            await readersHeld.Task.WaitAsync(timeout.Token);
            releaseRun.Set();
            TransactionContext? discovery = null;
            while (discovery is null)
            {
                timeout.Token.ThrowIfCancellationRequested();
                var contexts = database.Coordinator.GetOpenContexts();
                if (contexts.Count != 0) { discovery = contexts.ShouldHaveSingleItem(); }
                else { await Task.Yield(); }
            }
            discovery.State.ShouldBe(TransactionState.Active);
            application = await database.Coordinator.BeginAsync(IsolationLevel.ReadCommitted, timeout.Token);
            statementPin = await database.Coordinator.BeginSnapshotPinAsync(timeout.Token);
            var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            // Act: a recorded monitor wait, rather than elapsed time, proves the barrier ran.
            capture = DedicatedTestTask.Run(() =>
            {
                return reclamation.CaptureOpenContextsForTesting(() => waiting.TrySetResult());
            });
            await waiting.Task.WaitAsync(timeout.Token);
            bool waited = !capture.IsCompleted;
            releaseReaders.Set();
            var retained = await capture.WaitAsync(timeout.Token);

            // Assert: only the identified discovery context ended; application pins still count.
            waited.ShouldBeTrue();
            discovery.State.ShouldBe(TransactionState.RolledBack);
            retained.ShouldBe(new[] { application, statementPin }, ignoreOrder: true);
            retained.ShouldNotContain(discovery);
        }
        finally
        {
            releaseRun.Set();
            releaseReaders.Set();
            await run.WaitAsync(timeout.Token);
            if (readers is not null) { await readers.WaitAsync(timeout.Token); }
            if (capture is not null) { await capture.WaitAsync(timeout.Token); }
            if (statementPin is not null) { await database.Coordinator.RollbackAsync(statementPin, CancellationToken.None); }
            if (application is not null) { await database.Coordinator.RollbackAsync(application, CancellationToken.None); }
        }
        reclamation.CaptureOpenContextsForTesting().ShouldBeEmpty();
    }

    // Hold the existing locks from tests without adding callbacks to the production discovery path.
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_runLock")]
    private static extern ref object RunLock(SqlTableReclamation reclamation);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_readersLock")]
    private static extern ref object ReadersLock(SqlTableReclamation reclamation);
}
