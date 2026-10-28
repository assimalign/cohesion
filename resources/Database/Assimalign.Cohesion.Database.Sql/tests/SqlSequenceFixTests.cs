using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Storage.Tests.TestObjects;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Sql.Tests;

[Collection("SqlIdentityConcurrency")]
public sealed class SqlSequenceFixTests
{
    private readonly ITestOutputHelper _output;
    public SqlSequenceFixTests(ITestOutputHelper output) { _output = output; }

    private static string ProjectDirectory([CallerFilePath] string source = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, ".."));

    [Fact(DisplayName = "Cohesion Test [Sql] - Sequence documentation: SQ-S2 is delivered and dynamic blocking is explicit")]
    public void Documentation_SequenceScope_ShouldStateDeliveredSurfaceAndDirectoryCost()
    {
        // Arrange / Act
        string root = ProjectDirectory();
        string overview = File.ReadAllText(Path.Combine(root, "docs", "OVERVIEW.md"));
        string design = File.ReadAllText(Path.Combine(root, "docs", "DESIGN.md"));
        string dialect = File.ReadAllText(Path.Combine(root, "..", "Assimalign.Cohesion.Database.Sql.Language", "docs", "DIALECT.md"));

        // Assert
        overview.ShouldNotContain("SQL sequence syntax and public", Case.Sensitive);
        design.ShouldNotContain("SQL sequence DDL,\ncurrval/lastval/SCOPE_IDENTITY".Replace("\n", Environment.NewLine), Case.Sensitive);
        dialect.ShouldContain("blocks ALTER and DROP of unrelated sequences", Case.Sensitive);
        dialect.ShouldContain("one lock request per captured sequence", Case.Sensitive);
        dialect.ShouldContain("queued Exclusive request", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Dynamic sequence names: the captured directory owns ordered transaction grants")]
    public async Task Open_DynamicNames_ShouldLockTheDirectoryAndQueueLaterReaders()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = SqlDatabaseEngine.Create("sequence-fix", new());
        var db = await engine.CreateDatabaseAsync("db", timeout.Token);
        await using var a = await db.CreateSessionAsync(timeout.Token);
        await using var b = await db.CreateSessionAsync(timeout.Token);
        await using var c = await db.CreateSessionAsync(timeout.Token);
        await Exec(a, "CREATE SEQUENCE s"); await Exec(a, "CREATE SEQUENCE unrelated");
        await Exec(a, "CREATE TABLE t(name TEXT)"); await Exec(a, "INSERT INTO t VALUES ('s')");
        await Exec(a, "BEGIN");
        var ids = new List<ulong>();
        SqlSequenceExecution.AfterSequenceGrant = _ => ids.Add((ulong)ids.Count);
        Task? change = null, later = null;
        try
        {
            // Act
            await Rows(a, "SELECT nextval(name) FROM t", timeout.Token);
            ids.Count.ShouldBe(2);
            SqlSequenceExecution.AfterSequenceGrant = null;
            change = Exec(b, "ALTER SEQUENCE unrelated CACHE 2", timeout.Token);
            change.IsCompleted.ShouldBeFalse();
            later = Exec(c, "SELECT nextval(name) FROM t", timeout.Token);
            later.IsCompleted.ShouldBeFalse();
            await Exec(a, "ROLLBACK", timeout.Token);
            await Task.WhenAll(change, later).WaitAsync(timeout.Token);
            // Assert
            File.ReadAllText(Path.Combine(ProjectDirectory(), "..", "Assimalign.Cohesion.Database.Sql.Language", "docs", "DIALECT.md"))
                .ShouldContain("blocks ALTER and DROP of unrelated sequences", Case.Sensitive);
        }
        finally { SqlSequenceExecution.AfterSequenceGrant = null; if (a.CurrentTransaction is not null) { await Exec(a, "ROLLBACK"); } if (change is not null) { try { await change; } catch { } } if (later is not null) { try { await later; } catch { } } }
    }

    private static async Task Exec(SqlDatabaseSession session, string sql, CancellationToken token = default)
    {
        var result = await session.ExecuteAsync(sql, cancellationToken: token);
        result.Diagnostics.ShouldBeEmpty();
    }

    private static async Task<object?[][]> Rows(SqlDatabaseSession session, string sql, CancellationToken token = default)
    {
        await using var result = (DatabaseResultSet)await session.ExecuteAsync(sql, cancellationToken: token);
        var rows = new List<object?[]>();
        await foreach (var row in result.GetRowsAsync(token)) { rows.Add(Enumerable.Range(0, row.FieldCount).Select(row.GetValue).ToArray()); }
        return rows.ToArray();
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - NEXT VALUE: forbidden nested positions fail before evaluation")]
    [InlineData("SELECT (SELECT NEXT VALUE FOR s)")]
    [InlineData("SELECT (SELECT (SELECT NEXT VALUE FOR s))")]
    [InlineData("SELECT k FROM t WHERE k = (SELECT NEXT VALUE FOR s)")]
    [InlineData("SELECT * FROM (SELECT NEXT VALUE FOR s AS v) d")]
    [InlineData("WITH q AS (SELECT NEXT VALUE FOR s AS v) SELECT * FROM q")]
    [InlineData("WITH q AS (SELECT NEXT VALUE FOR s AS v) SELECT 1")]
    [InlineData("SELECT CASE WHEN k = 1 THEN NEXT VALUE FOR s ELSE 0 END FROM t")]
    [InlineData("SELECT COALESCE(NEXT VALUE FOR s, 0) FROM t")]
    [InlineData("SELECT NULLIF(NEXT VALUE FOR s, 0) FROM t")]
    [InlineData("SELECT t.k, (SELECT NEXT VALUE FOR s WHERE NOT EXISTS (SELECT 1 FROM u WHERE u.k = t.k)) FROM t")]
    [InlineData("INSERT INTO ids SELECT (SELECT NEXT VALUE FOR s WHERE NOT EXISTS (SELECT 1 FROM u WHERE u.k = t.k)) FROM t")]
    public async Task Plan_NestedNextValue_ShouldRefusePosition(string sql)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("sequence-fix", new());
        var db = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await db.CreateSessionAsync(CancellationToken.None);
        await Exec(session, "CREATE SEQUENCE s");
        await Exec(session, "CREATE TABLE t(k INT)");
        await Exec(session, "CREATE TABLE u(k INT, v INT)");
        await Exec(session, "CREATE TABLE ids(id BIGINT PRIMARY KEY)");
        await Exec(session, "INSERT INTO t VALUES (1),(2),(3)");

        // Act / Assert
        var error = await Should.ThrowAsync<DatabaseException>(() => session.ExecuteAsync(sql, cancellationToken: CancellationToken.None).AsTask());
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS03076");
        (await Rows(session, "SELECT nextval('s')"))[0][0].ShouldBe(1L);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - NEXT VALUE: nested projection preserves the outer row and INSERT uniqueness")]
    [InlineData(false)][InlineData(true)]
    public async Task Execute_NestedProjection_ShouldKeepRowScope(bool insert)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("sequence-fix", new());
        var db = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await db.CreateSessionAsync(CancellationToken.None);
        await Exec(session, "CREATE SEQUENCE s");
        await Exec(session, "CREATE TABLE t(k INT)");
        await Exec(session, "CREATE TABLE u(k INT, v INT)");
        await Exec(session, "CREATE TABLE ids(a BIGINT PRIMARY KEY, b INT, c BIGINT)");
        await Exec(session, "INSERT INTO t VALUES (1),(2),(3)");
        await Exec(session, "INSERT INTO u VALUES (1,10),(2,20),(3,30)");

        // Act
        const string query = "SELECT NEXT VALUE FOR s a, (SELECT u.v FROM u WHERE u.k = t.k) b, NEXT VALUE FOR s c FROM t";
        if (insert) { await Exec(session, "INSERT INTO ids " + query); }
        var rows = await Rows(session, insert ? "SELECT * FROM ids ORDER BY a" : query);

        // Assert
        rows.Select(r => r[0]).ShouldBe(new object?[] { 1L, 2L, 3L });
        rows.Select(r => r[2]).ShouldBe(new object?[] { 1L, 2L, 3L });
        rows.Select(r => r[1]).ShouldBe(new object?[] { 10, 20, 30 });
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Persisted sequences: domains and typed defaults retain CS03074")]
    [InlineData("nextval('s')", false)][InlineData("NEXT VALUE FOR s", false)]
    [InlineData("nextval('s')", true)][InlineData("NEXT VALUE FOR s", true)]
    public async Task Plan_PersistedSequence_ShouldRefuseBeforeLiteralAndCheckValidation(string expression, bool domain)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("sequence-fix", new());
        var db = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await db.CreateSessionAsync(CancellationToken.None);

        // Act / Assert
        var error = domain
            ? await Should.ThrowAsync<DatabaseException>(() => session.ExecuteAsync("CREATE DOMAIN d AS BIGINT CHECK (" + expression + " > 0)", cancellationToken: CancellationToken.None).AsTask())
            : Should.Throw<DatabaseException>(() => SqlSchemaProvisioner.CanonicalDefault(expression));
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS03074");
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Volatile sequences: sort and window precede independent output evaluation")]
    [InlineData("2", false)][InlineData("label", false)][InlineData("k", true)]
    public async Task Execute_OrderedSequence_ShouldNumberDeliveredRows(string order, bool limit)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("sequence-fix", new());
        var db = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await db.CreateSessionAsync(CancellationToken.None);
        await Exec(session, "CREATE SEQUENCE s");
        await Exec(session, "CREATE TABLE t(k INT, name TEXT)");
        await Exec(session, "CREATE TABLE ids(id BIGINT, name TEXT)");
        await Exec(session, "INSERT INTO t VALUES (3,'c'),(1,'a'),(2,'b')");

        // Act
        await Exec(session, "INSERT INTO ids SELECT nextval('s'), name AS label FROM t ORDER BY " + order + (limit ? " LIMIT 1" : ""));
        var rows = await Rows(session, "SELECT * FROM ids ORDER BY name");

        // Assert
        rows.Select(r => r[0]).ShouldBe(limit ? new object?[] { 1L } : new object?[] { 1L, 2L, 3L });
        (await Rows(session, "SELECT nextval('s')"))[0][0].ShouldBe(limit ? 2L : 4L);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Sequence output: grouped and correlated sorting limits reached calls")]
    [InlineData("SELECT nextval('s'), k FROM t GROUP BY k ORDER BY 2 LIMIT 1")]
    [InlineData("SELECT NEXT VALUE FOR s, NEXT VALUE FOR s, k FROM t GROUP BY k ORDER BY 3 LIMIT 1")]
    [InlineData("SELECT nextval('s'), (SELECT u.k FROM t u WHERE u.k = t.k) AS v FROM t ORDER BY v LIMIT 1")]
    [InlineData("SELECT nextval('s'), (SELECT u.k FROM t u WHERE u.k = t.k) AS v FROM t ORDER BY k LIMIT 1")]
    [InlineData("SELECT nextval('s'), k FROM t LIMIT 1 OFFSET 1")]
    public async Task Execute_SequenceOutputAcrossPlans_ShouldApplyWindowFirst(string sql)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("sequence-fix", new());
        var db = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await db.CreateSessionAsync(CancellationToken.None);
        await Exec(session, "CREATE SEQUENCE s"); await Exec(session, "CREATE TABLE t(k INT)");
        await Exec(session, "INSERT INTO t VALUES (3),(1),(2)");

        // Act
        var rows = await Rows(session, sql);

        // Assert
        rows.ShouldHaveSingleItem()[0].ShouldBe(1L);
        rows[0][^1].ShouldBe(1);
        if (rows[0].Length == 3) { rows[0][1].ShouldBe(1L); }
        (await Rows(session, "SELECT nextval('s')"))[0][0].ShouldBe(2L);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Sequence output: volatile alias sort keys evaluate once per input")]
    [InlineData("SELECT nextval('s'), observe(k) AS v FROM t ORDER BY v + 1 LIMIT 1")]
    [InlineData("SELECT nextval('s'), observe(k) AS v FROM t GROUP BY k ORDER BY v + 1 LIMIT 1")]
    [InlineData("SELECT nextval('s'), observe((SELECT u.k FROM t u WHERE u.k=t.k)) AS v FROM t ORDER BY v + 1 LIMIT 1")]
    public async Task Execute_SequenceAndVolatileSortAlias_ShouldEvaluateEachInItsPhase(string sql)
    {
        // Arrange
        int calls = 0;
        var builder = SqlDatabaseEngine.CreateBuilder("sequence-fix");
        builder.Functions.Add(SqlScalarFunction.Create<int, int>("observe", value => { calls++; return value; }));
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var db = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await db.CreateSessionAsync(CancellationToken.None);
        await Exec(session, "CREATE SEQUENCE s"); await Exec(session, "CREATE TABLE t(k INT)");
        await Exec(session, "INSERT INTO t VALUES (3),(1),(2)");

        // Act
        var rows = await Rows(session, sql);

        // Assert
        rows.ShouldHaveSingleItem().ShouldBe(new object?[] { 1L, 1 });
        calls.ShouldBe(3);
        (await Rows(session, "SELECT nextval('s')"))[0][0].ShouldBe(2L);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Sequence sorting: cached aliases keep the declared numeric output type")]
    [InlineData("SELECT nextval('s'), CASE WHEN k>0 THEN k ELSE CAST(0 AS BIGINT) END AS v FROM t GROUP BY k ORDER BY v LIMIT 1")]
    [InlineData("SELECT nextval('s'), CASE WHEN k>0 THEN (SELECT u.k FROM t u WHERE u.k=t.k) ELSE CAST(0 AS BIGINT) END AS v FROM t ORDER BY v LIMIT 1")]
    public async Task Execute_SequenceSortAlias_ShouldKeepPromotedType(string sql)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("sequence-fix", new());
        var db = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await db.CreateSessionAsync(CancellationToken.None);
        await Exec(session, "CREATE SEQUENCE s"); await Exec(session, "CREATE TABLE t(k INT)");
        await Exec(session, "INSERT INTO t VALUES (3),(1),(2)");

        // Act
        var rows = await Rows(session, sql);

        // Assert
        rows.ShouldHaveSingleItem().ShouldBe(new object?[] { 1L, 1L });
        rows[0][1].ShouldBeOfType<long>();
        (await Rows(session, "SELECT nextval('s')"))[0][0].ShouldBe(2L);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Sequence CREATE: existing guard precedes option validation")]
    public async Task Execute_ExistingCreateGuard_ShouldSkipInvalidOptions()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("sequence-fix", new());
        var db = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await db.CreateSessionAsync(CancellationToken.None);
        await Exec(session, "CREATE SEQUENCE s");

        // Act / Assert
        await Exec(session, "CREATE SEQUENCE IF NOT EXISTS s INCREMENT 0");
        (await Rows(session, "SELECT nextval('s')"))[0][0].ShouldBe(1L);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Sequence CREATE: concurrent guards publish once")]
    public async Task Execute_ConcurrentCreateGuard_ShouldSkipAtomically()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var barrier = new Barrier(2);
        await using var engine = SqlDatabaseEngine.Create("sequence-fix", new());
        var db = await engine.CreateDatabaseAsync("db", timeout.Token);
        await using var a = await db.CreateSessionAsync(timeout.Token);
        await using var b = await db.CreateSessionAsync(timeout.Token);
        int arrivals = 0;
        SqlPlanExecutor.BeforeSequenceCreate = () => { Interlocked.Increment(ref arrivals); barrier.SignalAndWait(timeout.Token); };
        Task? first = null, second = null;
        try
        {
            // Act
            first = DedicatedTestTask.Run(() => Exec(a, "CREATE SEQUENCE IF NOT EXISTS s", timeout.Token), timeout.Token);
            second = DedicatedTestTask.Run(() => Exec(b, "CREATE SEQUENCE IF NOT EXISTS s", timeout.Token), timeout.Token);
            await Task.WhenAll(first, second).WaitAsync(timeout.Token);

            // Assert
            arrivals.ShouldBe(2);
            db.Catalog.Sequences.Count.ShouldBe(1);
        }
        finally { SqlPlanExecutor.BeforeSequenceCreate = null; if (first is not null && second is not null) { try { await Task.WhenAll(first, second); } catch { } } }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Sequence IF EXISTS: disappearance during the X wait is skipped")]
    [InlineData("ALTER SEQUENCE IF EXISTS s RESTART")][InlineData("DROP SEQUENCE IF EXISTS s")]
    public async Task Execute_DroppedDuringWait_ShouldSkip(string sql)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = SqlDatabaseEngine.Create("sequence-fix", new());
        var db = await engine.CreateDatabaseAsync("db", timeout.Token);
        await using var session = await db.CreateSessionAsync(timeout.Token);
        await Exec(session, "CREATE SEQUENCE s", timeout.Token);
        var sequence = db.Catalog.FindSequence("dbo", "s")!;
        var owner = await db.Coordinator.BeginAsync(IsolationLevel.ReadCommitted, timeout.Token);
        await db.Coordinator.LockManager.AcquireAsync(owner.Sequence, LockResource.Object(sequence.ObjectId), LockMode.Exclusive, timeout.Token);
        var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        SqlPlanExecutor.BeforeSequenceDdlLock = () => requested.TrySetResult();
        Task? operation = null;
        try
        {
            // Act
            operation = Exec(session, sql, timeout.Token);
            await requested.Task.WaitAsync(timeout.Token);
            operation.IsCompleted.ShouldBeFalse();
            await db.Catalog.DropSequenceAsync(sequence, timeout.Token);
            await db.Coordinator.RollbackAsync(owner, timeout.Token);
            await operation.WaitAsync(timeout.Token);

            // Assert
            db.Catalog.Sequences.ShouldBeEmpty();
        }
        finally { SqlPlanExecutor.BeforeSequenceDdlLock = null; if (owner.State == TransactionState.Active) { await db.Coordinator.RollbackAsync(owner, CancellationToken.None); } if (operation is not null) { try { await operation; } catch { } } }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Sequence ALTER: unrelated index-build metadata lease permits publication")]
    public async Task Execute_UnrelatedCreateIndex_ShouldAllowAlter()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        await using var engine = SqlDatabaseEngine.Create("sequence-fix", new());
        var db = await engine.CreateDatabaseAsync("db", timeout.Token);
        await using var a = await db.CreateSessionAsync(timeout.Token);
        await using var b = await db.CreateSessionAsync(timeout.Token);
        await Exec(a, "CREATE SEQUENCE s");
        await Exec(a, "CREATE TABLE t(k INT)");
        await Exec(a, "INSERT INTO t VALUES (1),(2)");
        int decoded = 0;
        SqlPlanExecutor.RowDecoded = _ => { Interlocked.Increment(ref decoded); entered.Set(); release.Wait(timeout.Token); };
        var build = DedicatedTestTask.Run(() => Exec(a, "CREATE INDEX ix ON t(k)", timeout.Token), timeout.Token);
        try
        {
            entered.Wait(timeout.Token);
            // Act / Assert
            await Exec(b, "ALTER SEQUENCE s INCREMENT 2", timeout.Token);
            release.IsSet.ShouldBeFalse();
            decoded.ShouldBeGreaterThan(0);
        }
        finally { release.Set(); SqlPlanExecutor.RowDecoded = null; await build.WaitAsync(timeout.Token); }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Schema CASCADE: sequence locks precede table locks")]
    public async Task Execute_SchemaDropAgainstInsert_ShouldWaitWithoutDeadlock()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        await using var engine = SqlDatabaseEngine.Create("sequence-fix", new());
        var db = await engine.CreateDatabaseAsync("db", timeout.Token);
        await using var a = await db.CreateSessionAsync(timeout.Token);
        await using var b = await db.CreateSessionAsync(timeout.Token);
        await Exec(a, "CREATE SCHEMA x");
        await Exec(a, "CREATE TABLE x.t(k BIGINT)");
        await Exec(a, "CREATE SEQUENCE x.s");
        db.Catalog.AllocationEntering = () => { entered.Set(); release.Wait(timeout.Token); };
        var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        SqlPlanExecutor.BeforeSequenceDdlLock = () => requested.TrySetResult();
        var insert = DedicatedTestTask.Run(() => Exec(a, "INSERT INTO x.t VALUES (nextval('x.s'))", timeout.Token), timeout.Token);
        Task? drop = null;
        try
        {
            entered.Wait(timeout.Token);
            // Act
            drop = Exec(b, "DROP SCHEMA x CASCADE", timeout.Token);
            await requested.Task.WaitAsync(timeout.Token);
            drop.IsCompleted.ShouldBeFalse();
            release.Set();
            await insert.WaitAsync(timeout.Token);
            await drop.WaitAsync(timeout.Token);
            // Assert
            db.Catalog.Sequences.ShouldBeEmpty();
        }
        finally { release.Set(); db.Catalog.AllocationEntering = null; SqlPlanExecutor.BeforeSequenceDdlLock = null; try { await insert; } catch { } if (drop is not null) { try { await drop; } catch { } } }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Sequence grants: rollback between acquisitions cannot leak a grant")]
    public async Task Open_HostRollbackBetweenGrants_ShouldReleaseFinishedOwner()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = SqlDatabaseEngine.Create("sequence-fix", new());
        var db = await engine.CreateDatabaseAsync("db", timeout.Token);
        await db.Catalog.CreateSequenceAsync("dbo", "a", new());
        var second = await db.Catalog.CreateSequenceAsync("dbo", "b", new());
        var transaction = await db.Coordinator.BeginAsync(IsolationLevel.ReadCommitted, timeout.Token);
        int grants = 0;
        SqlSequenceExecution.AfterSequenceGrant = context =>
        {
            if (Interlocked.Increment(ref grants) == 1) { db.Coordinator.RollbackAsync(transaction, CancellationToken.None).AsTask().GetAwaiter().GetResult(); }
        };
        try
        {
            // Act / Assert
            await Should.ThrowAsync<TransactionAbortedException>(() => SqlSequenceExecution.OpenAsync(db.Catalog,
                new SqlStatementContext(transaction, db.Coordinator), new SqlSequenceSessionState(), timeout.Token).AsTask());
            grants.ShouldBeGreaterThan(0);
            var observer = await db.Coordinator.BeginAsync(IsolationLevel.ReadCommitted, timeout.Token);
            try { db.Coordinator.LockManager.TryAcquire(observer.Sequence, LockResource.Object(second.ObjectId), LockMode.Exclusive).ShouldBeTrue(); }
            finally { await db.Coordinator.RollbackAsync(observer, CancellationToken.None); }
        }
        finally { SqlSequenceExecution.AfterSequenceGrant = null; db.Coordinator.LockManager.ReleaseAll(transaction.Sequence); }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Sequence DDL grants: an ended owner cannot publish or retain X grants")]
    [InlineData("DROP SEQUENCE x.a, x.b")][InlineData("DROP SCHEMA x CASCADE")]
    public async Task Execute_EndedDdlOwner_ShouldReleaseSequenceGrantsBeforePublication(string sql)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = SqlDatabaseEngine.Create("sequence-fix", new());
        var db = await engine.CreateDatabaseAsync("db", timeout.Token);
        await using var session = await db.CreateSessionAsync(timeout.Token);
        await Exec(session, "CREATE SCHEMA x");
        await Exec(session, "CREATE SEQUENCE x.a"); await Exec(session, "CREATE SEQUENCE x.b");
        await Exec(session, "CREATE TABLE x.t(id INT)");
        var plan = new SqlPlanner(db.Catalog, null).Plan(SqlQueryRequest.FromSql(sql).Statement.SqlExpression);
        var transaction = await db.Coordinator.BeginAsync(IsolationLevel.ReadCommitted, timeout.Token);
        int grants = 0;
        SqlSequenceExecution.AfterSequenceGrant = _ =>
        {
            if (Interlocked.Increment(ref grants) == 1) { db.Coordinator.RollbackAsync(transaction, CancellationToken.None).AsTask().GetAwaiter().GetResult(); }
        };
        try
        {
            // Act / Assert
            var executor = new SqlPlanExecutor(db.DataStorage, db.Catalog, db.IndexManager, db.Definitions);
            await Should.ThrowAsync<TransactionAbortedException>(() => executor.ExecuteAsync(plan,
                new SqlStatementContext(transaction, db.Coordinator), timeout.Token));
            grants.ShouldBe(1);
            db.Catalog.Sequences.Count.ShouldBe(2);
            var observer = await db.Coordinator.BeginAsync(IsolationLevel.ReadCommitted, timeout.Token);
            try
            {
                foreach (var sequence in db.Catalog.Sequences)
                { db.Coordinator.LockManager.TryAcquire(observer.Sequence, LockResource.Object(sequence.ObjectId), LockMode.Exclusive).ShouldBeTrue(); }
            }
            finally { await db.Coordinator.RollbackAsync(observer, CancellationToken.None); }
        }
        finally { SqlSequenceExecution.AfterSequenceGrant = null; db.Coordinator.LockManager.ReleaseAll(transaction.Sequence); }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Sequence RETURNING: durable reservations leave the writer gate available")]
    [InlineData("INSERT INTO t VALUES (2) RETURNING nextval('s')")]
    [InlineData("INSERT INTO t VALUES (1) ON CONFLICT(k) DO UPDATE SET k = excluded.k RETURNING setval('s', 9)")]
    [InlineData("UPDATE t SET k = 2 RETURNING nextval('s')")]
    [InlineData("DELETE FROM t RETURNING nextval('s')")]
    [InlineData("UPDATE t SET k = u.k + 1 FROM t u WHERE t.k = u.k RETURNING nextval('s')")]
    [InlineData("DELETE FROM t USING t u WHERE t.k = u.k RETURNING nextval('s')")]
    [InlineData("MERGE INTO t USING (SELECT 1 AS k) u ON t.k = u.k WHEN MATCHED THEN UPDATE SET k = 2 RETURNING nextval('s')")]
    [InlineData("INSERT INTO t SELECT 2 RETURNING nextval('s')")]
    public async Task Execute_ReturningReservationHeld_ShouldAllowAnotherWriter(string sql)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        await using var engine = SqlDatabaseEngine.Create("sequence-fix", new());
        var db = await engine.CreateDatabaseAsync("db", timeout.Token);
        await using var a = await db.CreateSessionAsync(timeout.Token);
        await Exec(a, "CREATE SEQUENCE s");
        await Exec(a, "CREATE TABLE t(k INT PRIMARY KEY)");
        await Exec(a, "INSERT INTO t VALUES (1)");
        var writer = await db.Coordinator.BeginAsync(IsolationLevel.ReadCommitted, timeout.Token);
        int reservations = 0;
        db.Catalog.ReservationCommitting = () => { Interlocked.Increment(ref reservations); entered.Set(); release.Wait(timeout.Token); };
        var operation = DedicatedTestTask.Run(() => Exec(a, sql, timeout.Token), timeout.Token);
        Task<int>? other = null;
        try
        {
            entered.Wait(timeout.Token);
            // Act: IsCompleted is sampled at the deterministic reservation boundary.
            other = db.Coordinator.ApplyStatementAsync(writer, _ => 1, timeout.Token).AsTask();
            bool waited = !other.IsCompleted;
            _output.WriteLine($"writer gate waits while reservation held: {(waited ? 1 : 0)}; reservation callbacks: {reservations}");
            waited.ShouldBeFalse();
            (await other).ShouldBe(1);
            release.IsSet.ShouldBeFalse();
            release.Set();
            await operation.WaitAsync(timeout.Token);
            reservations.ShouldBe(1);
        }
        finally
        {
            release.Set(); db.Catalog.ReservationCommitting = null;
            try { await operation.WaitAsync(timeout.Token); } catch { }
            if (other is not null) { try { await other.WaitAsync(timeout.Token); } catch { } }
            await db.Coordinator.RollbackAsync(writer, CancellationToken.None);
        }
    }
}
