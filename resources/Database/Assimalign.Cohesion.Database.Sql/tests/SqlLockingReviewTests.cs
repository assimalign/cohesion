using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Client;
using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Client;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Storage;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

[Collection(SqlLockWaitCollection.Name)]
public sealed class SqlLockingReviewTests
{
    [Fact(DisplayName = "Cohesion Test [Sql] - Cascade: cancellation at apply admission releases every new lock (w11 review)")]
    public async Task ExecuteAsync_CascadeCanceledAtApplyGate_ShouldReleaseUnappliedLocks()
    {
        // Arrange: the gate holder owns no cascade resource.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        await using var engine = SqlDatabaseEngine.Create("cascade-gate", new());
        var database = await engine.CreateDatabaseAsync("db", timeout.Token);
        await using var session = await database.CreateSessionAsync(timeout.Token);
        await SeedCascadeAsync(session, timeout.Token);
        var coordinator = database.Coordinator;
        var holder = await coordinator.BeginAsync(IsolationLevel.ReadCommitted, timeout.Token);
        var probe = await coordinator.BeginAsync(IsolationLevel.ReadCommitted, timeout.Token);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holding = coordinator.ApplyStatementAsync(holder, async _ =>
        {
            entered.SetResult();
            await release.Task.WaitAsync(timeout.Token);
            return true;
        }, durable: false, timeout.Token).AsTask();
        await entered.Task.WaitAsync(timeout.Token);
        await using var transaction = await session.BeginTransactionAsync(IsolationLevel.ReadCommitted, timeout.Token);
        var earlier = LockResource.Object(0xFFFF_FF00);
        await coordinator.LockManager.AcquireAsync(transaction.Context.Sequence, earlier, LockMode.Exclusive, timeout.Token);
        int prepared = 0;
        session.ResultPreparation = new SqlResultPreparation(() =>
        {
            prepared++;
            return new SqlQueryResult(DatabaseResultStatus.Success, 1);
        });
        try
        {
            // Act: all preparation grants complete synchronously; the only wait is admission.
            var deleting = session.ExecuteAsync("DELETE FROM p WHERE id=1", cancellationToken: caller.Token).AsTask();
            deleting.IsCompleted.ShouldBeFalse();
            caller.Cancel();
            var canceled = await Should.ThrowAsync<OperationCanceledException>(async () => await deleting);

            // Assert: the transaction and its earlier work survive, with no delete apply.
            canceled.CancellationToken.ShouldBe(caller.Token);
            prepared.ShouldBe(0);
            transaction.State.ShouldBe(TransactionState.Active);
            database.Catalog.TryGetTable("dbo", "p", out var parent).ShouldBeTrue();
            coordinator.LockManager.TryAcquire(probe.Sequence, LastRow(database, parent.ObjectId), LockMode.Shared).ShouldBeTrue();
            coordinator.LockManager.TryAcquire(probe.Sequence, earlier, LockMode.Shared).ShouldBeFalse();
        }
        finally
        {
            release.TrySetResult();
            await holding.WaitAsync(timeout.Token);
            await coordinator.RollbackAsync(probe, CancellationToken.None);
            await coordinator.RollbackAsync(holder, CancellationToken.None);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Cascade: hot child inserts cannot perpetually restart one parent DELETE (w11 review)")]
    public async Task ExecuteAsync_HotChildInserters_ShouldBoundCascadeReleaseRetries()
    {
        // Arrange: each real wait admits another child commit and parks that new row.
        // Events order this adversarial schedule; the timeout is only a hang guard.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = SqlDatabaseEngine.Create("cascade-starvation", new()
        {
            MaintenanceInterval = TimeSpan.FromHours(1),
            CheckpointInterval = TimeSpan.FromHours(1),
        });
        var database = await engine.CreateDatabaseAsync("db", timeout.Token);
        await using var setup = await database.CreateSessionAsync(timeout.Token);
        await SeedCascadeAsync(setup, timeout.Token);
        await using var inserterOne = await database.CreateSessionAsync(timeout.Token);
        await using var inserterTwo = await database.CreateSessionAsync(timeout.Token);
        await using var session = await database.CreateSessionAsync(timeout.Token);
        await using var transaction = await session.BeginTransactionAsync(IsolationLevel.ReadCommitted, timeout.Token);
        database.Catalog.TryGetTable("dbo", "p", out var parent).ShouldBeTrue();
        database.Catalog.TryGetTable("dbo", "c", out var child).ShouldBeTrue();
        var coordinator = database.Coordinator;
        var locks = coordinator.LockManager;
        var probe = new TransactionSequence(ulong.MaxValue - 1420);
        var holder = await coordinator.BeginAsync(IsolationLevel.ReadCommitted, timeout.Token);
        await locks.AcquireAsync(holder.Sequence, LastRow(database, child.ObjectId), LockMode.Exclusive, timeout.Token);
        var waits = Channel.CreateUnbounded<bool>();
        using var listener = new LockWaitListener(database.Name);
        int retries = 0;
        int childCommits = 0;
        listener.Observed = written =>
        {
            if (written.EventName == "LockWaitStart" && written.Payload is { Count: > 1 } payload &&
                Equals(payload[1], (long)transaction.Context.Sequence.Value))
            {
                Interlocked.Increment(ref retries);
                waits.Writer.TryWrite(true);
            }
        };
        Task<DatabaseResult>? deleting = null;
        try
        {
            // Act: inject until the DELETE retains its parent, with a counter ceiling on base.
            deleting = session.ExecuteAsync("DELETE FROM p WHERE id=1", cancellationToken: timeout.Token).AsTask();
            while (true)
            {
                await waits.Reader.ReadAsync(timeout.Token);
                bool released = locks.TryAcquire(probe, LastRow(database, parent.ObjectId), LockMode.Shared);
                locks.ReleaseAll(probe);
                if (!released || retries == 8)
                {
                    await coordinator.RollbackAsync(holder, CancellationToken.None);
                    holder = null;
                    break;
                }
                var inserter = childCommits % 2 == 0 ? inserterOne : inserterTwo;
                (await inserter.ExecuteAsync($"INSERT INTO c VALUES({11 + childCommits},1)", cancellationToken: timeout.Token)).AffectedCount.ShouldBe(1);
                childCommits++;
                var next = await coordinator.BeginAsync(IsolationLevel.ReadCommitted, timeout.Token);
                await locks.AcquireAsync(next.Sequence, LastRow(database, child.ObjectId), LockMode.Exclusive, timeout.Token);
                await coordinator.RollbackAsync(holder, CancellationToken.None);
                holder = next;
            }
            var result = await deleting.WaitAsync(timeout.Token);

            // Assert: two releases, then the third refusal queues while holding the parent.
            result.AffectedCount.ShouldBe(1);
            retries.ShouldBe(3);
            childCommits.ShouldBe(2);
            await transaction.CommitAsync(timeout.Token);
            await using var rows = (DatabaseResultSet)await setup.ExecuteAsync("SELECT COUNT(*) FROM c", cancellationToken: timeout.Token);
            await foreach (var row in rows.GetRowsAsync(timeout.Token)) { Convert.ToInt64(row.GetValue(0)).ShouldBe(0); }
        }
        finally
        {
            listener.Observed = null;
            if (holder is not null) { await coordinator.RollbackAsync(holder, CancellationToken.None); }
            if (deleting is not null) { await deleting.WaitAsync(timeout.Token); }
            locks.ReleaseAll(probe);
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Deadlock: embedded and wire failures carry the transaction diagnostic mapped to 40P01 (w11 review)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_HardDeadlock_ShouldCarryStructuredDiagnostic(bool wire)
    {
        // Arrange: actual updates hold different rows, then request each other's row.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var harness = await ServerTestHarness.StartAsync(engineName: "hard-deadlock");
        harness.Engine.TryGetDatabase(ServerTestHarness.DatabaseName, out var database).ShouldBeTrue();
        await using var survivor = await database!.CreateSessionAsync(timeout.Token);
        await using var first = await survivor.BeginTransactionAsync(IsolationLevel.ReadCommitted, timeout.Token);
        await survivor.ExecuteAsync("UPDATE users SET name='survivor' WHERE id=1", cancellationToken: timeout.Token);
        await using var victim = await database.CreateSessionAsync(timeout.Token);
        await using var second = wire ? null : await victim.BeginTransactionAsync(IsolationLevel.ReadCommitted, timeout.Token);
        await using var client = SqlClient.Create(new SqlClientOptions
        {
            Settings = new DatabaseConnectionSettings { Database = ServerTestHarness.DatabaseName, Principal = "deadlock", EndPoint = harness.Listener.EndPoint },
            ConnectionFactory = harness.Listener.CreateFactory(),
        });
        await using var connection = wire ? await client.ConnectAsync(timeout.Token) : null;
        if (wire) { await connection!.ExecuteAsync("BEGIN", cancellationToken: timeout.Token); }
        async Task ExecuteVictimAsync(string sql)
        {
            if (wire) { await connection!.ExecuteAsync(sql, cancellationToken: timeout.Token); }
            else { await victim.ExecuteAsync(sql, cancellationToken: timeout.Token); }
        }
        await ExecuteVictimAsync("UPDATE users SET name='victim' WHERE id=2");
        using var waits = new LockWaitListener(database.Name);
        var pending = survivor.ExecuteAsync("UPDATE users SET name='survivor' WHERE id=2", cancellationToken: timeout.Token).AsTask();
        await waits.WaitForWaitAsync(first.Context.Sequence, timeout.Token);
        try
        {
            // Act / Assert: the requester is refused and its prior statement stays active.
            Diagnostic diagnostic;
            if (wire)
            {
                var failure = await Should.ThrowAsync<SqlClientException>(() => ExecuteVictimAsync("UPDATE users SET name='victim' WHERE id=1"));
                failure.Kind.ShouldBe(SqlClientErrorKind.ExecutionFailure);
                failure.ConnectionUsable.ShouldBeTrue();
                diagnostic = failure.Diagnostics.ShouldHaveSingleItem();
            }
            else
            {
                var failure = await Should.ThrowAsync<DatabaseTransactionDeadlockException>(() => ExecuteVictimAsync("UPDATE users SET name='victim' WHERE id=1"));
                failure.InnerException.ShouldBeOfType<TransactionDeadlockException>();
                second!.State.ShouldBe(TransactionState.Active);
                diagnostic = failure.Diagnostics.ShouldHaveSingleItem();
            }
            diagnostic.Code.ShouldBe("CS04065");
            diagnostic.Severity.ShouldBe(DiagnosticSeverity.Error);
            diagnostic.Message.ShouldNotBeNull().ShouldContain("deadlock");
            pending.IsCompleted.ShouldBeFalse("victim transaction rollback is caller-managed");
        }
        finally
        {
            if (wire) { await connection!.ExecuteAsync("ROLLBACK", cancellationToken: timeout.Token); }
            else { await second!.RollbackAsync(CancellationToken.None); }
            await pending.WaitAsync(timeout.Token);
            await first.CommitAsync(timeout.Token);
        }
    }

    private static async Task SeedCascadeAsync(SqlDatabaseSession session, CancellationToken cancellationToken)
    {
        await session.ExecuteAsync("CREATE TABLE p(id INT PRIMARY KEY)", cancellationToken: cancellationToken);
        await session.ExecuteAsync("CREATE TABLE c(id INT PRIMARY KEY, pid INT REFERENCES p(id) ON DELETE CASCADE)", cancellationToken: cancellationToken);
        await session.ExecuteAsync("INSERT INTO p VALUES(1)", cancellationToken: cancellationToken);
        await session.ExecuteAsync("INSERT INTO c VALUES(10,1)", cancellationToken: cancellationToken);
    }

    private static LockResource LastRow(SqlDatabase database, ulong objectId)
    {
        using var iterator = database.DataStorage.GetUnitIterator(objectId);
        LockResource? row = null;
        while (iterator.MoveNext())
        {
            var unit = iterator.Current;
            row = LockResource.Entry(objectId, SqlRecordLocation.Pack(unit.PageId, unit.SlotIndex));
        }
        return row!.Value;
    }
}
