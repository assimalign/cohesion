using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
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
public sealed class SqlOnUpdateFixTests
{
    private readonly ITestOutputHelper _output;

    public SqlOnUpdateFixTests(ITestOutputHelper output) { _output = output; }

    [Theory(DisplayName = "Cohesion Test [Sql] - Referential closures: plain UPDATE, upsert and DELETE obey memory budget (#1723)")]
    [InlineData(0)][InlineData(2)][InlineData(4)]
    public async Task ExecuteAsync_PlainClosureExceedsBudget_ShouldReleaseBeforeApply(int path)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("closure-budget", new() { StatementMemoryBudget = 8192 });
        var db = await engine.CreateDatabaseAsync("db");
        await using var session = await db.CreateSessionAsync();
        await SeedAsync(session, 1000);
        await session.ExecuteAsync("BEGIN");

        // Act
        string sql = path == 4 ? "DELETE FROM p WHERE tag=1" : SqlOnUpdateClosureTests.UpdateKey(path, "2");
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(sql));
        var metrics = session.LastStatementMetrics.ShouldNotBeNull();

        // Assert
        error.Diagnostics.ShouldContain(d => d.Code == "CS05067");
        metrics.PeakHashMemoryBytes.ShouldBeInRange(1, 8192);
        metrics.HashMemoryBytes.ShouldBe(0);
        await using var other = await db.CreateSessionAsync();
        await other.ExecuteAsync("UPDATE p SET tag=tag WHERE tag=1", cancellationToken: TestTimeout.Token());
        await session.ExecuteAsync("ROLLBACK");
        (await SqlOnUpdateClosureTests.Rows(session, "SELECT k FROM p")).ShouldBe([[1]]);
        (await SqlOnUpdateClosureTests.Rows(session, "SELECT COUNT(*) FROM c WHERE k=1")).ShouldBe([[1000L]]);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Referential retries: abandoned work releases its memory on every update path (#1723)")]
    [InlineData(0)][InlineData(1)][InlineData(2)][InlineData(3)]
    public async Task ExecuteAsync_ParkedChildRetry_ShouldKeepSingleAttemptBudget(int path)
    {
        // Arrange: measure the same closure, then allow only one attempt's peak plus owner overhead.
        long peak = await RunAsync(long.MaxValue, parked: false);
        peak.ShouldBeGreaterThan(0);

        // Act
        long retried = await RunAsync(path == 1 ? 192_120 : peak + 1024, parked: true);

        // Assert
        retried.ShouldBe(peak);
        _output.WriteLine($"path={path}; one-attempt={peak}; retried={retried}");

        async Task<long> RunAsync(long budget, bool parked)
        {
            using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(35));
            await using var engine = SqlDatabaseEngine.Create("closure-retry", new() { StatementMemoryBudget = budget });
            var db = await engine.CreateDatabaseAsync("db");
            await using var session = await db.CreateSessionAsync();
            await SeedAsync(session, 100);
            db.Catalog.TryGetTable("dbo", "c", out var child).ShouldBeTrue();
            var holder = await db.Coordinator.BeginAsync(IsolationLevel.ReadCommitted, guard.Token);
            await using var transaction = await session.BeginTransactionAsync(IsolationLevel.ReadCommitted, guard.Token);
            using var waits = new LockWaitListener(db.Name);
            if (parked)
            {
                using var iterator = db.DataStorage.GetUnitIterator(child.ObjectId);
                iterator.MoveNext().ShouldBeTrue();
                var unit = iterator.Current;
                await db.Coordinator.LockManager.AcquireAsync(holder.Sequence,
                    LockResource.Entry(child.ObjectId, SqlRecordLocation.Pack(unit.PageId, unit.SlotIndex)), LockMode.Exclusive, guard.Token);
            }
            var pending = session.ExecuteAsync(SqlOnUpdateClosureTests.UpdateKey(path, "2"), cancellationToken: guard.Token).AsTask();
            try
            {
                if (parked) { await waits.WaitForWaitAsync(transaction.Context.Sequence, guard.Token); }
                await db.Coordinator.RollbackAsync(holder, CancellationToken.None);
                (await pending).AffectedCount.ShouldBe(1);
                var metrics = session.LastStatementMetrics.ShouldNotBeNull();
                metrics.HashMemoryBytes.ShouldBe(0);
                return metrics.PeakHashMemoryBytes;
            }
            finally
            {
                if (holder.State == TransactionState.Active) { await db.Coordinator.RollbackAsync(holder, CancellationToken.None); }
                try { await pending; } catch (DatabaseException) { }
                await transaction.RollbackAsync(CancellationToken.None);
            }
        }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Plain UPDATE: direct proposals are charged before the next proposal is evaluated (#1723)")]
    public async Task ExecuteAsync_DirectProposals_ShouldRetainTheirOwnGrant()
    {
        // Arrange: a retained first proposal exceeds the grant before the second
        // volatile proposal can run, independently of the later closure ledger.
        int calls = 0;
        var builder = SqlDatabaseEngine.CreateBuilder("direct-proposal-grant");
        builder.Options.StatementMemoryBudget = 8192;
        builder.Functions.Add(SqlScalarFunction.Create("proposal_payload", (int value) =>
        {
            calls++;
            return new string('x', 4000 + value);
        }, SqlFunctionVolatility.Volatile));
        await using var engine = builder.Build();
        var db = await engine.CreateDatabaseAsync("db");
        await using var session = await db.CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE p(k INT PRIMARY KEY,v TEXT)");
        await session.ExecuteAsync("CREATE TABLE c(k INT REFERENCES p(k) ON UPDATE CASCADE)");
        await session.ExecuteAsync("INSERT INTO p VALUES(1,'old'),(2,'old')");

        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () =>
            await session.ExecuteAsync("UPDATE p SET k=k+10,v=proposal_payload(k)"));

        // Assert
        calls.ShouldBe(1);
        error.Diagnostics.ShouldContain(d => d.Code == "CS05067");
        session.LastStatementMetrics.ShouldNotBeNull().HashMemoryBytes.ShouldBe(0);
        (await SqlOnUpdateClosureTests.Rows(session, "SELECT k,v FROM p ORDER BY k")).ShouldBe([[1,"old"],[2,"old"]]);
    }

    public static IEnumerable<object[]> CreationOrders()
    {
        foreach (var order in Permute(["a", "b1", "b", "p", "c"])) { yield return [string.Join(',', order)]; }
        static IEnumerable<string[]> Permute(string[] names)
        {
            if (names.Length == 0) { yield return []; }
            foreach (string name in names)
            { foreach (var tail in Permute(names.Where(n => n != name).ToArray())) { yield return [name, .. tail]; } }
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Composite cascades: same-origin revisions converge in every creation order (#1723)")]
    [MemberData(nameof(CreationOrders))]
    public async Task ExecuteAsync_TwoHopCompositeKey_ShouldReplaceEarlierOrigin(string order)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("two-hop", new());
        var db = await engine.CreateDatabaseAsync("db");
        await using var session = await db.CreateSessionAsync();
        var declarations = new Dictionary<string, string>
        {
            ["a"] = "id INT PRIMARY KEY", ["b1"] = "k INT PRIMARY KEY", ["b"] = "k INT PRIMARY KEY",
            ["p"] = "x INT,y INT,PRIMARY KEY(x,y)", ["c"] = "cx INT,cy INT",
        };
        foreach (string name in order.Split(',')) { await session.ExecuteAsync($"CREATE TABLE {name}({declarations[name]})"); }
        foreach (string sql in new[] {
            "ALTER TABLE b1 ADD FOREIGN KEY(k) REFERENCES a(id) ON UPDATE CASCADE",
            "ALTER TABLE b ADD FOREIGN KEY(k) REFERENCES b1(k) ON UPDATE CASCADE",
            "ALTER TABLE p ADD FOREIGN KEY(x) REFERENCES a(id) ON UPDATE CASCADE",
            "ALTER TABLE p ADD FOREIGN KEY(y) REFERENCES b(k) ON UPDATE CASCADE",
            "ALTER TABLE c ADD FOREIGN KEY(cx,cy) REFERENCES p(x,y) ON UPDATE CASCADE",
            "INSERT INTO a VALUES(1)", "INSERT INTO b1 VALUES(1)", "INSERT INTO b VALUES(1)",
            "INSERT INTO p VALUES(1,1)", "INSERT INTO c VALUES(1,1)" }) { await session.ExecuteAsync(sql); }

        // Act
        await session.ExecuteAsync("UPDATE a SET id=2");

        // Assert
        (await SqlOnUpdateClosureTests.Rows(session, "SELECT cx,cy FROM c")).ShouldBe([[2, 2]]);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Self references: action matching follows direct assignments and RETURNING preserves direct writes (#1723)")]
    [InlineData(0, "pid", 11)][InlineData(1, "pid", 11)][InlineData(2, "pid", 11)][InlineData(3, "pid", 11)]
    [InlineData(0, "5", 5)][InlineData(1, "5", 5)][InlineData(2, "5", 5)][InlineData(3, "5", 5)]
    [InlineData(0, "", 11)][InlineData(1, "", 11)][InlineData(2, "", 11)][InlineData(3, "", 11)]
    public async Task ExecuteAsync_DirectSelfAssignment_ShouldMatchFinalProposalAndReturnOwnWrite(int path, string assignment, int expected)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("direct-self", new());
        var db = await engine.CreateDatabaseAsync("db");
        await using var session = await db.CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE t(id INT PRIMARY KEY,pid INT REFERENCES t(id) ON UPDATE CASCADE)");
        await session.ExecuteAsync("INSERT INTO t VALUES(1,NULL),(2,1),(5,NULL)");
        string assignments = "id=t.id+10" + (assignment.Length == 0 ? "" : ",pid=" + (assignment == "pid" ? "t.pid" : assignment));
        string sql = path switch
        {
            0 => $"UPDATE t SET {assignments} WHERE id<3 RETURNING *",
            1 => $"UPDATE t SET {assignments} FROM (SELECT 1 AS v) s WHERE t.id<3 RETURNING t.id,t.pid",
            2 => $"INSERT INTO t VALUES(1,NULL),(2,1) ON CONFLICT(id) DO UPDATE SET {assignments} RETURNING *",
            _ => $"MERGE INTO t USING (SELECT 1 AS v) s ON t.id<3 WHEN MATCHED THEN UPDATE SET {assignments} RETURNING t.id,t.pid",
        };

        // Act
        await using var returned = (await session.ExecuteAsync(sql)).ShouldBeAssignableTo<DatabaseResultSet>();
        var rows = await SqlT1ScalarTests.ReadAsync(returned);

        // Assert
        rows.Single(row => Equals(row[0], 12))[1].ShouldBe(assignment == "5" ? 5 : 1);
        (await SqlOnUpdateClosureTests.Rows(session, "SELECT pid FROM t WHERE id=12")).ShouldBe([[expected]]);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - DELETE SET closure: indexed work stays linear at 1,000 and 10,000 rows (#1723)")]
    [InlineData(false, 1000)][InlineData(false, 10000)][InlineData(true, 1000)][InlineData(true, 10000)]
    public async Task ExecuteAsync_DeleteSetNonreferencedKey_ShouldVisitLinearRows(bool joined, int count)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("delete-linear", new());
        var db = await engine.CreateDatabaseAsync("db");
        await using var session = await db.CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE p(id INT PRIMARY KEY)");
        await session.ExecuteAsync("CREATE TABLE c(id INT PRIMARY KEY,pid INT REFERENCES p(id) ON DELETE SET NULL)");
        await session.ExecuteAsync("CREATE TABLE d(id INT PRIMARY KEY,pid INT REFERENCES c(id) ON UPDATE CASCADE)");
        await session.ExecuteAsync("CREATE INDEX ix_pid ON c(pid)");
        for (int start = 1; start <= count; start += 1000)
        {
            await session.ExecuteAsync("INSERT INTO p VALUES" + string.Join(',', Enumerable.Range(start, 1000).Select(i => $"({i})")));
            await session.ExecuteAsync("INSERT INTO c VALUES" + string.Join(',', Enumerable.Range(start, 1000).Select(i => $"({i},{i})")));
        }

        // Act
        (await session.ExecuteAsync(joined ? "DELETE FROM p USING (SELECT 1 AS v) s WHERE s.v=1" : "DELETE FROM p")).AffectedCount.ShouldBe(count);
        var metrics = session.LastStatementMetrics.ShouldNotBeNull();

        // Assert
        _output.WriteLine($"joined={joined}; rows={count}; comparisons={metrics.ConstraintKeyComparisons}; records={metrics.RecordsExamined}");
        metrics.ConstraintKeyComparisons.ShouldBeLessThan(100L * count);
        metrics.RecordsExamined.ShouldBeLessThan(100L * count);
        (await SqlOnUpdateClosureTests.Rows(session, "SELECT COUNT(*) FROM c WHERE pid IS NULL")).ShouldBe([[ (long)count ]]);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - DELETE SET DEFAULT: an uncommitted parent cannot satisfy the final foreign key (#1723)")]
    public async Task ExecuteAsync_DeleteDefaultUncommittedParent_ShouldRefuseOrphan()
    {
        // Arrange
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        await using var engine = SqlDatabaseEngine.Create("delete-visibility", new());
        var db = await engine.CreateDatabaseAsync("db");
        await using var deleting = await db.CreateSessionAsync();
        await using var writer = await db.CreateSessionAsync();
        await deleting.ExecuteAsync("CREATE TABLE p(id INT PRIMARY KEY)");
        await deleting.ExecuteAsync("CREATE TABLE c(pid INT DEFAULT 2 REFERENCES p(id) ON DELETE SET DEFAULT)");
        await deleting.ExecuteAsync("INSERT INTO p VALUES(1)");
        await deleting.ExecuteAsync("INSERT INTO c VALUES(1)");
        await using var insertion = await writer.BeginTransactionAsync(IsolationLevel.ReadCommitted, guard.Token);
        await writer.ExecuteAsync("INSERT INTO p VALUES(2)");
        await using var transaction = await deleting.BeginTransactionAsync(IsolationLevel.ReadCommitted, guard.Token);
        using var waits = new LockWaitListener(db.Name);

        // Act: old code admits the uncommitted row, queues S, then accepts its rollback.
        var pending = deleting.ExecuteAsync("DELETE FROM p WHERE id=1", cancellationToken: guard.Token).AsTask();
        if (!pending.IsCompleted) { await waits.WaitForWaitAsync(transaction.Context.Sequence, guard.Token); }
        await insertion.RollbackAsync(CancellationToken.None);
        var error = await Should.ThrowAsync<DatabaseException>(async () => await pending);

        // Assert
        error.Diagnostics.ShouldContain(d => d.Code == "CS03039");
        await transaction.CommitAsync(CancellationToken.None);
        (await SqlOnUpdateClosureTests.Rows(deleting, "SELECT pid FROM c")).ShouldBe([[1]]);
    }

    private static async Task SeedAsync(SqlDatabaseSession session, int count)
    {
        await session.ExecuteAsync("CREATE TABLE p(tag INT PRIMARY KEY,k INT UNIQUE)");
        await session.ExecuteAsync("CREATE TABLE c(id INT PRIMARY KEY,k INT REFERENCES p(k) ON UPDATE CASCADE ON DELETE CASCADE)");
        await session.ExecuteAsync("CREATE INDEX ix_k ON c(k)");
        await session.ExecuteAsync("INSERT INTO p VALUES(1,1)");
        await session.ExecuteAsync("INSERT INTO c VALUES" + string.Join(',', Enumerable.Range(1, count).Select(i => $"({i},1)")));
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Upsert: pending inserts still substitute for a released NO ACTION parent key (#1723)")]
    public async Task ExecuteAsync_NoActionPendingInsert_ShouldRetainSubstitution()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("upsert-substitute", new());
        var db = await engine.CreateDatabaseAsync("db");
        await using var session = await db.CreateSessionAsync();
        await session.ExecuteAsync("CREATE TABLE p(tag INT PRIMARY KEY,k INT UNIQUE)");
        await session.ExecuteAsync("CREATE TABLE c(k INT REFERENCES p(k) ON UPDATE NO ACTION)");
        await session.ExecuteAsync("INSERT INTO p VALUES(1,1)");
        await session.ExecuteAsync("INSERT INTO c VALUES(1)");

        // Act
        await session.ExecuteAsync("INSERT INTO p VALUES(1,2),(2,1) ON CONFLICT(tag) DO UPDATE SET k=excluded.k");

        // Assert
        (await SqlOnUpdateClosureTests.Rows(session, "SELECT tag,k FROM p ORDER BY tag")).ShouldBe([[1,2],[2,1]]);
        (await SqlOnUpdateClosureTests.Rows(session, "SELECT k FROM c")).ShouldBe([[1]]);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - SET DEFAULT contention: parent updates and child re-points all make recorded progress (#1723)")]
    public async Task ExecuteAsync_DefaultRepointContention_ShouldProgressThroughLockManager()
    {
        // Arrange: elapsed time is only a minimum exercise window and a hang guard.
        using var ceiling = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        await using var engine = SqlDatabaseEngine.Create("default-repoint", new());
        var db = await engine.CreateDatabaseAsync("race-" + Guid.NewGuid().ToString("N"));
        await using var setup = await db.CreateSessionAsync();
        await setup.ExecuteAsync("CREATE TABLE p(tag INT PRIMARY KEY,k INT UNIQUE)");
        await setup.ExecuteAsync("CREATE TABLE c(id INT PRIMARY KEY,k INT DEFAULT 0 REFERENCES p(k) ON UPDATE SET DEFAULT)");
        await setup.ExecuteAsync("INSERT INTO p VALUES(0,0),(1,1)");
        await setup.ExecuteAsync("INSERT INTO c VALUES(1,1)");
        long updates = 0, repoints = 0, movingRepoints = 0, waitsSeen = 0, conflicts = 0;
        using var waits = new LockWaitListener(db.Name);
        waits.Observed = written => { if (written.EventName == "LockWaitStart") { Interlocked.Increment(ref waitsSeen); } };
        var clock = Stopwatch.StartNew();
        bool Complete() => clock.Elapsed >= TimeSpan.FromSeconds(1) && Interlocked.Read(ref updates) >= 8 &&
            Interlocked.Read(ref repoints) >= 16 && Interlocked.Read(ref movingRepoints) >= 8 && Interlocked.Read(ref waitsSeen) > 0;

        // Act
        async Task RunAsync(int actor)
        {
            await using var session = await db.CreateSessionAsync(ceiling.Token);
            int iteration = 0;
            while (!Complete())
            {
                try
                {
                    int tag = (++iteration + actor) % 2;
                    string sql = actor == 0 ? "UPDATE p SET k=3-k WHERE tag=1"
                        : $"UPDATE c SET k=p.k FROM p WHERE c.id=1 AND p.tag={tag}";
                    await session.ExecuteAsync(sql, cancellationToken: ceiling.Token);
                    if (actor == 0) { Interlocked.Increment(ref updates); }
                    else
                    {
                        Interlocked.Increment(ref repoints);
                        if (tag == 1) { Interlocked.Increment(ref movingRepoints); }
                    }
                }
                catch (DatabaseException failure) when (failure is SqlConstraintViolationException or DatabaseTransactionAbortedException)
                { Interlocked.Increment(ref conflicts); }
                await Task.Yield();
            }
        }
        await Task.WhenAll(Task.Run(() => RunAsync(0)), Task.Run(() => RunAsync(1)), Task.Run(() => RunAsync(2))).WaitAsync(ceiling.Token);
        clock.Stop();

        // Assert
        updates.ShouldBeGreaterThanOrEqualTo(8);
        repoints.ShouldBeGreaterThanOrEqualTo(16);
        movingRepoints.ShouldBeGreaterThanOrEqualTo(8);
        waitsSeen.ShouldBeGreaterThan(0);
        _output.WriteLine($"seconds={clock.Elapsed.TotalSeconds:F4}; updates={updates}; re-points={repoints}; moving-parent re-points={movingRepoints}; waits={waitsSeen}; conflicts={conflicts}; " +
            $"updates/s={updates / clock.Elapsed.TotalSeconds:F2}; re-points/s={repoints / clock.Elapsed.TotalSeconds:F2}; waits/s={waitsSeen / clock.Elapsed.TotalSeconds:F2}");
        (await SqlOnUpdateClosureTests.Rows(setup, "SELECT COUNT(*) FROM c LEFT JOIN p ON c.k=p.k WHERE c.k IS NOT NULL AND p.k IS NULL")).ShouldBe([[0L]]);
    }
}
