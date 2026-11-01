using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Storage;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Sql.Tests;

[Collection(SqlLockWaitCollection.Name)]
public sealed class SqlOnUpdateConcurrencyTests
{
    private readonly ITestOutputHelper _output;

    public SqlOnUpdateConcurrencyTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - ON UPDATE: parent updates race child inserts and deletes until all counters advance, without orphans (#1723)")]
    [InlineData("CASCADE")][InlineData("SET NULL")][InlineData("SET DEFAULT")]
    public async Task ExecuteAsync_ConcurrentReferences_ShouldNeverOrphan(string action)
    {
        // Arrange: time supplies only a minimum exercise window and a hang guard;
        // committed update/insert/delete and actual wait counters determine completion.
        using var ceiling = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        await using var engine = SqlDatabaseEngine.Create("update-stress", new());
        var db = await engine.CreateDatabaseAsync("race-" + Guid.NewGuid().ToString("N"));
        await using var setup = await db.CreateSessionAsync();
        await setup.ExecuteAsync("CREATE TABLE p(tag INT PRIMARY KEY,k INT UNIQUE)");
        await setup.ExecuteAsync($"CREATE TABLE c(id INT PRIMARY KEY,k INT DEFAULT 0 REFERENCES p(k) ON UPDATE {action})");
        await setup.ExecuteAsync("INSERT INTO p VALUES(0,0),(1,1)");
        long updates = 0, inserts = 0, deletes = 0, waitsSeen = 0, conflicts = 0;
        using var waits = new LockWaitListener(db.Name);
        waits.Observed = written => { if (written.EventName == "LockWaitStart") { Interlocked.Increment(ref waitsSeen); } };
        var clock = Stopwatch.StartNew();
        bool Complete() => clock.Elapsed >= TimeSpan.FromSeconds(1) && Interlocked.Read(ref updates) >= 8 &&
            Interlocked.Read(ref inserts) >= 8 && Interlocked.Read(ref deletes) >= 8 && Interlocked.Read(ref waitsSeen) > 0;

        // Act
        async Task Run(int actor)
        {
            await using var session = await db.CreateSessionAsync(ceiling.Token);
            int id = 0;
            while (!Complete())
            {
                ceiling.Token.ThrowIfCancellationRequested();
                try
                {
                    string sql = actor switch
                    {
                        0 => "UPDATE p SET k=3-k WHERE tag=1",
                        1 => $"INSERT INTO c SELECT {++id},k FROM p WHERE tag=1",
                        _ => "DELETE FROM c",
                    };
                    var result = await session.ExecuteAsync(sql, cancellationToken: ceiling.Token);
                    if (result.AffectedCount > 0)
                    {
                        if (actor == 0) { Interlocked.Increment(ref updates); }
                        else if (actor == 1) { Interlocked.Increment(ref inserts); }
                        else { Interlocked.Increment(ref deletes); }
                    }
                }
                catch (DatabaseException failure) when (failure is SqlConstraintViolationException or DatabaseTransactionAbortedException)
                { Interlocked.Increment(ref conflicts); }
                await Task.Yield();
            }
        }
        await Task.WhenAll(Task.Run(() => Run(0)), Task.Run(() => Run(1)), Task.Run(() => Run(2))).WaitAsync(ceiling.Token);

        // Assert
        _output.WriteLine($"{action}: updates={updates}, inserts={inserts}, deletes={deletes}, waits={waitsSeen}, conflicts={conflicts}");
        updates.ShouldBeGreaterThanOrEqualTo(8);
        inserts.ShouldBeGreaterThanOrEqualTo(8);
        deletes.ShouldBeGreaterThanOrEqualTo(8);
        waitsSeen.ShouldBeGreaterThan(0);

        (await SqlOnUpdateClosureTests.Rows(setup, "SELECT COUNT(*) FROM c LEFT JOIN p ON c.k=p.k WHERE c.k IS NOT NULL AND p.k IS NULL")).ShouldBe([[0L]]);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - ON UPDATE: a refused child releases preparation grants on retry, cancellation, failure and session disposal (#1723)")]
    [InlineData(0,0)][InlineData(0,1)][InlineData(0,2)][InlineData(0,3)]
    [InlineData(1,0)][InlineData(1,1)][InlineData(1,2)][InlineData(1,3)]
    [InlineData(2,0)][InlineData(2,1)][InlineData(2,2)][InlineData(2,3)]
    [InlineData(3,0)][InlineData(3,1)][InlineData(3,2)][InlineData(3,3)]
    public async Task ExecuteAsync_RefusedChild_ShouldReleaseOwnedGrants(int path, int ending)
    {
        // Arrange: direct kernel ownership parks the child without locking its parent.
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(guard.Token);
        await using var engine = SqlDatabaseEngine.Create("update-release", new());
        var db = await engine.CreateDatabaseAsync("release-" + Guid.NewGuid().ToString("N"));
        await using var setup = await db.CreateSessionAsync();
        await setup.ExecuteAsync("CREATE TABLE p(tag INT PRIMARY KEY,k INT UNIQUE)");
        await setup.ExecuteAsync("CREATE TABLE c(k INT REFERENCES p(k) ON UPDATE CASCADE" + (ending == 3 ? " CHECK(k<2))" : ")"));
        await setup.ExecuteAsync("INSERT INTO p VALUES(1,1)");
        await setup.ExecuteAsync("INSERT INTO c VALUES(1)");
        db.Catalog.TryGetTable("dbo", "c", out var child).ShouldBeTrue();
        db.Catalog.TryGetTable("dbo", "p", out var parent).ShouldBeTrue();
        var locks = db.Coordinator.LockManager;
        var holder = await db.Coordinator.BeginAsync(IsolationLevel.ReadCommitted, guard.Token);
        var probe = await db.Coordinator.BeginAsync(IsolationLevel.ReadCommitted, guard.Token);
        await locks.AcquireAsync(holder.Sequence, FirstRow(db,child.ObjectId), LockMode.Exclusive, guard.Token);
        await using var session = await db.CreateSessionAsync();
        await using var transaction = await session.BeginTransactionAsync(IsolationLevel.ReadCommitted, guard.Token);
        var earlier = LockResource.Object(0xFFFF_FF23);
        await locks.AcquireAsync(transaction.Context.Sequence,earlier,LockMode.Exclusive,guard.Token);
        using var waits = new LockWaitListener(db.Name);
        Task<DatabaseResult>? pending = null;
        try
        {
            // Act: lock-wait events order the test; no timing assumption admits a result.
            pending = session.ExecuteAsync(SqlOnUpdateClosureTests.UpdateKey(path,"2"), cancellationToken: caller.Token).AsTask();
            if (pending.IsCompleted) { await pending; }
            await waits.WaitForWaitAsync(transaction.Context.Sequence, guard.Token);
            locks.TryAcquire(probe.Sequence, FirstRow(db,parent.ObjectId), LockMode.Shared).ShouldBeTrue();
            await db.Coordinator.RollbackAsync(probe,CancellationToken.None);
            probe = await db.Coordinator.BeginAsync(IsolationLevel.ReadCommitted,guard.Token);
            if (ending == 1) { caller.Cancel(); }
            else if (ending == 2)
            {
                var disposal = session.DisposeAsync().AsTask();
                await db.Coordinator.RollbackAsync(holder, CancellationToken.None);
                await disposal.WaitAsync(guard.Token);
            }
            else { await db.Coordinator.RollbackAsync(holder, CancellationToken.None); }
            if (ending == 1) { await Should.ThrowAsync<OperationCanceledException>(async () => await pending); }
            else if (ending == 2)
            {
                var error = await Record.ExceptionAsync(async () => await pending);
                (error is DatabaseException or OperationCanceledException).ShouldBeTrue(error?.ToString());
            }
            else if (ending == 3) { await Should.ThrowAsync<DatabaseException>(async () => await pending); }
            else { (await pending).AffectedCount.ShouldBe(1); }

            locks.TryAcquire(probe.Sequence,earlier,LockMode.Shared).ShouldBe(ending == 2);
            // Assert: failure releases all new row/table grants while the transaction
            // remains open. Successful apply retains them until its explicit rollback.
            if (ending != 0)
            {
                locks.TryAcquire(probe.Sequence,FirstRow(db,parent.ObjectId),LockMode.Exclusive).ShouldBeTrue();
                // Upsert's metadata intents precede arbitration's mark and retain their
                // existing transaction duration. All new action/row grants must release.
                if (path != 2) { locks.TryAcquire(probe.Sequence,LockResource.Object(parent.ObjectId),LockMode.Exclusive).ShouldBeTrue(); }
            }
        }
        finally
        {
            caller.Cancel();
            if (holder.State == TransactionState.Active) { await db.Coordinator.RollbackAsync(holder, CancellationToken.None); }
            if (pending is not null) { try { await pending; } catch (Exception) { } }
            await transaction.RollbackAsync(CancellationToken.None);
            if (probe.State == TransactionState.Active) { await db.Coordinator.RollbackAsync(probe, CancellationToken.None); }
        }
        (await SqlOnUpdateClosureTests.Rows(setup,"SELECT k FROM p")).ShouldBe([[1]]);
        (await SqlOnUpdateClosureTests.Rows(setup,"SELECT k FROM c")).ShouldBe([[1]]);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - ON UPDATE: retained grants still participate in deadlock detection (#1723)")]
    public async Task ExecuteAsync_RetainedCascadeCycle_ShouldDetectDeadlock()
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        await using var engine = SqlDatabaseEngine.Create("update-deadlock", new());
        var db = await engine.CreateDatabaseAsync("deadlock-" + Guid.NewGuid().ToString("N"));
        await using var first = await db.CreateSessionAsync();
        await using var second = await db.CreateSessionAsync();
        await first.ExecuteAsync("CREATE TABLE p(tag INT PRIMARY KEY,k INT UNIQUE)");
        await first.ExecuteAsync("CREATE TABLE c(k INT REFERENCES p(k) ON UPDATE CASCADE)");
        await first.ExecuteAsync("INSERT INTO p VALUES(1,1),(2,2)");
        await first.ExecuteAsync("INSERT INTO c VALUES(1),(2)");
        await using var one = await first.BeginTransactionAsync(IsolationLevel.ReadCommitted, guard.Token);
        await using var two = await second.BeginTransactionAsync(IsolationLevel.ReadCommitted, guard.Token);
        await first.ExecuteAsync("UPDATE p SET k=11 WHERE tag=1");
        await second.ExecuteAsync("UPDATE p SET k=22 WHERE tag=2");
        using var waits = new LockWaitListener(db.Name);
        var pending = first.ExecuteAsync("UPDATE p SET k=23 WHERE tag=2", cancellationToken: guard.Token).AsTask();
        await waits.WaitForWaitAsync(one.Context.Sequence, guard.Token);
        try
        {
            // Act
            var error = await Should.ThrowAsync<DatabaseException>(async () =>
                await second.ExecuteAsync("UPDATE p SET k=12 WHERE tag=1", cancellationToken: guard.Token));
            // Assert
            error.Message.ShouldContain("deadlock");
        }
        finally
        {
            await two.RollbackAsync(CancellationToken.None);
            try { await pending; } catch (DatabaseException) { }
            await one.RollbackAsync(CancellationToken.None);
        }
        (await SqlOnUpdateClosureTests.Rows(first,"SELECT k FROM c ORDER BY k")).ShouldBe([[1],[2]]);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - ON UPDATE: an action published after planning cannot acquire a late sequence grant (#1723)")]
    public async Task ExecuteAsync_LateSequenceAction_ShouldRequireRetry()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("update-late-grant",new());
        var db = await engine.CreateDatabaseAsync("db");
        await using var session = await db.CreateSessionAsync();
        await using var changer = await db.CreateSessionAsync();
        await session.ExecuteAsync("CREATE SEQUENCE s START WITH 2");
        await session.ExecuteAsync("CREATE TABLE p(tag INT PRIMARY KEY,k BIGINT UNIQUE)");
        await session.ExecuteAsync("CREATE TABLE c(k BIGINT DEFAULT NEXT VALUE FOR s)");
        await session.ExecuteAsync("INSERT INTO p VALUES(1,1),(2,2)");
        await session.ExecuteAsync("INSERT INTO c VALUES(1)");
        int published = 0;
        SqlPlanExecutor.ReferenceIntentsDiscovered = table =>
        {
            if (table.Name == "p" && Interlocked.Exchange(ref published,1) == 0)
            { changer.ExecuteAsync("ALTER TABLE c ADD CONSTRAINT late_fk FOREIGN KEY(k) REFERENCES p(k) ON UPDATE SET DEFAULT").GetAwaiter().GetResult(); }
        };
        try
        {
            // Act
            var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("UPDATE p SET k=4 WHERE tag=1"));
            // Assert
            published.ShouldBe(1);
            error.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "CS04067");
            (await SqlOnUpdateClosureTests.Rows(session,"SELECT k FROM c")).ShouldBe([[1L]]);
            (await SqlOnUpdateClosureTests.Rows(session,"SELECT k FROM p ORDER BY tag")).ShouldBe([[1L],[2L]]);
        }
        finally { SqlPlanExecutor.ReferenceIntentsDiscovered = null; }
        await session.ExecuteAsync("UPDATE p SET k=4 WHERE tag=1");
        (await SqlOnUpdateClosureTests.Rows(session,"SELECT k FROM c")).ShouldBe([[2L]]);
    }

    private static LockResource FirstRow(SqlDatabase database, ulong objectId)
    {
        using var iterator = database.DataStorage.GetUnitIterator(objectId);
        iterator.MoveNext().ShouldBeTrue();
        var unit = iterator.Current;
        return LockResource.Entry(objectId, SqlRecordLocation.Pack(unit.PageId,unit.SlotIndex));
    }
}
