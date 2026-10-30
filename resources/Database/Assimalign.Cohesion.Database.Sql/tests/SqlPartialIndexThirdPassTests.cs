using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;

namespace Assimalign.Cohesion.Database.Sql.Tests;

[Collection(SqlTimingCollection.Name)]
public sealed class SqlPartialIndexThirdPassTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - Partial predicates: JSON representations check the converter's runtime slots (#1630)")]
    [InlineData("JSONB", "CASE WHEN k>0 THEN 0 ELSE doc END")]
    [InlineData("JSONB", "COALESCE(k,doc)")]
    [InlineData("JSON", "CASE WHEN k>0 THEN 0 ELSE doc END")]
    [InlineData("JSON", "COALESCE(k,doc)")]
    [InlineData("native_doc", "CASE WHEN k>0 THEN 0 ELSE doc END")]
    [InlineData("native_doc", "COALESCE(k,doc)")]
    public async Task ExecuteAsync_JsonAlternativeCast_ShouldRefuseBeforeCallback(string type, string operand)
    {
        // Arrange
        int calls = 0;
        bool json = type == "JSON";
        var builder = SqlDatabaseEngine.CreateBuilder("partial-json-slots");
        builder.Types.Add(SqlType.CreateDomain("native_doc", SqlType.Jsonb));
        var target = SqlType.CreateDomain("native_int", SqlType.Integer);
        builder.Types.Add(target);
        if (json)
        { builder.Types.AddCast(SqlCast.Create<string, int>(SqlType.Text, target, _ => { calls++; return 1; }, SqlCastContext.Explicit)); }
        else
        { builder.Types.AddCast(SqlCast.Create<byte[], int>(SqlType.Binary, SqlType.Integer, _ => { calls++; return 1; }, SqlCastContext.Explicit)); }
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SqlPartialIndexTests.Execute(session, $"CREATE TABLE t(k INT,doc {type})");
        string predicate = $"CAST({operand} AS {(json ? "native_int" : "INT")})>0";

        // Act: if admission regresses, exercise the callback rather than just observing creation.
        DatabaseException? refusal = null;
        try { await SqlPartialIndexTests.Execute(session, "CREATE INDEX ix ON t(k) WHERE " + predicate); }
        catch (DatabaseException failure) { refusal = failure; }
        if (refusal is null)
        {
            string key = operand.StartsWith("COALESCE", StringComparison.Ordinal) ? "NULL" : "-1";
            await SqlPartialIndexTests.Execute(session, $"INSERT INTO t VALUES({key},CAST('{{}}' AS {(json ? "JSON" : "JSONB")}))");
        }

        // Assert
        calls.ShouldBe(0);
        refusal.ShouldNotBeNull().Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "CS05091");
        database.IndexManager.ExportRegistrations().ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Partial predicates: weakened native domain parameters and results reopen unresolved (#1630)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OpenDatabaseAsync_WeakenedFunctionDomain_ShouldReadAndRefuseWrites(bool resultDomain)
    {
        // Arrange
        var storage = new CrashCaptureSqlStorageStrategy();
        var builder = SqlDatabaseEngine.CreateBuilder("partial-function-domain");
        builder.Options.StorageStrategy = storage;
        builder.Types.Add(SqlType.CreateDomain("native_int", SqlType.Integer,
            SqlScalarFunction.Create("native_check", static (int value) => value >= 0, SqlFunctionVolatility.Immutable)));
        await using var original = await builder.BuildAsync(CancellationToken.None);
        var first = await original.CreateDatabaseAsync("db", CancellationToken.None);
        await using var writer = await first.CreateSessionAsync(CancellationToken.None);
        await SqlPartialIndexTests.Execute(writer, "CREATE TABLE t(k INT)");
        await SqlPartialIndexTests.Execute(writer, "INSERT INTO t VALUES(1)");
        await SqlPartialIndexTests.Execute(writer, resultDomain
            ? "CREATE FUNCTION nonneg(x INT) RETURNS native_int IMMUTABLE RETURN x"
            : "CREATE FUNCTION nonneg(x native_int) RETURNS BOOLEAN IMMUTABLE RETURN x>=0");
        await SqlPartialIndexTests.Execute(writer, "CREATE INDEX ix ON t(k) WHERE " + (resultDomain ? "nonneg(k)>0" : "nonneg(k)"));
        int callbacks = 0;
        var next = SqlDatabaseEngine.CreateBuilder("partial-function-domain");
        next.Options.StorageStrategy = storage.CaptureDurableImages();
        next.Types.Add(SqlPartialIndexRegistrations.ChangedDomain(
            SqlScalarFunction.Create("native_check", (int value) => { callbacks++; return value >= 0; })));
        await using var reopened = await next.BuildAsync(CancellationToken.None);

        // Act
        var database = await reopened.OpenDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);

        // Assert
        (await SqlPartialIndexTests.Rows(session, "SELECT * FROM t")).ShouldBe([[1]]);
        (await SqlPartialIndexTests.Rows(session, "SELECT PREDICATE FROM COHESION_SCHEMA.INDEXES WHERE INDEX_NAME='ix'")).Length.ShouldBe(1);
        foreach (string sql in new[] { "INSERT INTO t VALUES(2)", "UPDATE t SET k=2 WHERE FALSE", "DELETE FROM t WHERE FALSE" })
        { await SqlPartialIndexTests.Refuses(session, sql, "CS01009"); }
        callbacks.ShouldBe(0);
        await SqlPartialIndexTests.Execute(session, "DROP INDEX ix ON t");
        await SqlPartialIndexTests.Execute(session, "INSERT INTO t VALUES(2)");
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Partial predicates: function and domain DDL preserve early unresolved write refusal (#1630)")]
    [InlineData("CREATE FUNCTION other(x INT) RETURNS INT IMMUTABLE RETURN x")]
    [InlineData("CREATE DOMAIN other AS INT CHECK(VALUE>=0)")]
    public async Task ExecuteAsync_DefinitionRefresh_ShouldPreserveUnresolvedFlag(string ddl)
    {
        // Arrange
        var storage = new CrashCaptureSqlStorageStrategy();
        var builder = SqlDatabaseEngine.CreateBuilder("partial-refresh");
        builder.Options.StorageStrategy = storage;
        builder.Functions.Add(SqlScalarFunction.Create("positive", static (int value) => value > 0, SqlFunctionVolatility.Immutable));
        await using var original = await builder.BuildAsync(CancellationToken.None);
        var first = await original.CreateDatabaseAsync("db", CancellationToken.None);
        await using var writer = await first.CreateSessionAsync(CancellationToken.None);
        await SqlPartialIndexTests.Execute(writer, "CREATE TABLE t(k INT)");
        await SqlPartialIndexTests.Execute(writer, "CREATE INDEX ix ON t(k) WHERE positive(k)");
        await SqlPartialIndexTests.Execute(writer, "CREATE TABLE plain(k INT)");
        var next = SqlDatabaseEngine.CreateBuilder("partial-refresh");
        next.Options.StorageStrategy = storage.CaptureDurableImages();
        await using var reopened = await next.BuildAsync(CancellationToken.None);
        var database = await reopened.OpenDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);

        // Act
        await SqlPartialIndexTests.Execute(session, ddl);
        int before = database.Definitions.ValidationWalkCount;
        await SqlPartialIndexTests.Execute(session, "INSERT INTO plain VALUES(1)");
        await SqlPartialIndexTests.Execute(session, "UPDATE plain SET k=2 WHERE FALSE");
        await SqlPartialIndexTests.Execute(session, "DELETE FROM plain WHERE FALSE");

        // Assert
        database.Definitions.ValidationWalkCount.ShouldBe(before);
        await SqlPartialIndexTests.Refuses(session, "DELETE FROM t WHERE FALSE", "CS01009");
        database.Definitions.ValidationWalkCount.ShouldBe(before + 1);
        await SqlPartialIndexTests.Refuses(session, "UPDATE t SET k=2 WHERE FALSE", "CS01009");
        database.Definitions.ValidationWalkCount.ShouldBe(before + 1);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Partial predicates: creation refusals use complete cast and domain sentences (#1630)")]
    [InlineData(false, "An index predicate cannot use the application-registered cast from BOOLEAN to INTEGER because it has no immutable contract.")]
    [InlineData(true, "An index predicate cannot use domain native_int because its check is not immutable.")]
    public async Task ExecuteAsync_UnprovenConversion_ShouldReportCompleteSentence(bool domain, string message)
    {
        // Arrange
        var builder = SqlDatabaseEngine.CreateBuilder("partial-messages");
        builder.Types.Add(SqlPartialIndexRegistrations.ChangedDomain(
            SqlScalarFunction.Create("native_check", static (int value) => value >= 0)));
        builder.Types.AddCast(SqlCast.Create<bool, int>(SqlType.Boolean, SqlType.Integer, static value => value ? 1 : 0, SqlCastContext.Explicit));
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SqlPartialIndexTests.Execute(session, "CREATE TABLE t(k INT,active BOOLEAN)");

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(() => SqlPartialIndexTests.Execute(session,
            "CREATE INDEX ix ON t(k) WHERE " + (domain ? "CAST(k AS native_int)>0" : "CAST(active AS INT)>0")));

        // Assert
        failure.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "CS05091");
        failure.Message.ShouldBe(message);
        failure.Diagnostics.Single(diagnostic => diagnostic.Code == "CS05091").Message.ShouldBe(message);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Conformance: bulk DO NOTHING retains failure markers without retaining predicate exceptions (#1630)")]
    public async Task ExecuteAsync_BulkPredicateFailures_ShouldRetainNoExceptions()
    {
        // Arrange: CHECK observes preparation before any arbiter consumes its markers.
        const int rows = 128;
        var failures = new List<WeakReference>();
        int retained = -1, observations = 0;
        bool observe = false;
        var builder = SqlDatabaseEngine.CreateBuilder("partial-failure-retention");
        builder.Functions.Add(SqlScalarFunction.Create<int, bool>("fails", (int value) =>
        {
            var failure = new DatabaseException("APP001: predicate failure");
            failures.Add(new WeakReference(failure));
            throw failure;
        }, SqlFunctionVolatility.Immutable));
        builder.Functions.Add(SqlScalarFunction.Create("observe", (int value) =>
        {
            if (observe && observations++ == 0)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                retained = failures.Count(reference => reference.IsAlive);
            }
            return value > 0;
        }, SqlFunctionVolatility.Immutable));
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SqlPartialIndexTests.Execute(session, "CREATE TABLE t(id INT PRIMARY KEY,k INT,CHECK(observe(id)))");
        await SqlPartialIndexTests.Execute(session, "CREATE UNIQUE INDEX ix ON t(k) WHERE fails(k)");
        await SqlPartialIndexTests.Execute(session, "INSERT INTO t VALUES(1,NULL)");
        observe = true;

        // Act
        var result = await session.ExecuteAsync("INSERT INTO t VALUES " +
            string.Join(',', Enumerable.Range(1, rows).Select(value => $"(1,{value})")) + " ON CONFLICT DO NOTHING", cancellationToken: CancellationToken.None);

        // Assert
        result.AffectedCount.ShouldBe(0);
        failures.Count.ShouldBe(rows);
        observations.ShouldBe(rows);
        retained.ShouldBe(0);
    }
}
