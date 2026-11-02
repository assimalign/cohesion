using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Storage.Tests.TestObjects;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Sql.Tests;

[Collection(SqlLockWaitCollection.Name)]
public sealed class SqlTableReclamationContextTests
{
    [Fact(DisplayName = "Cohesion Test [Sql] - Context barrier: waits for the discovery pin and retains application contexts")]
    public async Task GetOpenContextsAtIdleForTesting_DiscoveryPinHeld_ShouldWaitAndRetainApplicationContexts()
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
            var capturing = new TaskCompletionSource<Thread>(TaskCreationOptions.RunContinuationsAsynchronously);

            // Act: a recorded monitor wait, rather than elapsed time, proves the barrier ran.
            capture = DedicatedTestTask.Run(() =>
            {
                capturing.SetResult(Thread.CurrentThread);
                return reclamation.GetOpenContextsAtIdleForTesting();
            });
            var thread = await capturing.Task.WaitAsync(timeout.Token);
            while (!capture.IsCompleted && (thread.ThreadState & ThreadState.WaitSleepJoin) == 0)
            {
                timeout.Token.ThrowIfCancellationRequested();
                await Task.Yield();
            }
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
        reclamation.GetOpenContextsAtIdleForTesting().ShouldBeEmpty();
    }

    // Hold the existing locks from tests without adding callbacks to the production discovery path.
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_runLock")]
    private static extern ref object RunLock(SqlTableReclamation reclamation);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_readersLock")]
    private static extern ref object ReadersLock(SqlTableReclamation reclamation);
}
