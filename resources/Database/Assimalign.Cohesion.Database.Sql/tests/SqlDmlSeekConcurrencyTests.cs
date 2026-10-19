using System;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Sql.Tests;

[Collection(SqlDmlSeekCollection.Name)]
public sealed class SqlDmlSeekConcurrencyTests
{
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - DML seek: snapshot lock races retain first-updater-wins and release resources (#1274)")]
    public async Task ExecuteAsync_SeekThenRowLockWait_ShouldExerciseCommitRollbackCancellationAndDisposal()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = SqlDatabaseEngine.Create("dml-seek-races", new());
        var database = await engine.CreateDatabaseAsync("db", timeout.Token);
        await using var setup = await database.CreateSessionAsync(timeout.Token);
        await setup.ExecuteAsync("CREATE TABLE t (id INT PRIMARY KEY, v INT)", cancellationToken: timeout.Token);
        await setup.ExecuteAsync("INSERT INTO t VALUES (1,0)", cancellationToken: timeout.Token);
        using var waits = new LockWaitListener(database.Name);
        int waitCount = 0;
        waits.Observed = written => { if (written.EventName == "LockWaitStart") { Interlocked.Increment(ref waitCount); } };
        int[] exercised = new int[16];

        // Act: run two passes over every operation/isolation/outcome scenario.
        // The event confirms the row-lock wait; the cancellation token guards against hangs.
        for (int iteration = 0; iteration < 2 * exercised.Length; iteration++)
        {
            int scenario = iteration % exercised.Length;
            bool delete = (scenario & 1) != 0;
            var isolation = (scenario & 2) != 0 ? IsolationLevel.ReadCommitted : IsolationLevel.Snapshot;
            int outcome = scenario / 4;
            await using var holderSession = await database.CreateSessionAsync(timeout.Token);
            await using var holder = await holderSession.BeginTransactionAsync(isolation, timeout.Token);
            await holderSession.ExecuteAsync("UPDATE t SET v = v + 1 WHERE id = 1", cancellationToken: timeout.Token);
            await using var contender = await database.CreateSessionAsync(timeout.Token);
            await using var transaction = await contender.BeginTransactionAsync(isolation, timeout.Token);
            using var canceled = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
            string sql = delete ? "DELETE FROM t WHERE id = 1" : "UPDATE t SET v = v + 1 WHERE id = 1";
            var pending = contender.ExecuteAsync(sql, cancellationToken: canceled.Token).AsTask();
            try
            {
                await waits.WaitForWaitAsync(transaction.Context.Sequence, timeout.Token);
                if (outcome == 0)
                {
                    await holder.CommitAsync(timeout.Token);
                    await Should.ThrowAsync<DatabaseTransactionAbortedException>(async () => await pending);
                    await transaction.RollbackAsync(timeout.Token);
                }
                else if (outcome == 1)
                {
                    canceled.Cancel();
                    await Should.ThrowAsync<OperationCanceledException>(async () => await pending);
                    await transaction.RollbackAsync(timeout.Token);
                    await holder.RollbackAsync(timeout.Token);
                }
                else
                {
                    await holder.RollbackAsync(timeout.Token);
                    (await pending).AffectedCount.ShouldBe(1);
                    var metrics = contender.LastStatementMetrics.ShouldNotBeNull();
                    metrics.AccessPath.ShouldStartWith("seek:");
                    metrics.RecordsExamined.ShouldBe(1);
                    if (outcome == 2) { await transaction.RollbackAsync(timeout.Token); }
                    else { await contender.DisposeAsync(); }
                }
            }
            finally
            {
                // Drain even on assertion/timeout failure; no started statement outlives the test.
                canceled.Cancel();
                if (holder.State == TransactionState.Active) { await holder.RollbackAsync(CancellationToken.None); }
                try { await pending; }
                catch (DatabaseException) { }
                catch (OperationCanceledException) { }
            }
            exercised[scenario]++;

            // Assert each round: no retained tree latch or transaction grant blocks the next writer.
            (await setup.ExecuteAsync("UPDATE t SET v = v + 1 WHERE id = 1", cancellationToken: timeout.Token)).AffectedCount.ShouldBe(1);
        }

        // Assert
        exercised.ShouldAllBe(count => count == 2);
        Volatile.Read(ref waitCount).ShouldBeGreaterThanOrEqualTo(2 * exercised.Length);
        database.Coordinator.PairedTransactionCount.ShouldBe(0);
    }
}
