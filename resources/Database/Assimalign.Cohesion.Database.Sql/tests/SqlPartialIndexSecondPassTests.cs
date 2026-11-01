using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;

namespace Assimalign.Cohesion.Database.Sql.Tests;

[Collection(SqlTimingCollection.Name)]
public sealed class SqlPartialIndexSecondPassTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - Partial predicates: unreachable application cast slots permit creation and maintain the scan subset (#1630)")]
    [InlineData("k")]
    [InlineData("qty")]
    [InlineData("1")]
    [InlineData("identity_int(k)")]
    [InlineData("CASE WHEN active THEN k ELSE qty END")]
    [InlineData("COALESCE(k,qty)")]
    [InlineData("NULLIF(k,qty)")]
    public async Task ExecuteAsync_UnreachableCastSlot_ShouldMaintainIndex(string operand)
    {
        // Arrange
        int calls = 0;
        var builder = SqlDatabaseEngine.CreateBuilder("partial-slots");
        builder.Types.AddCast(SqlCast.Create<bool, int>(SqlType.Boolean, SqlType.Integer,
            _ => { calls++; return 1; }, SqlCastContext.Explicit));
        builder.Functions.Add(SqlScalarFunction.Create("identity_int", static (int value) => value, SqlFunctionVolatility.Immutable));
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SqlPartialIndexTests.Execute(session, "CREATE TABLE t(id INT,k INT,qty NUMERIC,active BOOLEAN)");
        string predicate = $"CAST({operand} AS INT)>0";

        // Act
        await SqlPartialIndexTests.Execute(session, "CREATE INDEX ix ON t(k) WHERE " + predicate);
        await SqlPartialIndexTests.Execute(session, "INSERT INTO t VALUES(1,1,2,TRUE),(2,-2,-3,FALSE),(3,3,NULL,NULL)");
        await SqlPartialIndexTests.Oracle(database, session, predicate: predicate);
        await SqlPartialIndexTests.Execute(session, "UPDATE t SET k=4,qty=5 WHERE id=2");
        await SqlPartialIndexTests.Oracle(database, session, predicate: predicate);
        await SqlPartialIndexTests.Execute(session, "DELETE FROM t WHERE id=1");

        // Assert
        await SqlPartialIndexTests.Oracle(database, session, predicate: predicate);
        calls.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Partial predicates: adding an unreachable cast at reopen preserves normal binding (#1630)")]
    public async Task OpenDatabaseAsync_UnreachableCastAdded_ShouldMaintainIndex()
    {
        // Arrange
        var storage = new CrashCaptureSqlStorageStrategy();
        var builder = SqlDatabaseEngine.CreateBuilder("partial-reopen-slot");
        builder.Options.StorageStrategy = storage;
        await using var original = await builder.BuildAsync(CancellationToken.None);
        var first = await original.CreateDatabaseAsync("db", CancellationToken.None);
        await using var writer = await first.CreateSessionAsync(CancellationToken.None);
        await SqlPartialIndexTests.Execute(writer, "CREATE TABLE t(id INT,k INT,code TEXT,qty NUMERIC)");
        await SqlPartialIndexTests.Execute(writer, "CREATE UNIQUE INDEX ix ON t(k) WHERE CAST(qty AS INT)>0");
        await SqlPartialIndexTests.Execute(writer, "INSERT INTO t VALUES(1,7,'a',2),(2,7,'b',-2)");
        int calls = 0;
        var next = SqlDatabaseEngine.CreateBuilder("partial-reopen-slot");
        next.Options.StorageStrategy = storage.CaptureDurableImages();
        next.Types.AddCast(SqlCast.Create<bool, int>(SqlType.Boolean, SqlType.Integer,
            _ => { calls++; return 1; }, SqlCastContext.Explicit));
        await using var reopened = await next.BuildAsync(CancellationToken.None);

        // Act
        var database = await reopened.OpenDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SqlPartialIndexTests.Execute(session, "INSERT INTO t VALUES(3,8,'c',3)");
        await SqlPartialIndexTests.Execute(session, "UPDATE t SET qty=0 WHERE id=1");
        await SqlPartialIndexTests.Execute(session, "UPDATE t SET qty=2 WHERE id=2");

        // Assert
        await SqlPartialIndexTests.Oracle(database, session, predicate: "CAST(qty AS INT)>0");
        await SqlConstraintAssertions.ThrowAsync(async () => await SqlPartialIndexTests.Execute(session, "INSERT INTO t VALUES(4,7,'d',1)"));
        calls.ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Partial predicates: changed registration opens for reads and index removal but refuses writes (#1630)")]
    [InlineData("result", "positive(k)", "returns")]
    [InlineData("result", "positive(k)=TRUE", "returns")]
    [InlineData("cast", "CAST(k AS INT)>0", "cast")]
    [InlineData("cast", "TRUE OR CAST(k AS INT)>0", "cast")]
    public async Task OpenDatabaseAsync_ChangedRegistration_ShouldDeferNamedFailure(string change, string predicate, string reason)
    {
        // Arrange
        var storage = new CrashCaptureSqlStorageStrategy();
        var builder = SqlDatabaseEngine.CreateBuilder("partial-registration");
        builder.Options.StorageStrategy = storage;
        builder.Types.Add(SqlType.CreateDomain("native_int", SqlType.Integer,
            SqlScalarFunction.Create("native_check", static (int value) => value >= 0, SqlFunctionVolatility.Immutable)));
        builder.Functions.Add(SqlScalarFunction.Create("positive", static (int value) => value > 0, SqlFunctionVolatility.Immutable));
        await using var original = await builder.BuildAsync(CancellationToken.None);
        var first = await original.CreateDatabaseAsync("db", CancellationToken.None);
        await using var writer = await first.CreateSessionAsync(CancellationToken.None);
        await SqlPartialIndexTests.Execute(writer, "CREATE TABLE t(k " + (change == "cast" ? "native_int" : "INT") + ")");
        await SqlPartialIndexTests.Execute(writer, "INSERT INTO t VALUES(1)");
        await SqlPartialIndexTests.Execute(writer, "CREATE INDEX ix ON t(k) WHERE " + predicate);
        var next = SqlDatabaseEngine.CreateBuilder("partial-registration");
        next.Options.StorageStrategy = storage.CaptureDurableImages();
        var domain = SqlType.CreateDomain("native_int", SqlType.Integer,
            SqlScalarFunction.Create("native_check", static (int value) => value >= 0, SqlFunctionVolatility.Immutable));
        next.Types.Add(domain);
        int callbacks = 0;
        if (change == "cast")
        { next.Types.AddCast(SqlCast.Create<int, int>(domain, SqlType.Integer, value => { callbacks++; return value; }, SqlCastContext.Explicit)); }
        next.Functions.Add(SqlScalarFunction.Create("positive", (int value) => { callbacks++; return value; }, SqlFunctionVolatility.Immutable));
        await using var reopened = await next.BuildAsync(CancellationToken.None);

        // Act
        var database = await reopened.OpenDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);

        // Assert
        (await SqlPartialIndexTests.Rows(session, "SELECT * FROM t")).ShouldBe([[1]]);
        (await SqlPartialIndexTests.Rows(session, "SELECT PREDICATE FROM COHESION_SCHEMA.INDEXES WHERE INDEX_NAME='ix'")).Length.ShouldBe(1);
        var failure = await Should.ThrowAsync<DatabaseException>(() => SqlPartialIndexTests.Execute(session, "INSERT INTO t VALUES(2)"));
        failure.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "CS01009");
        failure.Message.ShouldContain(reason);
        foreach (string sql in new[] { "UPDATE t SET k=2 WHERE FALSE", "DELETE FROM t WHERE FALSE", "INSERT INTO t SELECT 2 WHERE FALSE" })
        { await SqlPartialIndexTests.Refuses(session, sql, "CS01009"); }
        callbacks.ShouldBe(0);
        await SqlPartialIndexTests.Execute(session, "DROP INDEX ix ON t");
        await SqlPartialIndexTests.Execute(session, "INSERT INTO t VALUES(2)");
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Conformance: untargeted DO NOTHING reaches predicates only after constraints and earlier arbiters (#1630)")]
    [InlineData(false, "(1,0)", null)]
    [InlineData(true, "(1,0)", null)]
    [InlineData(false, "(2,1),(3,0)", "CS03038")]
    [InlineData(true, "(2,0)", "CS03038")]
    [InlineData(false, "(3,0)", "CS02001")]
    [InlineData(true, "(3,0)", "CS02001")]
    public async Task ExecuteAsync_DeferredPredicateFailure_ShouldRespectRowAndArbiterOrder(bool orderedDefaults, string values, string? code)
    {
        // Arrange
        var builder = SqlDatabaseEngine.CreateBuilder("partial-error-order");
        builder.Functions.Add(SqlScalarFunction.Create("requested_default", static (int value) => value, SqlFunctionVolatility.Volatile));
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SqlPartialIndexTests.Execute(session, "CREATE TABLE guard(id INT PRIMARY KEY,k INT,v INT DEFAULT requested_default(1),CONSTRAINT ck CHECK(id<>2))");
        await SqlPartialIndexTests.Execute(session, "INSERT INTO guard(id,k,v) VALUES(1,1,1)");
        await SqlPartialIndexTests.Execute(session, "CREATE UNIQUE INDEX g ON guard(k) WHERE 10/k>0");
        string sql = "INSERT INTO guard(id,k" + (orderedDefaults ? ") VALUES" : ",v) VALUES") +
            (orderedDefaults ? values : values.Replace(")", ",1)")) + " ON CONFLICT DO NOTHING RETURNING id";

        // Act / Assert
        if (code is null)
        {
            await using var result = (await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();
            result.AffectedCount.ShouldBe(0);
            int rows = 0;
            await foreach (var row in result.GetRowsAsync(CancellationToken.None)) { rows++; }
            rows.ShouldBe(0);
        }
        else { await SqlPartialIndexTests.Refuses(session, sql, code); }
        (await SqlPartialIndexTests.Rows(session, "SELECT id,k FROM guard")).ShouldBe([[1, 1]]);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Partial predicates: unrelated writes and remedied generations avoid catalog validation (#1630)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_UnresolvedOtherTableOrRestoredFunction_ShouldAvoidValidation(bool remedy)
    {
        // Arrange
        var storage = new CrashCaptureSqlStorageStrategy();
        var builder = SqlDatabaseEngine.CreateBuilder("partial-validation-cost");
        builder.Options.StorageStrategy = storage;
        builder.Functions.Add(SqlScalarFunction.Create("positive", static (int value) => value > 0, SqlFunctionVolatility.Immutable));
        await using var original = await builder.BuildAsync(CancellationToken.None);
        var first = await original.CreateDatabaseAsync("db", CancellationToken.None);
        await using var writer = await first.CreateSessionAsync(CancellationToken.None);
        await SqlPartialIndexTests.Execute(writer, "CREATE TABLE t(k INT)");
        await SqlPartialIndexTests.Execute(writer, "CREATE INDEX ix ON t(k) WHERE positive(k)");
        await SqlPartialIndexTests.Execute(writer, "CREATE TABLE plain(k INT)");
        var next = SqlDatabaseEngine.CreateBuilder("partial-validation-cost");
        next.Options.StorageStrategy = storage.CaptureDurableImages();
        await using var reopened = await next.BuildAsync(CancellationToken.None);
        var database = await reopened.OpenDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        if (remedy)
        {
            await SqlPartialIndexTests.Execute(session, "CREATE FUNCTION positive(k INT) RETURNS BOOLEAN IMMUTABLE RETURN k>0");
            database.Definitions.FindUnresolved(CancellationToken.None).ShouldBeNull();
        }
        int before = database.Definitions.ValidationWalkCount;

        // Act: each DDL invalidates the catalog validation entry, without changing either target.
        for (int step = 0; step < 3; step++)
        {
            await SqlPartialIndexTests.Execute(session, $"CREATE TABLE unrelated{step}(k INT)");
            string target = remedy ? "t" : "plain";
            foreach (string sql in new[] { $"INSERT INTO {target} VALUES({step + 1})",
                $"UPDATE {target} SET k=1 WHERE FALSE", $"DELETE FROM {target} WHERE FALSE",
                $"MERGE INTO {target} USING (SELECT 1 AS k WHERE FALSE) s ON {target}.k=s.k WHEN NOT MATCHED THEN INSERT VALUES(s.k)" })
            { await SqlPartialIndexTests.Execute(session, sql); }
        }

        // Assert
        database.Definitions.ValidationWalkCount.ShouldBe(before);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Conformance: arbiter creation order survives a dropped directory slot and reopen (#1630)")]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ExecuteAsync_ReusedIndexDirectorySlot_ShouldKeepCreationOrder(bool reopen, bool rewrite)
    {
        // Arrange
        var storage = new CrashCaptureSqlStorageStrategy();
        var builder = SqlDatabaseEngine.CreateBuilder("partial-index-order");
        builder.Options.StorageStrategy = storage;
        await using var original = await builder.BuildAsync(CancellationToken.None);
        var first = await original.CreateDatabaseAsync("db", CancellationToken.None);
        await using var writer = await first.CreateSessionAsync(CancellationToken.None);
        await SqlPartialIndexTests.Execute(writer, "CREATE TABLE t(id INT,k INT)");
        await SqlPartialIndexTests.Execute(writer, "CREATE INDEX discarded ON t(id)");
        await SqlPartialIndexTests.Execute(writer, "CREATE UNIQUE INDEX earlier ON t(id)");
        await SqlPartialIndexTests.Execute(writer, "INSERT INTO t VALUES(1,1)");
        await SqlPartialIndexTests.Execute(writer, "DROP INDEX discarded ON t");
        await SqlPartialIndexTests.Execute(writer, "CREATE UNIQUE INDEX later ON t(k) WHERE 10/k>0");
        if (rewrite) { await SqlPartialIndexTests.Execute(writer, "ALTER TABLE t ALTER COLUMN id TYPE BIGINT"); }
        var next = SqlDatabaseEngine.CreateBuilder("partial-index-order");
        next.Options.StorageStrategy = storage.CaptureDurableImages();
        await using var reopened = await next.BuildAsync(CancellationToken.None);
        var database = reopen ? await reopened.OpenDatabaseAsync("db", CancellationToken.None) : first;
        await using var session = await database.CreateSessionAsync(CancellationToken.None);

        // Act
        var result = await session.ExecuteAsync("INSERT INTO t VALUES(1,0) ON CONFLICT DO NOTHING", cancellationToken: CancellationToken.None);

        // Assert
        result.AffectedCount.ShouldBe(0);
        (await SqlPartialIndexTests.Rows(session, "SELECT * FROM t")).ShouldBe([[1, 1]]);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Partial predicates: dropping the unresolved index clears the validated generation's write tax (#1630)")]
    public async Task ExecuteAsync_DroppedUnresolvedIndex_ShouldClearValidationState()
    {
        // Arrange
        var storage = new CrashCaptureSqlStorageStrategy();
        var builder = SqlDatabaseEngine.CreateBuilder("partial-drop-state");
        builder.Options.StorageStrategy = storage;
        builder.Functions.Add(SqlScalarFunction.Create("positive", static (int value) => value > 0, SqlFunctionVolatility.Immutable));
        await using var original = await builder.BuildAsync(CancellationToken.None);
        var first = await original.CreateDatabaseAsync("db", CancellationToken.None);
        await using var writer = await first.CreateSessionAsync(CancellationToken.None);
        await SqlPartialIndexTests.Execute(writer, "CREATE TABLE t(k INT)");
        await SqlPartialIndexTests.Execute(writer, "CREATE INDEX ix ON t(k) WHERE positive(k)");
        var next = SqlDatabaseEngine.CreateBuilder("partial-drop-state");
        next.Options.StorageStrategy = storage.CaptureDurableImages();
        await using var reopened = await next.BuildAsync(CancellationToken.None);
        var database = await reopened.OpenDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SqlPartialIndexTests.Refuses(session, "INSERT INTO t VALUES(1)", "CS01009");
        await SqlPartialIndexTests.Execute(session, "DROP INDEX ix ON t");
        database.Definitions.FindUnresolved(CancellationToken.None).ShouldBeNull();
        await SqlPartialIndexTests.Execute(session, "CREATE INDEX valid ON t(k) WHERE k>0");
        database.Catalog.TryGetTable("dbo", "t", out var table).ShouldBeTrue();
        int before = database.Definitions.ValidationWalkCount;

        // Act
        database.Definitions.EnsureIndexPredicatesResolved(table, CancellationToken.None);
        await SqlPartialIndexTests.Execute(session, "INSERT INTO t VALUES(1)");

        // Assert
        database.Definitions.ValidationWalkCount.ShouldBe(before);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Partial predicates: damaged persisted dependencies fail open instead of becoming unresolved (#1630)")]
    [InlineData("columns", "CS06059")]
    [InlineData("functions", "CS06059")]
    [InlineData("missing-column", "CS06059")]
    [InlineData("syntax", "CS06058")]
    public async Task OpenDatabaseAsync_DamagedPredicate_ShouldRefuseAsCatalogDamage(string damage, string code)
    {
        // Arrange: replace catalog metadata only, retaining the real published tree.
        var storage = new CrashCaptureSqlStorageStrategy();
        var builder = SqlDatabaseEngine.CreateBuilder("partial-damaged");
        builder.Options.StorageStrategy = storage;
        await using var original = await builder.BuildAsync(CancellationToken.None);
        var first = await original.CreateDatabaseAsync("db", CancellationToken.None);
        await using var writer = await first.CreateSessionAsync(CancellationToken.None);
        await SqlPartialIndexTests.Execute(writer, "CREATE TABLE t(k INT)");
        await SqlPartialIndexTests.Execute(writer, "CREATE INDEX ix ON t(k) WHERE ABS(k)>0");
        var table = first.Catalog.Tables.Single();
        var index = first.Catalog.GetIndexes(table.ObjectId).Single();
        var registrations = first.Catalog.GetIndexRegistrations();
        await first.Catalog.DropIndexAsync(table.ObjectId, "ix", [], CancellationToken.None);
        await first.Catalog.CreateIndexAsync(new SqlCatalogIndex(table.ObjectId, "ix", ["k"], false,
            predicate: damage == "syntax" ? "k >" : damage == "missing-column" ? "missing > 0" : index.Predicate,
            predicateColumns: damage == "columns" ? [] : ["k"],
            predicateFunctionCalls: damage is "functions" or "missing-column" or "syntax" ? [] : index.PredicateFunctionCalls),
            registrations, CancellationToken.None);
        var next = SqlDatabaseEngine.CreateBuilder("partial-damaged");
        next.Options.StorageStrategy = storage.CaptureDurableImages();
        await using var reopened = await next.BuildAsync(CancellationToken.None);

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await reopened.OpenDatabaseAsync("db", CancellationToken.None));

        // Assert
        failure.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == code);
    }
}
