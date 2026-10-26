using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Sql.Tests;

[Collection(SqlDmlSeekCollection.Name)]
public sealed class SqlDmlSourceConcurrencyTests
{
    [Fact(DisplayName = "Cohesion Test [SqlEngine] - Joined DML: snapshot lock races retain first-updater-wins and release resources (#1714)")]
    public async Task ExecuteAsync_AssociationThenRowLockWait_ShouldExerciseCommitRollbackCancellationAndDisposal()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var engine = SqlDatabaseEngine.Create("dml-source-races", new());
        var database = await engine.CreateDatabaseAsync("db", timeout.Token);
        await using var setup = await database.CreateSessionAsync(timeout.Token);
        await setup.ExecuteAsync("CREATE TABLE t (id INT PRIMARY KEY, v INT)", cancellationToken: timeout.Token);
        await setup.ExecuteAsync("INSERT INTO t VALUES (1,0)", cancellationToken: timeout.Token);
        await setup.ExecuteAsync("CREATE TABLE s (id INT,v INT)", cancellationToken: timeout.Token);
        await setup.ExecuteAsync("INSERT INTO s VALUES(1,7)", cancellationToken: timeout.Token);
        int collected = 0;
        SqlPlanExecutor.DmlAssociationsCollected = () => Interlocked.Increment(ref collected);
        using var waits = new LockWaitListener(database.Name);
        int waitCount = 0;
        waits.Observed = written => { if (written.EventName == "LockWaitStart") { Interlocked.Increment(ref waitCount); } };
        int[] exercised = new int[16];

        // Act: run for at least a second and until every operation/isolation/outcome raced twice.
        // The event confirms the row-lock wait; the cancellation token guards against hangs.
        var elapsed = Stopwatch.StartNew();
        int iteration = 0;
        try
        {
            while (elapsed.Elapsed < TimeSpan.FromSeconds(1) || Array.Exists(exercised, count => count < 2))
            {
                int scenario = iteration % exercised.Length;
                bool delete = (scenario & 1) != 0;
                var isolation = (scenario & 2) != 0 ? IsolationLevel.ReadCommitted : IsolationLevel.Snapshot;
                int outcome = scenario / 4;
                await using var holderSession = await database.CreateSessionAsync(timeout.Token);
                await using var holder = await holderSession.BeginTransactionAsync(isolation, timeout.Token);
                await holderSession.ExecuteAsync("UPDATE t SET id=2,v=v+1 WHERE id=1", cancellationToken: timeout.Token);
                await using var contender = await database.CreateSessionAsync(timeout.Token);
                await using var transaction = await contender.BeginTransactionAsync(isolation, timeout.Token);
                using var canceled = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
                string sql = delete ? "DELETE FROM t USING s WHERE t.id=s.id RETURNING t.id,s.v"
                    : "UPDATE t SET id=t.id+10,v=s.v FROM s WHERE t.id=s.id RETURNING t.id,s.v";
                var pending = contender.ExecuteAsync(sql, cancellationToken: canceled.Token).AsTask();
                try
                {
                    await waits.WaitForWaitAsync(transaction.Context.Sequence, timeout.Token);
                    await setup.ExecuteAsync("UPDATE s SET v=v+1", cancellationToken: timeout.Token);
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
                        await using var result = (await pending).ShouldBeAssignableTo<DatabaseResultSet>();
                        result.AffectedCount.ShouldBe(1);
                        var values = new List<object?>();
                        await foreach (var row in result.GetRowsAsync(timeout.Token)) { values.Add(row.GetValue(1)); }
                        values.ShouldBe([7 + iteration]);
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
                (await setup.ExecuteAsync("UPDATE t SET id=1,v=0", cancellationToken: timeout.Token)).AffectedCount.ShouldBe(1);
                iteration++;
            }

        }
        finally { SqlPlanExecutor.DmlAssociationsCollected = null; }

        // Assert
        exercised.ShouldAllBe(count => count >= 2);
        Volatile.Read(ref collected).ShouldBeGreaterThanOrEqualTo(iteration);
        Volatile.Read(ref waitCount).ShouldBeGreaterThanOrEqualTo(2 * exercised.Length);
        database.Coordinator.PairedTransactionCount.ShouldBe(0);
    }
}
