using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Sql.Tests;

[Collection(SqlUpsertConcurrencyCollection.Name)]
public sealed class SqlMergeConcurrencyTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - MERGE: gated target changes follow PostgreSQL EPQ or Snapshot refusal (#1715)")]
    [InlineData(false, "joined")]
    [InlineData(true, "joined")]
    [InlineData(false, "upsert")]
    [InlineData(true, "upsert")]
    [InlineData(false, "merge")]
    [InlineData(true, "merge")]
    [InlineData(false, "cascade-update")]
    [InlineData(true, "cascade-update")]
    [InlineData(false, "keep")]
    [InlineData(true, "keep")]
    [InlineData(false, "break")]
    [InlineData(true, "break")]
    [InlineData(false, "delete")]
    [InlineData(true, "delete")]
    [InlineData(false, "twice")]
    [InlineData(true, "twice")]
    [InlineData(false, "source-update")]
    [InlineData(true, "source-update")]
    [InlineData(false, "source-delete")]
    [InlineData(true, "source-delete")]
    [InlineData(false, "insert")]
    [InlineData(true, "insert")]
    [InlineData(false, "rollback")]
    [InlineData(true, "rollback")]
    [InlineData(false, "failed-update-delete")]
    [InlineData(true, "failed-update-delete")]
    public async Task ExecuteAsync_ConcurrentTargetChange_ShouldRechooseOrderedActions(bool snapshot, string scenario)
    {
        // Arrange: stop after selection, before taking any target row lock.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var release = new ManualResetEventSlim();
        var selected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var engine = SqlDatabaseEngine.Create("merge-epq", new());
        var database = await engine.CreateDatabaseAsync("db", timeout.Token);
        await using var writer = await database.CreateSessionAsync(timeout.Token);
        await using var contender = await database.CreateSessionAsync(timeout.Token);
        if (scenario == "cascade-update")
        {
            await ExecuteAsync(writer, "CREATE TABLE p(id INT PRIMARY KEY)", timeout.Token);
            await ExecuteAsync(writer, "INSERT INTO p VALUES(10),(20),(22)", timeout.Token);
        }
        await ExecuteAsync(writer, scenario == "cascade-update"
            ? "CREATE TABLE t(id INT PRIMARY KEY,v INT DEFAULT 20 REFERENCES p(id) ON DELETE SET DEFAULT)"
            : "CREATE TABLE t(id INT PRIMARY KEY,v INT)", timeout.Token);
        await ExecuteAsync(writer, "CREATE TABLE s(id INT,v INT)", timeout.Token);
        if (scenario != "insert") { await ExecuteAsync(writer, "INSERT INTO t VALUES(1,10)", timeout.Token); }
        if (!scenario.StartsWith("source-", StringComparison.Ordinal))
        { await ExecuteAsync(writer, "INSERT INTO s VALUES(1,99)", timeout.Token); }
        await using var transaction = await contender.BeginTransactionAsync(snapshot ? IsolationLevel.Snapshot : IsolationLevel.ReadCommitted, timeout.Token);
        int rechecks = 0;
        SqlPlanExecutor.MergeActionsSelected = _ => { if (selected.TrySetResult()) { release.Wait(timeout.Token); } };
        SqlPlanExecutor.MergeTargetRechecked = _ => rechecks++;
        var pending = Task.Run(async () => await contender.ExecuteAsync("MERGE INTO t USING s ON t.id=s.id " +
            "WHEN MATCHED AND t.v<15 THEN UPDATE SET v=t.v+1 " +
            "WHEN MATCHED AND t.v<30 THEN UPDATE SET v=t.v+2 " +
            "WHEN MATCHED THEN UPDATE SET v=t.v+4 " +
            "WHEN NOT MATCHED BY SOURCE THEN UPDATE SET v=t.v+3 " +
            "WHEN NOT MATCHED THEN INSERT VALUES(s.id,s.v) RETURNING t.id,t.v,s.id,merge_action()", cancellationToken: timeout.Token));
        try
        {
            await selected.Task.WaitAsync(timeout.Token);

            // Act: every competing write is decided before the target lock/recheck.
            if (scenario == "rollback" || scenario == "failed-update-delete")
            {
                await using var holder = await writer.BeginTransactionAsync(IsolationLevel.ReadCommitted, timeout.Token);
                if (scenario == "rollback")
                {
                    await ExecuteAsync(writer, "UPDATE t SET v=50", timeout.Token);
                    await holder.RollbackAsync(timeout.Token);
                }
                else
                {
                    await Should.ThrowAsync<DatabaseException>(async () => await writer.ExecuteAsync(
                        "UPDATE t SET v=50 RETURNING 1/0", cancellationToken: timeout.Token));
                    await ExecuteAsync(writer, "DELETE FROM t", timeout.Token);
                    await holder.CommitAsync(timeout.Token);
                }
            }
            else if (scenario is "joined" or "upsert" or "merge" or "cascade-update")
            {
                string update = scenario switch
                {
                    "joined" => "UPDATE t SET v=x.v FROM (SELECT 1 AS id,20 AS v) AS x WHERE t.id=x.id",
                    "upsert" => "INSERT INTO t VALUES(1,20) ON CONFLICT(id) DO UPDATE SET v=EXCLUDED.v",
                    "merge" => "MERGE INTO t USING (SELECT 1 AS id,20 AS v) AS x ON t.id=x.id WHEN MATCHED THEN UPDATE SET v=x.v",
                    _ => "DELETE FROM p WHERE id=10",
                };
                await ExecuteAsync(writer, update, timeout.Token);
            }
            else if (scenario.Contains("delete", StringComparison.Ordinal)) { await ExecuteAsync(writer, "DELETE FROM t", timeout.Token); }
            else if (scenario == "insert") { await ExecuteAsync(writer, "INSERT INTO t VALUES(1,50)", timeout.Token); }
            else
            {
                if (scenario == "twice")
                {
                    // The older transaction publishes the second link, so sequence order
                    // differs from physical replacement order. The suspended statement pin
                    // must protect both links from maintenance.
                    await using var earlySession = await database.CreateSessionAsync(timeout.Token);
                    await using var early = await earlySession.BeginTransactionAsync(IsolationLevel.ReadCommitted, timeout.Token);
                    await ExecuteAsync(writer, "UPDATE t SET v=20", timeout.Token);
                    await ExecuteAsync(earlySession, "UPDATE t SET v=30", timeout.Token);
                    await early.CommitAsync(timeout.Token);
                    int retained = database.DataStorage.RowVersionLinks.Count;
                    retained.ShouldBeGreaterThanOrEqualTo(2);
                    database.DataStorage.RowVersionLinks.Prune(database.Coordinator.PruneBound, timeout.Token);
                    database.DataStorage.RowVersionLinks.Count.ShouldBe(retained);
                }
                else { await ExecuteAsync(writer, scenario is "break" or "source-update" ? "UPDATE t SET id=2,v=20" : "UPDATE t SET v=20", timeout.Token); }
            }
            release.Set();

            // Assert: Snapshot refuses changed targets, while competing INSERT is ordinary uniqueness.
            if (scenario == "insert")
            {
                var failure = await Should.ThrowAsync<DatabaseException>(async () => await pending);
                failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS03037");
                await transaction.RollbackAsync(timeout.Token);
            }
            else if (snapshot && scenario != "rollback")
            {
                await Should.ThrowAsync<DatabaseTransactionAbortedException>(async () => await pending);
                await transaction.RollbackAsync(timeout.Token);
            }
            else
            {
                var result = await pending;
                var rows = await RowsAsync(result, timeout.Token);
                var expected = scenario switch
                {
                    "keep" or "joined" or "upsert" or "merge" or "cascade-update" => new[] { "1,22,1,UPDATE" },
                    "break" => ["1,99,1,INSERT", "2,23,,UPDATE"],
                    "delete" or "failed-update-delete" => ["1,99,1,INSERT"],
                    "twice" => ["1,34,1,UPDATE"],
                    "source-update" => ["2,23,,UPDATE"],
                    "source-delete" => [],
                    _ => ["1,11,1,UPDATE"],
                };
                rows.Select(row => string.Join(',', row)).Order().ShouldBe(expected.Order());
                result.AffectedCount.ShouldBe(expected.Length);
                rechecks.ShouldBe(scenario == "rollback" ? 0 : 1);
                await transaction.CommitAsync(timeout.Token);
                (await RowsAsync(await writer.ExecuteAsync("SELECT id,v FROM t ORDER BY id", cancellationToken: timeout.Token), timeout.Token))
                    .Select(row => string.Join(',', row)).ShouldBe(expected.Order().Select(row => string.Join(',', row.Split(',').Take(2))));
            }
            contender.LastStatementMetrics!.HashMemoryBytes.ShouldBe(0);
        }
        finally
        {
            release.Set();
            SqlPlanExecutor.MergeActionsSelected = null;
            SqlPlanExecutor.MergeTargetRechecked = null;
            timeout.Cancel();
            await ((Task)pending).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - MERGE: row wait cancellation, exceptions, no-op and disposal release statement resources (#1715)")]
    [InlineData("cancel")]
    [InlineData("no-op")]
    [InlineData("exception")]
    [InlineData("dispose")]
    public async Task ExecuteAsync_RecheckExit_ShouldReleaseOwnedResources(string outcome)
    {
        // Arrange: a real lock wait orders selection before the holder's commit.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        await using var engine = SqlDatabaseEngine.Create("merge-exits", new());
        var database = await engine.CreateDatabaseAsync("db", timeout.Token);
        await using var writer = await database.CreateSessionAsync(timeout.Token);
        await using var contender = await database.CreateSessionAsync(timeout.Token);
        await ExecuteAsync(writer, "CREATE TABLE t(id INT PRIMARY KEY,v INT)", timeout.Token);
        await ExecuteAsync(writer, "INSERT INTO t VALUES(1,10)", timeout.Token);
        await using var holder = await writer.BeginTransactionAsync(IsolationLevel.ReadCommitted, timeout.Token);
        await ExecuteAsync(writer, "UPDATE t SET v=20", timeout.Token);
        await using var transaction = await contender.BeginTransactionAsync(IsolationLevel.ReadCommitted, timeout.Token);
        using var waits = new LockWaitListener(database.Name);
        string action = outcome == "exception" ? "UPDATE SET v=1/(t.v-20)" : "UPDATE SET v=t.v+1";
        var pending = contender.ExecuteAsync("MERGE INTO t USING (SELECT 1 AS id) AS s ON t.id=s.id " +
            (outcome == "no-op" ? "WHEN MATCHED AND t.v=20 THEN DO NOTHING " : "") + "WHEN MATCHED THEN " + action,
            cancellationToken: cancel.Token).AsTask();
        try
        {
            await waits.WaitForWaitAsync(transaction.Context.Sequence, timeout.Token);

            // Act
            if (outcome == "cancel") { cancel.Cancel(); }
            else { await holder.CommitAsync(timeout.Token); }

            // Assert
            if (outcome == "cancel") { await Should.ThrowAsync<OperationCanceledException>(async () => await pending); }
            else if (outcome == "exception")
            {
                var failure = await Should.ThrowAsync<DatabaseException>(async () => await pending);
                failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS02001");
            }
            else { (await pending).AffectedCount.ShouldBe(outcome == "no-op" ? 0 : 1); }
            if (holder.State == TransactionState.Active) { await holder.RollbackAsync(timeout.Token); }
            if (outcome == "dispose") { await contender.DisposeAsync(); }
            else { await transaction.CommitAsync(timeout.Token); }
            (await writer.ExecuteAsync("UPDATE t SET v=v+1", cancellationToken: timeout.Token)).AffectedCount.ShouldBe(1);
            contender.LastStatementMetrics!.HashMemoryBytes.ShouldBe(0);
        }
        finally
        {
            cancel.Cancel();
            if (holder.State == TransactionState.Active) { await holder.RollbackAsync(CancellationToken.None); }
            await ((Task)pending).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - MERGE: sustained gated races recheck latest increments exactly once and prune lineage (#1715)")]
    public async Task ExecuteAsync_RepeatedRaces_ShouldCountRechecksAndAvoidLostUpdates()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var engine = SqlDatabaseEngine.Create("merge-stress", new());
        var database = await engine.CreateDatabaseAsync("db", timeout.Token);
        await using var writer = await database.CreateSessionAsync(timeout.Token);
        await using var contender = await database.CreateSessionAsync(timeout.Token);
        await ExecuteAsync(writer, "CREATE TABLE t(id INT PRIMARY KEY,v INT)", timeout.Token);
        await ExecuteAsync(writer, "INSERT INTO t VALUES(1,0)", timeout.Token);
        int iterations = 0, rechecks = 0;
        SqlPlanExecutor.MergeTargetRechecked = _ => Interlocked.Increment(ref rechecks);
        var elapsed = Stopwatch.StartNew();
        try
        {
            // Act: minimum duration, then recorded progress, bounded by the outer hang guard.
            while (elapsed.Elapsed < TimeSpan.FromSeconds(1) || rechecks < 32)
            {
                using var release = new ManualResetEventSlim();
                var selected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                SqlPlanExecutor.MergeActionsSelected = _ => { selected.TrySetResult(); release.Wait(timeout.Token); };
                await using var transaction = await contender.BeginTransactionAsync(IsolationLevel.ReadCommitted, timeout.Token);
                var pending = Task.Run(async () => await contender.ExecuteAsync("MERGE INTO t USING (SELECT 1 AS id) AS s ON t.id=s.id " +
                    "WHEN MATCHED THEN UPDATE SET v=t.v+1", cancellationToken: timeout.Token));
                try
                {
                    await selected.Task.WaitAsync(timeout.Token);
                    await ExecuteAsync(writer, "UPDATE t SET v=v+10", timeout.Token);
                    release.Set();
                    (await pending).AffectedCount.ShouldBe(1);
                    await transaction.CommitAsync(timeout.Token);
                    iterations++;
                }
                finally
                {
                    release.Set();
                    await ((Task)pending).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
                }
            }

            // Assert
            rechecks.ShouldBe(iterations);
            rechecks.ShouldBeGreaterThanOrEqualTo(32);
            (await RowsAsync(await writer.ExecuteAsync("SELECT v FROM t", cancellationToken: timeout.Token), timeout.Token))
                .ShouldHaveSingleItem().ShouldBe([11 * iterations]);
            while (database.DataStorage.RowVersionLinks.Count != 0)
            { database.DataStorage.RowVersionLinks.Prune(database.Coordinator.PruneBound, timeout.Token); }
            database.DataStorage.RowVersionLinks.Count.ShouldBe(0);
            database.Coordinator.PairedTransactionCount.ShouldBe(0);
        }
        finally
        {
            SqlPlanExecutor.MergeActionsSelected = null;
            SqlPlanExecutor.MergeTargetRechecked = null;
        }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - MERGE: EPQ retains independent subqueries and reevaluates scalar assignments (#1715)")]
    public async Task ExecuteAsync_RecheckedAction_ShouldRetainStatementSubqueryCache()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var release = new ManualResetEventSlim();
        var selected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int independent = 0, scalar = 0;
        var builder = SqlDatabaseEngine.CreateBuilder("merge-epq-subqueries");
        builder.Functions.Add(SqlScalarFunction.Create("merge_independent", (int value) => { independent++; return 1; }, SqlFunctionVolatility.Volatile));
        builder.Functions.Add(SqlScalarFunction.Create("merge_scalar", (int value) => { scalar++; return 1; }, SqlFunctionVolatility.Volatile));
        await using var engine = builder.Build();
        var database = await engine.CreateDatabaseAsync("db", timeout.Token);
        await using var writer = await database.CreateSessionAsync(timeout.Token);
        await using var contender = await database.CreateSessionAsync(timeout.Token);
        await ExecuteAsync(writer, "CREATE TABLE t(id INT PRIMARY KEY,v INT)", timeout.Token);
        await ExecuteAsync(writer, "INSERT INTO t VALUES(1,10)", timeout.Token);
        await using var transaction = await contender.BeginTransactionAsync(IsolationLevel.ReadCommitted, timeout.Token);
        SqlPlanExecutor.MergeActionsSelected = _ => { selected.TrySetResult(); release.Wait(timeout.Token); };
        var pending = Task.Run(async () => await contender.ExecuteAsync("MERGE INTO t USING (SELECT 1 AS id) AS s ON t.id=s.id " +
            "WHEN MATCHED THEN UPDATE SET v=t.v+(SELECT merge_independent(1))+merge_scalar(1) RETURNING t.v", cancellationToken: timeout.Token));
        try
        {
            await selected.Task.WaitAsync(timeout.Token);

            // Act
            await ExecuteAsync(writer, "UPDATE t SET v=20", timeout.Token);
            release.Set();
            var result = await pending;

            // Assert
            (await RowsAsync(result, timeout.Token)).ShouldHaveSingleItem().ShouldBe([22]);
            independent.ShouldBe(1);
            scalar.ShouldBe(2);
            await transaction.CommitAsync(timeout.Token);
        }
        finally
        {
            release.Set();
            SqlPlanExecutor.MergeActionsSelected = null;
            timeout.Cancel();
            await ((Task)pending).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - MERGE: repeated-target detection uses reselected modifying actions (#1715)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_RecheckedDuplicateSources_ShouldCountOnlyModifyingActions(bool duplicate)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var release = new ManualResetEventSlim();
        var selected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var engine = SqlDatabaseEngine.Create("merge-epq-duplicates", new());
        var database = await engine.CreateDatabaseAsync("db", timeout.Token);
        await using var writer = await database.CreateSessionAsync(timeout.Token);
        await using var contender = await database.CreateSessionAsync(timeout.Token);
        await ExecuteAsync(writer, "CREATE TABLE t(id INT PRIMARY KEY,v INT)", timeout.Token);
        await ExecuteAsync(writer, "INSERT INTO t VALUES(1,10)", timeout.Token);
        await using var transaction = await contender.BeginTransactionAsync(IsolationLevel.ReadCommitted, timeout.Token);
        SqlPlanExecutor.MergeActionsSelected = _ => { selected.TrySetResult(); release.Wait(timeout.Token); };
        var pending = Task.Run(async () => await contender.ExecuteAsync("MERGE INTO t USING (SELECT 1 AS id,1 AS v UNION ALL SELECT 1,2) AS s ON t.id=s.id " +
            (duplicate ? "" : "WHEN MATCHED AND t.v=20 AND s.v=1 THEN DO NOTHING ") +
            "WHEN MATCHED THEN UPDATE SET v=t.v+s.v RETURNING t.v", cancellationToken: timeout.Token));
        try
        {
            await selected.Task.WaitAsync(timeout.Token);

            // Act
            await ExecuteAsync(writer, "UPDATE t SET v=20", timeout.Token);
            release.Set();

            // Assert
            if (duplicate)
            {
                var failure = await Should.ThrowAsync<DatabaseException>(async () => await pending);
                failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS05036");
                await transaction.RollbackAsync(timeout.Token);
            }
            else
            {
                (await RowsAsync(await pending, timeout.Token)).ShouldHaveSingleItem().ShouldBe([22]);
                await transaction.CommitAsync(timeout.Token);
            }
        }
        finally
        {
            release.Set();
            SqlPlanExecutor.MergeActionsSelected = null;
            timeout.Cancel();
            await ((Task)pending).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - MERGE: a recorded lock cycle retains deadlock identity and victim cleanup (#1715)")]
    public async Task ExecuteAsync_LockCycle_ShouldPreserveDeadlockDiagnostic()
    {
        // Arrange: prior statements retain opposite rows across MERGE's release/retry.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = SqlDatabaseEngine.Create("merge-deadlock", new());
        var database = await engine.CreateDatabaseAsync("db", timeout.Token);
        await using var first = await database.CreateSessionAsync(timeout.Token);
        await using var second = await database.CreateSessionAsync(timeout.Token);
        await ExecuteAsync(first, "CREATE TABLE t(id INT PRIMARY KEY,v INT)", timeout.Token);
        await ExecuteAsync(first, "INSERT INTO t VALUES(1,10),(2,20)", timeout.Token);
        await using var one = await first.BeginTransactionAsync(IsolationLevel.ReadCommitted, timeout.Token);
        await using var two = await second.BeginTransactionAsync(IsolationLevel.ReadCommitted, timeout.Token);
        await ExecuteAsync(first, "UPDATE t SET v=v+1 WHERE id=1", timeout.Token);
        await ExecuteAsync(second, "UPDATE t SET v=v+1 WHERE id=2", timeout.Token);
        using var waits = new LockWaitListener(database.Name);
        var pending = first.ExecuteAsync("MERGE INTO t USING (SELECT 2 AS id) AS s ON t.id=s.id " +
            "WHEN MATCHED THEN UPDATE SET v=t.v+1", cancellationToken: timeout.Token).AsTask();
        try
        {
            await waits.WaitForWaitAsync(one.Context.Sequence, timeout.Token);

            // Act
            var failure = await Should.ThrowAsync<DatabaseTransactionDeadlockException>(async () => await second.ExecuteAsync(
                "MERGE INTO t USING (SELECT 1 AS id) AS s ON t.id=s.id WHEN MATCHED THEN UPDATE SET v=t.v+1", cancellationToken: timeout.Token));

            // Assert
            failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS04065");
            await two.RollbackAsync(timeout.Token);
            (await pending).AffectedCount.ShouldBe(1);
            await one.CommitAsync(timeout.Token);
            (await RowsAsync(await first.ExecuteAsync("SELECT id,v FROM t ORDER BY id", cancellationToken: timeout.Token), timeout.Token))
                .Select(row => string.Join(',', row)).ShouldBe(["1,11", "2,21"]);
        }
        finally
        {
            if (two.State == TransactionState.Active) { await two.RollbackAsync(CancellationToken.None); }
            timeout.Cancel();
            await ((Task)pending).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        }
    }

    private static async Task ExecuteAsync(SqlDatabaseSession session, string sql, CancellationToken cancellationToken)
        => (await session.ExecuteAsync(sql, cancellationToken: cancellationToken)).Status.ShouldBe(DatabaseResultStatus.Success);

    private static async Task<List<object?[]>> RowsAsync(DatabaseResult result, CancellationToken cancellationToken)
    {
        var rows = new List<object?[]>();
        await using var set = result.ShouldBeAssignableTo<DatabaseResultSet>();
        await foreach (var row in set.GetRowsAsync(cancellationToken))
        { rows.Add(Enumerable.Range(0, row.FieldCount).Select(row.GetValue).ToArray()); }
        return rows;
    }
}
