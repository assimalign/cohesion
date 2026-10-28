using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;

namespace Assimalign.Cohesion.Database.Sql.Tests;

[Collection("SqlIdentityConcurrency")]
public sealed class SqlSequenceSqlTests
{
    private readonly ITestOutputHelper _output;
    public SqlSequenceSqlTests(ITestOutputHelper output) { _output = output; }
    [Fact(DisplayName = "Cohesion Test [Sql] - Sequences: session state and setval survive rollback")]
    public async Task ExecuteAsync_SessionState_ShouldRemainNontransactional()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("sequences", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var a = await database.CreateSessionAsync(CancellationToken.None);
        await using var b = await database.CreateSessionAsync(CancellationToken.None);
        await Exec(a, "CREATE SEQUENCE s START 10 INCREMENT 2");
        await Refuses(a, "SELECT currval('s')", "CS04068");
        await Refuses(a, "SELECT lastval()", "CS04069");

        // Act / Assert
        await Exec(a, "BEGIN");
        (await Rows(a, "SELECT nextval('s'), nextval('s'), currval('s'), lastval()"))[0].ShouldBe(new object?[] { 10L, 12L, 12L, 12L });
        await Exec(a, "ROLLBACK");
        (await Rows(a, "SELECT currval('s'), lastval()"))[0].ShouldBe(new object?[] { 12L, 12L });
        await Refuses(b, "SELECT currval('s')", "CS04068");
        (await Rows(b, "SELECT nextval('s')"))[0][0].ShouldBe(14L);
        await Exec(a, "BEGIN");
        (await Rows(a, "SELECT setval('s', 30, false)"))[0][0].ShouldBe(30L);
        await Exec(a, "ROLLBACK");
        (await Rows(a, "SELECT currval('s')"))[0][0].ShouldBe(12L);
        (await Rows(b, "SELECT nextval('s')"))[0][0].ShouldBe(30L);
        (await Rows(a, "SELECT setval('s', 40), currval('s'), lastval()"))[0].ShouldBe(new object?[] { 40L, 40L, 40L });
        (await Rows(b, "SELECT nextval('s')"))[0][0].ShouldBe(42L);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Sequences: NEXT VALUE shares within each INSERT and SELECT row")]
    public async Task ExecuteAsync_RowValues_ShouldShareStandardOccurrencesOnly()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("sequences", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await Exec(session, "CREATE SEQUENCE s");
        await Exec(session, "CREATE TABLE t (a BIGINT, b BIGINT, c BIGINT)");

        // Act
        await Exec(session, "INSERT INTO t VALUES (NEXT VALUE FOR s, NEXT VALUE FOR s, nextval('s')), (NEXT VALUE FOR s, NEXT VALUE FOR s, nextval('s'))");

        // Assert
        var rows = await Rows(session, "SELECT * FROM t ORDER BY a");
        rows[0].ShouldBe(new object?[] { 1L, 1L, 2L });
        rows[1].ShouldBe(new object?[] { 3L, 3L, 4L });
        var selected = await Rows(session, "SELECT NEXT VALUE FOR s, NEXT VALUE FOR s, nextval('s') FROM t ORDER BY a");
        selected[0].ShouldBe(new object?[] { 5L, 5L, 6L });
        selected[1].ShouldBe(new object?[] { 7L, 7L, 8L });
        (await Rows(session, "SELECT nextval('s'::regclass)"))[0][0].ShouldBe(9L);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Sequences: DDL and information schema expose effective options")]
    public async Task ExecuteAsync_Ddl_ShouldPublishOptionsAndRespectGuards()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("sequences", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await Exec(session, "CREATE SEQUENCE s AS SMALLINT START 3 INCREMENT 2 MINVALUE 1 MAXVALUE 99 CACHE 4 CYCLE");
        await Exec(session, "CREATE SEQUENCE IF NOT EXISTS s");

        // Act / Assert
        (await Rows(session, "SELECT * FROM INFORMATION_SCHEMA.SEQUENCES"))[0].ShouldBe(new object?[] { "db", "dbo", "s", "SMALLINT", 16L, 2L, 0L, "3", "1", "99", "2", "YES" });
        await Exec(session, "ALTER SEQUENCE s AS INTEGER NO MAXVALUE CACHE 1 NO CYCLE RESTART WITH 8");
        (await Rows(session, "SELECT nextval('s')"))[0][0].ShouldBe(8L);
        await Exec(session, "ALTER SEQUENCE s START WITH 9 RESTART");
        (await Rows(session, "SELECT nextval('s')"))[0][0].ShouldBe(9L);
        await Exec(session, "ALTER SEQUENCE IF EXISTS absent RESTART");
        await Exec(session, "CREATE SEQUENCE other");
        await Refuses(session, "DROP SEQUENCE s, absent", "CS03073");
        (await Rows(session, "SELECT nextval('s')"))[0][0].ShouldBe(11L);
        await Exec(session, "DROP SEQUENCE s, other RESTRICT");
        (await Rows(session, "SELECT * FROM INFORMATION_SCHEMA.SEQUENCES")).ShouldBeEmpty();
        await Refuses(session, "SELECT lastval()", "CS04069");
        await Exec(session, "DROP SEQUENCE IF EXISTS s CASCADE");
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Sequences: invalid definitions fail with their owned identity")]
    [InlineData("CREATE SEQUENCE s INCREMENT 0", "CS03069")]
    [InlineData("CREATE SEQUENCE s AS TEXT", "CS03069")]
    [InlineData("CREATE SEQUENCE s MINVALUE 2 MAXVALUE 2", "CS03069")]
    [InlineData("CREATE SEQUENCE s CACHE 0", "CS03069")]
    [InlineData("ALTER SEQUENCE missing RESTART", "CS03073")]
    [InlineData("CREATE TABLE t(v BIGINT DEFAULT nextval('s'))", "CS03074")]
    [InlineData("CREATE TABLE t(v BIGINT CHECK (nextval('s') > 0))", "CS03074")]
    [InlineData("CREATE VIEW v AS SELECT nextval('s')", "CS03074")]
    public async Task ExecuteAsync_InvalidSequence_ShouldKeepCatalogUnchanged(string sql, string code)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("sequences", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);

        // Act / Assert
        await Refuses(session, sql, code);
        database.Catalog.Tables.ShouldBeEmpty();
        database.Catalog.Sequences.ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Sequences: DDL inside BEGIN uses the table DDL refusal")]
    [InlineData("CREATE SEQUENCE s")][InlineData("ALTER SEQUENCE s RESTART")][InlineData("DROP SEQUENCE s")]
    public async Task ExecuteAsync_ExplicitTransactionDdl_ShouldRefuse(string sql)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("sequences", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await Exec(session, "BEGIN");

        // Act / Assert
        var result = await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None);
        result.Diagnostics.Single().Code.ShouldBe("CS04003");
        await Exec(session, "ROLLBACK");
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Sequences: crash images preserve SQL nextval and setval")]
    [InlineData(false)][InlineData(true)]
    public async Task ExecuteAsync_DurableCapture_ShouldNotReissueAllocatedValues(bool setValue)
    {
        // Arrange
        var strategy = new CrashCaptureSqlStorageStrategy();
        await using var engine = SqlDatabaseEngine.Create("sequence-source", new() { StorageStrategy = strategy });
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await Exec(session, "CREATE SEQUENCE s");
        (await Rows(session, "SELECT nextval('s')"))[0][0].ShouldBe(1L);
        if (setValue) { await Rows(session, "SELECT setval('s', 70, false)"); }
        var image = strategy.CaptureDurableImages();

        // Act
        await using var recovered = SqlDatabaseEngine.Create("sequence-recovered", new() { StorageStrategy = image });
        var reopened = await recovered.OpenDatabaseAsync("db", CancellationToken.None);
        await using var reader = await reopened.CreateSessionAsync(CancellationToken.None);

        // Assert
        await Refuses(reader, "SELECT currval('s')", "CS04068");
        (await Rows(reader, "SELECT nextval('s')"))[0][0].ShouldBe(setValue ? 70L : 33L);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Sequences: long INSERT SELECT completes before waiting ALTER, RESTART or DROP")]
    [InlineData("ALTER SEQUENCE s INCREMENT 2", 514L)]
    [InlineData("ALTER SEQUENCE s RESTART WITH 9000", 9000L)]
    [InlineData("DROP SEQUENCE s", 0L)]
    public async Task ExecuteAsync_ConcurrentDdl_ShouldNeverFailRunningNextval(string ddl, long next)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = SqlDatabaseEngine.Create("sequence-locks", new());
        var database = await engine.CreateDatabaseAsync("db", timeout.Token);
        await using var writer = await database.CreateSessionAsync(timeout.Token);
        await using var changer = await database.CreateSessionAsync(timeout.Token);
        await Exec(writer, "CREATE SEQUENCE s");
        await Exec(writer, "CREATE TABLE source(id INT)");
        await Exec(writer, "CREATE TABLE target(id BIGINT)");
        await Exec(writer, "INSERT INTO source VALUES " + string.Join(",", Enumerable.Range(1, 512).Select(i => "(" + i + ")")));
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int allocations = 0;
        database.Catalog.AllocationEntering = () =>
        {
            if (Interlocked.Increment(ref allocations) == 1) { entered.Set(); release.Wait(timeout.Token); }
        };
        SqlPlanExecutor.BeforeSequenceDdlLock = () => requested.TrySetResult();
        Task? insert = null;
        Task<DatabaseResult>? change = null;
        try
        {
            // Act: pause after execution acquired the real object grant, before its first reservation.
            insert = Task.Factory.StartNew(() => Exec(writer, "INSERT INTO target SELECT nextval('s') FROM source ORDER BY id"),
                timeout.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
            entered.Wait(timeout.Token);
            change = changer.ExecuteAsync(ddl, cancellationToken: timeout.Token).AsTask();
            await requested.Task.WaitAsync(timeout.Token);
            change.IsCompleted.ShouldBeFalse();
            release.Set();
            await insert.WaitAsync(timeout.Token);
            await change.WaitAsync(timeout.Token);

            // Assert
            allocations.ShouldBe(512);
            var rows = await Rows(writer, "SELECT id FROM target ORDER BY id");
            rows.Select(row => (long)row[0]!).ShouldBe(Enumerable.Range(1, 512).Select(i => (long)i));
            if (next == 0) { await Refuses(writer, "SELECT nextval('s')", "CS03073"); }
            else { (await Rows(writer, "SELECT nextval('s')"))[0][0].ShouldBe(next); }
        }
        finally
        {
            release.Set();
            database.Catalog.AllocationEntering = null;
            SqlPlanExecutor.BeforeSequenceDdlLock = null;
            if (insert is not null) { try { await insert.WaitAsync(timeout.Token); } catch { /* Retain the test failure. */ } }
            if (change is not null) { try { await change.WaitAsync(timeout.Token); } catch { /* Retain the test failure. */ } }
        }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Sequences: repeated transaction endings release ALTER waits and cancelled DDL leaves no grant")]
    public async Task ExecuteAsync_LockLifecycleStress_ShouldExerciseCommitRollbackCancellationAndDisposal()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = SqlDatabaseEngine.Create("sequence-lock-stress", new());
        var database = await engine.CreateDatabaseAsync("db", timeout.Token);
        await using var changer = await database.CreateSessionAsync(timeout.Token);
        await Exec(changer, "CREATE SEQUENCE s");
        int commits = 0, rollbacks = 0, disposals = 0, cancellations = 0, waits = 0;
        var watch = Stopwatch.StartNew();

        // Act: each round must record the real lock request while its holder remains live.
        while (watch.Elapsed < TimeSpan.FromSeconds(1) || waits < 32)
        {
            timeout.Token.ThrowIfCancellationRequested();
            await using var holder = await database.CreateSessionAsync(timeout.Token);
            await Exec(holder, "BEGIN");
            await Rows(holder, waits % 2 == 0 ? "SELECT nextval('s')" : "SELECT setval('s', 5)");
            var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            SqlPlanExecutor.BeforeSequenceDdlLock = () => requested.TrySetResult();
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
            var change = changer.ExecuteAsync("ALTER SEQUENCE s CACHE 2", cancellationToken: cancellation.Token).AsTask();
            try
            {
                await requested.Task.WaitAsync(timeout.Token);
                change.IsCompleted.ShouldBeFalse();
                waits++;
                if (waits % 4 == 0)
                {
                    cancellation.Cancel();
                    await Should.ThrowAsync<OperationCanceledException>(async () => await change);
                    cancellations++;
                }
                switch (waits % 3)
                {
                    case 0: await Exec(holder, "COMMIT"); commits++; break;
                    case 1: await Exec(holder, "ROLLBACK"); rollbacks++; break;
                    default: await holder.DisposeAsync(); disposals++; break;
                }
                if (!cancellation.IsCancellationRequested) { await change.WaitAsync(timeout.Token); }
            }
            finally { SqlPlanExecutor.BeforeSequenceDdlLock = null; }
        }

        // Assert
        waits.ShouldBeGreaterThanOrEqualTo(32);
        _output.WriteLine($"Sequence lifecycle stress: waits={waits}, commits={commits}, rollbacks={rollbacks}, disposals={disposals}, cancelled DDL={cancellations}.");
        commits.ShouldBeGreaterThan(0); rollbacks.ShouldBeGreaterThan(0);
        disposals.ShouldBeGreaterThan(0); cancellations.ShouldBeGreaterThan(0);
        await Exec(changer, "ALTER SEQUENCE s RESTART WITH 100");
        (await Rows(changer, "SELECT nextval('s')"))[0][0].ShouldBe(100L);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Sequences: cached nextval evaluation allocates only its BIGINT result box")]
    public async Task Evaluate_CachedNextval_ShouldReportPerCallAllocation()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("sequence-sql-bytes", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await Exec(session, "CREATE SEQUENCE s");
        await using var transaction = await session.BeginTransactionAsync(CancellationToken.None);
        using var execution = await SqlSequenceExecution.OpenAsync(database.Catalog, new SqlStatementContext(transaction.Context, database.Coordinator),
            session.SequenceState, CancellationToken.None);
        execution.Enter();
        var evaluator = SqlExpressionEvaluator.ForExecution(null);
        var call = new SqlBoundSequence("NEXTVAL", [new SqlBoundConstant("s")]);
        evaluator.Evaluate(call, []).ShouldBe(1L);

        // Act
        long before = GC.GetAllocatedBytesForCurrentThread();
        long total = 0;
        for (int index = 0; index < 20; index++) { total += (long)evaluator.Evaluate(call, [])!; }
        long bytes = GC.GetAllocatedBytesForCurrentThread() - before;

        // Assert
        total.ShouldBe(230L);
        bytes.ShouldBe(20 * 24L);
        _output.WriteLine($"Cached bound nextval: {bytes / 20} managed bytes/call (BIGINT result box); allocator adds zero bytes.");
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Sequences: SQL relation planning waits on a held reservation commit without extra catalog work")]
    public async Task ExecuteAsync_ReservationCommit_ShouldReportRelationPlanningContention()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = SqlDatabaseEngine.Create("sequence-plan-contention", new());
        var database = await engine.CreateDatabaseAsync("db", timeout.Token);
        await using var allocator = await database.CreateSessionAsync(timeout.Token);
        await using var reader = await database.CreateSessionAsync(timeout.Token);
        await Exec(allocator, "CREATE SEQUENCE s");
        await Exec(allocator, "CREATE TABLE t(id INT)");
        await Rows(reader, "SELECT * FROM t");
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var planning = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int reservations = 0;
        database.Catalog.ReservationCommitting = () => { Interlocked.Increment(ref reservations); entered.Set(); release.Wait(timeout.Token); };
        Task? allocation = null, query = null;
        try
        {
            allocation = Task.Factory.StartNew(() => Rows(allocator, "SELECT nextval('s')"), timeout.Token,
                TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
            entered.Wait(timeout.Token);
            SqlQueryExecutor.BeforePlan = _ => planning.TrySetResult();
            var watch = Stopwatch.StartNew();
            query = Task.Factory.StartNew(() => Rows(reader, "SELECT * FROM t"), timeout.Token,
                TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
            await planning.Task.WaitAsync(timeout.Token);

            // Act / Assert
            query.IsCompleted.ShouldBeFalse();
            reservations.ShouldBe(1);
            release.Set();
            await allocation.WaitAsync(timeout.Token);
            await query.WaitAsync(timeout.Token);
            _output.WriteLine($"Relation planning under one deliberately held reservation: {watch.Elapsed.TotalMilliseconds:F3} ms including test gate; reservations={reservations}. Scalar planning keeps SQ-S1's monitor-free generation reads.");
        }
        finally
        {
            release.Set(); database.Catalog.ReservationCommitting = null; SqlQueryExecutor.BeforePlan = null;
            if (allocation is not null) { try { await allocation.WaitAsync(timeout.Token); } catch { /* Retain the original test failure. */ } }
            if (query is not null) { try { await query.WaitAsync(timeout.Token); } catch { /* Retain the original test failure. */ } }
        }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Sequences: CTEs and subqueries inherit execution state and row counts execute once")]
    public async Task ExecuteAsync_NestedScopes_ShouldCarrySequenceStateAndEvaluateCountsOnce()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("sequence-scopes", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await Exec(session, "CREATE SEQUENCE s");

        // Act / Assert
        (await Rows(session, "WITH c AS (SELECT nextval('s') AS n) SELECT n FROM c"))[0][0].ShouldBe(1L);
        (await Rows(session, "SELECT (SELECT nextval('s'))"))[0][0].ShouldBe(2L);
        (await Rows(session, "SELECT 1 LIMIT nextval('s')")).ShouldHaveSingleItem()[0].ShouldBe(1L);
        (await Rows(session, "SELECT currval('s')"))[0][0].ShouldBe(3L);
        await Refuses(session, "SELECT setval('s', 0)", "CS03075");
        await Refuses(session, "SELECT nextval('s..invalid')", "CS03077");
        await Refuses(session, "SELECT nextval('')", "CS03077");
        await Refuses(session, "SELECT nextval('s') FILTER (WHERE TRUE)", "CS05072");
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Sequences: static names lock only their objects and dynamic names remain supported")]
    public async Task ExecuteAsync_ReferenceLocks_ShouldAllowUnrelatedAlterAndDynamicNames()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = SqlDatabaseEngine.Create("sequence-names", new());
        var database = await engine.CreateDatabaseAsync("db", timeout.Token);
        await using var a = await database.CreateSessionAsync(timeout.Token);
        await using var b = await database.CreateSessionAsync(timeout.Token);
        await Exec(a, "CREATE SEQUENCE s"); await Exec(a, "CREATE SEQUENCE other");
        await Exec(a, "CREATE TABLE names(n TEXT)");
        await Exec(a, "INSERT INTO names VALUES ('s'), ('other')");
        await Exec(a, "BEGIN");
        await Rows(a, "SELECT nextval('s')");

        // Act / Assert: this must complete while s's Shared grant is still held.
        await b.ExecuteAsync("ALTER SEQUENCE other RESTART WITH 50", cancellationToken: timeout.Token);
        await Exec(a, "ROLLBACK");
        (await Rows(a, "SELECT nextval(n) FROM names ORDER BY n")).Select(row => row[0]).ShouldBe(new object?[] { 50L, 2L });
        await Refuses(a, "SELECT NEXT VALUE FOR s WHERE NEXT VALUE FOR s > 0", "CS03076");
        await Refuses(a, "SELECT 1 ORDER BY NEXT VALUE FOR s", "CS03076");
        await Refuses(a, "SELECT 1 LIMIT NEXT VALUE FOR s", "CS03076");
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Sequences: schema-drop sequence waits are visible to deadlock detection")]
    public async Task ExecuteAsync_SequenceTableCycle_ShouldDetectDeadlockAndReleaseOnRollback()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = SqlDatabaseEngine.Create("sequence-deadlock", new());
        var database = await engine.CreateDatabaseAsync("db", timeout.Token);
        await using var a = await database.CreateSessionAsync(timeout.Token);
        await using var b = await database.CreateSessionAsync(timeout.Token);
        await Exec(a, "CREATE SCHEMA sales"); await Exec(a, "CREATE TABLE sales.t(id INT)");
        await Exec(a, "CREATE SEQUENCE sales.s");
        await Exec(a, "BEGIN"); await Rows(a, "SELECT nextval('sales.s')");
        var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        SqlPlanExecutor.BeforeSequenceDdlLock = () => requested.TrySetResult();
        Task<DatabaseResult>? drop = null;
        try
        {
            // Act: schema drop holds the table and waits for the sequence; INSERT closes the cycle.
            drop = b.ExecuteAsync("DROP SCHEMA sales CASCADE", cancellationToken: timeout.Token).AsTask();
            await requested.Task.WaitAsync(timeout.Token);
            drop.IsCompleted.ShouldBeFalse();
            await Refuses(a, "INSERT INTO sales.t VALUES (1)", "CS04065");
            await Exec(a, "ROLLBACK");
            await drop.WaitAsync(timeout.Token);

            // Assert
            database.Catalog.FindSequence("sales", "s").ShouldBeNull();
            await Exec(b, "CREATE SEQUENCE fresh");
        }
        finally
        {
            SqlPlanExecutor.BeforeSequenceDdlLock = null;
            if (a.CurrentTransaction is { } transaction) { await transaction.RollbackAsync(CancellationToken.None); }
            if (drop is not null) { try { await drop.WaitAsync(timeout.Token); } catch { /* Retain the test failure. */ } }
        }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Sequences: exhaustion rolls back rows while retaining handed-out session values")]
    public async Task ExecuteAsync_Exhaustion_ShouldUndoRowsAndRetainSessionValues()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("sequence-exhaustion", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await Exec(session, "CREATE SEQUENCE s MAXVALUE 2"); await Exec(session, "CREATE TABLE t(id BIGINT)");

        // Act / Assert
        await Refuses(session, "INSERT INTO t VALUES (nextval('s')), (nextval('s')), (nextval('s'))", "CS02002");
        (await Rows(session, "SELECT * FROM t")).ShouldBeEmpty();
        (await Rows(session, "SELECT currval('s'), lastval()"))[0].ShouldBe(new object?[] { 2L, 2L });
        await Refuses(session, "SELECT nextval('s')", "CS02002");
        await Refuses(session, "SELECT nextval(TRUE) FROM t", "CS01006");
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Sequences: setval true defines currval without selecting lastval and EXPLAIN allocates only under ANALYZE")]
    public async Task ExecuteAsync_SetvalAndExplain_ShouldFollowSessionAndEffectBoundaries()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("sequence-explain", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await Rows(session, "EXPLAIN CREATE SEQUENCE s");
        database.Catalog.Sequences.ShouldBeEmpty();
        await Exec(session, "CREATE SEQUENCE s");
        await Exec(session, "CREATE SEQUENCE other");

        // Act / Assert
        (await Rows(session, "SELECT setval('s', 8), currval('s')"))[0].ShouldBe(new object?[] { 8L, 8L });
        await Refuses(session, "SELECT lastval()", "CS04069");
        await Rows(session, "EXPLAIN SELECT nextval('s')");
        (await Rows(session, "SELECT nextval('s')"))[0][0].ShouldBe(9L);
        await Rows(session, "EXPLAIN ANALYZE SELECT nextval('s')");
        (await Rows(session, "SELECT currval('s')"))[0][0].ShouldBe(10L);
        (await Rows(session, "SELECT setval('other', 50), currval('other'), lastval()"))[0].ShouldBe(new object?[] { 50L, 50L, 10L });
        await Refuses(session, "CREATE SEQUENCE INFORMATION_SCHEMA.s", "CS04021");
        await Refuses(session, "DROP SEQUENCE INFORMATION_SCHEMA.SEQUENCES", "CS04021");
        await Rows(session, "EXPLAIN DROP SEQUENCE s");
        database.Catalog.FindSequence("dbo", "s").ShouldNotBeNull();
        await Exec(session, "CREATE SCHEMA sequence_schema");
        await Exec(session, "CREATE SEQUENCE sequence_schema.s");
        await Refuses(session, "DROP SCHEMA sequence_schema RESTRICT", "CS03030");
        await Exec(session, "DROP SCHEMA sequence_schema CASCADE");
        database.Catalog.FindSequence("sequence_schema", "s").ShouldBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Sequences: allocator closure also fences setval after engine disposal")]
    [InlineData(false)][InlineData(true)]
    public async Task SetSequence_AfterDisposal_ShouldRefuseWithoutPublishing(bool synchronous)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("setval-disposal", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await Exec(session, "CREATE SEQUENCE s");
        var sequence = database.Catalog.FindSequence("dbo", "s")!;

        // Act
        if (synchronous) { engine.Dispose(); } else { await engine.DisposeAsync(); }

        // Assert
        Should.Throw<ObjectDisposedException>(() => database.Catalog.SetSequence(sequence, 8, false));
    }

    private static async Task Exec(SqlDatabaseSession session, string sql)
    { var result = await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None); result.Diagnostics.ShouldNotContain(d => d.Severity == Types.DiagnosticSeverity.Error); }
    private static async Task Refuses(SqlDatabaseSession session, string sql, string code)
    { var failure = await Should.ThrowAsync<DatabaseException>(() => Exec(session, sql)); failure.Diagnostics[0].Code.ShouldBe(code); }
    private static async Task<List<object?[]>> Rows(SqlDatabaseSession session, string sql)
    {
        await using var result = (DatabaseResultSet)await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None);
        var rows = new List<object?[]>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None)) { rows.Add(Enumerable.Range(0, row.FieldCount).Select(row.GetValue).ToArray()); }
        return rows;
    }
}
