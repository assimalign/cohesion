using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Schema;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

[Collection(SqlTimingCollection.Name)]
public sealed class SqlPartialIndexFixTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - Partial indexes: every CASE cast alternative must be immutable (#1630)")]
    [InlineData("CASE WHEN active THEN 1 ELSE TRUE END")]
    [InlineData("CASE WHEN TRUE THEN 1 ELSE TRUE END")]
    [InlineData("COALESCE(k, TRUE)")]
    public async Task ExecuteAsync_MutableAlternativeCast_ShouldRefuseBeforeCallback(string operand)
    {
        // Arrange
        int calls = 0, result = 1;
        var builder = SqlDatabaseEngine.CreateBuilder("partial-cast-fix");
        builder.Types.AddCast(SqlCast.Create<bool, int>(SqlType.Boolean, SqlType.Integer,
            _ => { calls++; return result; }, SqlCastContext.Explicit));
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SqlPartialIndexTests.Execute(session, "CREATE TABLE t(k INT,active BOOLEAN)");
        await SqlPartialIndexTests.Execute(session, "INSERT INTO t VALUES(NULL,FALSE)");

        // Act / Assert
        await SqlPartialIndexTests.Refuses(session, $"CREATE INDEX ix ON t(k) WHERE CAST({operand} AS INT)>0", "CS05091");
        result = 0;
        calls.ShouldBe(0);
        database.IndexManager.ExportRegistrations().ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Partial indexes: unavailable calls allow reads and refuse every write after reopen (#1630)")]
    [InlineData("missing", "active OR positive(k)")]
    [InlineData("missing", "positive(k)")]
    [InlineData("stable", "active OR positive(k)")]
    public async Task OpenDatabaseAsync_UnavailablePredicateFunction_ShouldReadAndRefuseAllWrites(string registration, string predicate)
    {
        // Arrange
        var storage = new CrashCaptureSqlStorageStrategy();
        var builder = SqlDatabaseEngine.CreateBuilder("partial-unresolved");
        builder.Options.StorageStrategy = storage;
        builder.Functions.Add(SqlScalarFunction.Create("positive", static (int value) => value > 0, SqlFunctionVolatility.Immutable));
        await using var original = await builder.BuildAsync(CancellationToken.None);
        var first = await original.CreateDatabaseAsync("db", CancellationToken.None);
        await using var writer = await first.CreateSessionAsync(CancellationToken.None);
        await SqlPartialIndexTests.Execute(writer, "CREATE TABLE t(k INT,active BOOLEAN)");
        await SqlPartialIndexTests.Execute(writer, "INSERT INTO t VALUES(1,TRUE)");
        await SqlPartialIndexTests.Execute(writer, "CREATE INDEX ix ON t(k) WHERE " + predicate);
        var next = SqlDatabaseEngine.CreateBuilder("partial-unresolved");
        next.Options.StorageStrategy = storage.CaptureDurableImages();
        if (registration == "stable")
        { next.Functions.Add(SqlScalarFunction.Create("positive", static (int value) => value > 0, SqlFunctionVolatility.Stable)); }
        await using var reopened = await next.BuildAsync(CancellationToken.None);

        // Act
        var database = await reopened.OpenDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);

        // Assert: even writes whose row never reaches the call fail before mutation.
        (await SqlPartialIndexTests.Rows(session, "SELECT * FROM t")).ShouldBe([[1, true]]);
        foreach (string sql in new[] { "INSERT INTO t VALUES(2,TRUE)", "INSERT INTO t VALUES(2,FALSE)",
            "UPDATE t SET active=TRUE", "DELETE FROM t", "UPDATE t SET active=TRUE WHERE FALSE", "DELETE FROM t WHERE FALSE" })
        { await SqlPartialIndexTests.Refuses(session, sql, "CS01009"); }
        (await SqlPartialIndexTests.Rows(session, "SELECT * FROM t")).ShouldBe([[1, true]]);
        if (registration == "missing")
        {
            await SqlPartialIndexTests.Execute(session, "CREATE FUNCTION positive(k INT) RETURNS BOOLEAN IMMUTABLE RETURN k>0");
            await SqlPartialIndexTests.Execute(session, "INSERT INTO t VALUES(3,TRUE)");
        }
        await SqlPartialIndexTests.Execute(session, "DROP INDEX ix ON t");
        await SqlPartialIndexTests.Execute(session, "INSERT INTO t VALUES(2,TRUE)");
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Conformance: untargeted DO NOTHING arbitrates partial uniqueness by row membership (#1630)")]
    public async Task ExecuteAsync_UntargetedConflict_ShouldSkipOnlyMatchingNonNullKeys()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("partial-conflict-fix", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SqlPartialIndexTests.Execute(session, "CREATE TABLE t(id INT PRIMARY KEY,k INT,active BOOLEAN)");
        await SqlPartialIndexTests.Execute(session, "CREATE UNIQUE INDEX uq ON t(k) WHERE active");
        await SqlPartialIndexTests.Execute(session, "CREATE UNIQUE INDEX other ON t(k) WHERE NOT active");
        await SqlPartialIndexTests.Execute(session, "INSERT INTO t VALUES(1,7,TRUE)");

        // Act
        var result = await session.ExecuteAsync("INSERT INTO t VALUES(2,7,TRUE),(3,7,FALSE),(4,7,FALSE),(5,7,NULL),(6,NULL,TRUE),(7,NULL,TRUE),(8,8,TRUE),(9,8,TRUE) ON CONFLICT DO NOTHING RETURNING id", cancellationToken: CancellationToken.None);

        // Assert
        result.AffectedCount.ShouldBe(5);
        await using (var rows = result.ShouldBeAssignableTo<DatabaseResultSet>())
        {
            int returned = 0;
            await foreach (var row in rows.GetRowsAsync(CancellationToken.None)) { returned++; }
            returned.ShouldBe(5);
        }
        (await SqlPartialIndexTests.Rows(session, "SELECT id FROM t ORDER BY id")).ShouldBe([[1], [3], [5], [6], [7], [8]]);
        await SqlPartialIndexTests.Refuses(session, "INSERT INTO t VALUES(10,7,TRUE) ON CONFLICT(k) DO NOTHING", "CS03035");
        await SqlPartialIndexTests.Refuses(session, "INSERT INTO t VALUES(10,7,TRUE) ON CONFLICT ON CONSTRAINT uq DO NOTHING", "CS03035");
        // Targeted arbitration must not evaluate a non-arbiter's partial predicate
        // for a proposal the unconditional target discards.
        await SqlPartialIndexTests.Execute(session, "CREATE TABLE guard(id INT PRIMARY KEY,k INT)");
        await SqlPartialIndexTests.Execute(session, "INSERT INTO guard VALUES(1,1)");
        await SqlPartialIndexTests.Execute(session, "CREATE UNIQUE INDEX guarded ON guard(k) WHERE 10/k>0");
        (await session.ExecuteAsync("INSERT INTO guard VALUES(1,0) ON CONFLICT(id) DO NOTHING", cancellationToken: CancellationToken.None)).AffectedCount.ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Partial uniqueness: NULL inserts and tombstones never wait for another NULL key (#1630)")]
    [InlineData("INSERT INTO t VALUES(3,NULL,TRUE)")]
    [InlineData("UPDATE t SET active=FALSE WHERE id=2")]
    [InlineData("DELETE FROM t WHERE id=2")]
    [InlineData("INSERT INTO t VALUES(3,NULL,TRUE) ON CONFLICT DO NOTHING")]
    public async Task ExecuteAsync_NullKeyInAnotherTransaction_ShouldCompleteWithoutKeyWait(string sql)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = SqlDatabaseEngine.Create("partial-null-lock", new());
        var database = await engine.CreateDatabaseAsync("db", timeout.Token);
        await using var first = await database.CreateSessionAsync(timeout.Token);
        await using var second = await database.CreateSessionAsync(timeout.Token);
        await SqlPartialIndexTests.Execute(first, "CREATE TABLE t(id INT PRIMARY KEY,k INT,active BOOLEAN)");
        await SqlPartialIndexTests.Execute(first, "CREATE UNIQUE INDEX uq ON t(k) WHERE active");
        await SqlPartialIndexTests.Execute(first, "INSERT INTO t VALUES(2,NULL,TRUE)");
        await using var held = await first.BeginTransactionAsync(IsolationLevel.ReadCommitted, timeout.Token);
        await first.ExecuteAsync("INSERT INTO t VALUES(1,NULL,TRUE)", cancellationToken: timeout.Token);
        await using var other = await second.BeginTransactionAsync(IsolationLevel.ReadCommitted, timeout.Token);
        var waited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var listener = new LockWaitListener(database.Name);
        listener.Observed = written => { if (written.EventName == "LockWaitStart") { waited.TrySetResult(); } };

        // Act: recorded progress decides nonblocking; timeout only guards a hang.
        var pending = second.ExecuteAsync(sql, cancellationToken: timeout.Token).AsTask();
        bool completed = ReferenceEquals(await Task.WhenAny(pending, waited.Task).WaitAsync(timeout.Token), pending);
        await held.RollbackAsync(timeout.Token);
        await pending;
        await other.RollbackAsync(timeout.Token);

        // Assert: rollback must restore a tombstoned NULL entry as well.
        completed.ShouldBeTrue("NULL keys must not acquire uniqueness locks");
        (await SqlPartialIndexTests.Rows(first, "SELECT id FROM t")).ShouldBe([[2]]);
        database.IndexManager.TryGetIndex(database.Catalog.Tables.Single().ObjectId, "uq", out var index).ShouldBeTrue();
        await using var read = await first.BeginTransactionAsync(IsolationLevel.Snapshot, timeout.Token);
        await using var cursor = index.OpenCursor(read.Context, Indexing.IndexKeyRange.All);
        (await cursor.MoveNextAsync(timeout.Token)).ShouldBeTrue();
        (await cursor.MoveNextAsync(timeout.Token)).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Partial indexes: compiled schema retypes predicate columns between index drop and add (#1630)")]
    public async Task ApplySchemaAsync_PredicateColumnTypeChange_ShouldRebuildAroundAlter()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("partial-retype-fix", new());
        var database = await engine.CreateDatabaseAsync("app", CancellationToken.None);
        SqlCompiledSchema Schema(int length) => new(SqlCompiledSchema.CurrentFormat, "app", true, [],
            [new("t", "Tests:Row", [new("k", DatabaseType.Int32, true), new("v", DatabaseType.String, true, MaxLength: length)], null,
                [new("uq", ["k"], true, "v = 'yes'")], [])], []);
        await database.ApplySchemaAsync(Schema(20), CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SqlPartialIndexTests.Execute(session, "INSERT INTO t VALUES(7,'yes'),(7,'no'),(NULL,'yes'),(NULL,'yes')");
        var desired = Schema(50);

        // Act
        var result = await database.ApplySchemaAsync(desired, CancellationToken.None);
        await database.VerifySchemaAsync(desired, CancellationToken.None);

        // Assert
        result.OperationCount.ShouldBe(3);
        (await SqlPartialIndexTests.Rows(session, "SELECT v FROM t WHERE k=7 ORDER BY v")).ShouldBe([["no"], ["yes"]]);
        await SqlConstraintAssertions.ThrowAsync(async () => await SqlPartialIndexTests.Execute(session, "INSERT INTO t VALUES(7,'yes')"));
        await SqlPartialIndexTests.Execute(session, "INSERT INTO t VALUES(7,'no')");
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Conformance: predicate column widening consistently refuses CS03083 (#1630)")]
    public async Task ExecuteAsync_PredicateColumnWidening_ShouldRequireIndexDrop()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("partial-widen-fix", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SqlPartialIndexTests.Execute(session, "CREATE TABLE t(k INT,v VARCHAR(20))");
        await SqlPartialIndexTests.Execute(session, "CREATE INDEX ix ON t(k) WHERE v='yes'");

        // Act / Assert
        await SqlPartialIndexTests.Refuses(session, "ALTER TABLE t ALTER COLUMN v TYPE VARCHAR(50)", "CS03083");
        database.Catalog.Tables.Single().Columns[1].Type.MaxLength.ShouldBe(20);
        await SqlPartialIndexTests.Execute(session, "DROP INDEX ix ON t");
        await SqlPartialIndexTests.Execute(session, "ALTER TABLE t ALTER COLUMN v TYPE VARCHAR(50)");
    }
}
