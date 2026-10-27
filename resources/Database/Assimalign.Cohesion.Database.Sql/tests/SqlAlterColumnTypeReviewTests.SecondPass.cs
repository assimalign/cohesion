using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlAlterColumnTypeReviewTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - ALTER TYPE second pass: stable and volatile defaults are not evaluated by DDL")]
    [InlineData(SqlFunctionVolatility.Stable)]
    [InlineData(SqlFunctionVolatility.Volatile)]
    public async Task ExecuteAsync_DeferredTemporalDefault_ShouldReadInsertValue(SqlFunctionVolatility volatility)
    {
        // Arrange
        int calls = 0;
        var day = new DateOnly(2001, 1, 1);
        var builder = SqlDatabaseEngine.CreateBuilder("alter-second-deferred");
        builder.Functions.Add(SqlScalarFunction.Create("next_day", (int _) => { calls++; return day; }, volatility));
        await using var engine = builder.Build();
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecAsync(session, "CREATE TABLE t(v DATE DEFAULT next_day(1))");

        // Act
        await ExecAsync(session, "ALTER TABLE t ALTER v TYPE TIMESTAMP");
        calls.ShouldBe(0);
        day = new DateOnly(2002, 2, 2);
        await ExecAsync(session, "INSERT INTO t DEFAULT VALUES");

        // Assert
        calls.ShouldBe(1);
        (await RowsAsync(session, "SELECT v FROM t")).ShouldHaveSingleItem()[0].ShouldBe(day.ToDateTime(TimeOnly.MinValue));
        Table(database).Columns[0].DefaultFunctionCalls.ShouldHaveSingleItem();
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - ALTER TYPE second pass: temporal defaults cast at INSERT and after reopen")]
    [InlineData("DATE", "CURRENT_DATE", "TIMESTAMP", "LOCALTIMESTAMP", false)]
    [InlineData("TIMESTAMP", "LOCALTIMESTAMP", "TIMESTAMPTZ", "CURRENT_TIMESTAMP", true)]
    public async Task Reopen_TemporalDefault_ShouldEvaluateAtInsert(string source, string expression, string target, string clock, bool instant)
    {
        // Arrange
        var storage = new CrashCaptureSqlStorageStrategy();
        await using (var engine = SqlDatabaseEngine.Create("alter-second-clock", new() { StorageStrategy = storage }))
        {
            var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
            await using var session = await database.CreateSessionAsync(CancellationToken.None);
            await ExecAsync(session, $"CREATE TABLE t(v {source} DEFAULT {expression})");

            // Act
            await ExecAsync(session, $"ALTER TABLE t ALTER v TYPE {target}");

            // Assert
            Table(database).Columns[0].DefaultLiteral.ShouldBe(expression);
            await AssertClockDefaultAsync(session, clock, instant);
        }
        await using var reopened = SqlDatabaseEngine.Create("alter-second-clock", new() { StorageStrategy = storage.CaptureDurableImages() });
        var restored = await reopened.OpenDatabaseAsync("db", CancellationToken.None);
        await using var reader = await restored.CreateSessionAsync(CancellationToken.None);
        await ExecAsync(reader, "DELETE FROM t");
        await AssertClockDefaultAsync(reader, clock, instant);
    }

    private static async Task AssertClockDefaultAsync(SqlDatabaseSession session, string clock, bool instant)
    {
        await using var transaction = await session.BeginTransactionAsync(CancellationToken.None);
        await ExecAsync(session, "INSERT INTO t DEFAULT VALUES");
        var row = (await RowsAsync(session, $"SELECT v, {clock} FROM t")).ShouldHaveSingleItem();
        if (instant) { row[0].ShouldBeOfType<DateTimeOffset>().ShouldBe(row[1]); }
        else { row[0].ShouldBeOfType<DateTime>().ShouldBe(((DateTime)row[1]!).Date); }
        await transaction.CommitAsync(CancellationToken.None);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - ALTER TYPE second pass: narrowing trims trailing spaces through a rewrite")]
    [InlineData("TEXT", "'ab    '", "VARCHAR(3)", "ab ")]
    [InlineData("VARCHAR(8)", "'ab    '", "VARCHAR(3)", "ab ")]
    public async Task ExecuteAsync_NarrowingCharacter_ShouldRewriteWithAssignment(string source, string value, string target, string expected)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("alter-second-trim", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecAsync(session, $"CREATE TABLE t(v {source} UNIQUE)");
        await ExecAsync(session, $"INSERT INTO t VALUES({value})");
        var previous = Table(database);

        // Act
        await ExecAsync(session, $"ALTER TABLE t ALTER v TYPE {target}");

        // Assert
        Table(database).ObjectId.ShouldNotBe(previous.ObjectId);
        (await RowsAsync(session, "SELECT v FROM t")).ShouldHaveSingleItem()[0].ShouldBe(expected);
        (await RowsAsync(session, "SELECT v FROM t WHERE v='ab '")).ShouldHaveSingleItem();
        session.LastStatementMetrics!.AccessPath.ShouldStartWith("seek:");
        var narrowed = Table(database);
        var indexes = database.IndexManager.ExportRegistrations().ToArray();
        await ExecAsync(session, "ALTER TABLE t ALTER v TYPE VARCHAR(100)");
        Table(database).ObjectId.ShouldBe(narrowed.ObjectId);
        database.IndexManager.ExportRegistrations().ShouldBe(indexes);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - ALTER TYPE second pass: narrowing overflow abandons an unpublished rewrite")]
    public async Task ExecuteAsync_NarrowingOverflow_ShouldPreserveOriginal()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("alter-second-overflow", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecAsync(session, "CREATE TABLE t(v TEXT UNIQUE)");
        await ExecAsync(session, "INSERT INTO t VALUES('ab    '),('abcd')");
        var previous = Table(database);
        int reserved = 0;
        SqlPlanExecutor.RewriteStage = (stage, _) => { if (stage == "Reserved") { reserved++; } };
        try
        {
            // Act
            await Should.ThrowAsync<DatabaseException>(() => ExecAsync(session, "ALTER TABLE t ALTER v TYPE VARCHAR(3)"));

            // Assert
            reserved.ShouldBe(1);
            Table(database).ShouldBeSameAs(previous);
            (await RowsAsync(session, "SELECT v FROM t ORDER BY v")).Select(r => r[0]).ShouldBe(new[] { "ab    ", "abcd" });
        }
        finally { SqlPlanExecutor.RewriteStage = null; }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - ALTER TYPE second pass: CHECK LIKE retains runtime text conversion")]
    [InlineData("qty", "BIGINT")]
    [InlineData("zip", "BIGINT")]
    public async Task ExecuteAsync_NumericLikeCheck_ShouldKeepRuntimeConversion(string column, string target)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("alter-second-like", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecAsync(session, "CREATE TABLE t(zip INT, qty INT, CHECK(zip LIKE '9%' AND qty>0))");
        await ExecAsync(session, "INSERT INTO t VALUES(90210,1)");

        // Act
        await ExecAsync(session, $"ALTER TABLE t ALTER {column} TYPE {target}");

        // Assert
        await ExecAsync(session, "INSERT INTO t VALUES(99999,2)");
        (await RowsAsync(session, "SELECT zip FROM t")).Count.ShouldBe(2);
        await Should.ThrowAsync<DatabaseException>(() => ExecAsync(session, "INSERT INTO t VALUES(12345,1)"));
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - ALTER TYPE second pass: persisted CHECKs keep declaration exceptions")]
    [InlineData("CAST(qty AS BIGINT)>0")]
    [InlineData("legacy.qty>0")]
    public async Task BindPersistedCheck_RetypingExistingDefinition_ShouldKeepLoadRules(string predicate)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("alter-second-persisted", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecAsync(session, "CREATE TABLE t(qty INT)");
        await database.Catalog.AddConstraintAsync("dbo", "t", new SqlCatalogConstraint("ck", SqlCatalogConstraintKind.Check,
            [], checkExpression: predicate), CancellationToken.None);
        await ExecAsync(session, "INSERT INTO t VALUES(1)");

        // Act
        await ExecAsync(session, "ALTER TABLE t ALTER qty TYPE BIGINT");

        // Assert
        (await RowsAsync(session, "SELECT qty FROM t")).ShouldHaveSingleItem()[0].ShouldBe(1L);
        await ExecAsync(session, "INSERT INTO t VALUES(2)");
        await Should.ThrowAsync<DatabaseException>(() => ExecAsync(session, "INSERT INTO t VALUES(0)"));
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - ALTER TYPE second pass: ordinary domain defaults retain existing behavior after reopen")]
    [InlineData("'abcd'")]
    [InlineData("('ab'||'cd')")]
    public async Task Reopen_OrdinaryApplicationDomainDefault_ShouldKeepOriginalEvaluation(string expression)
    {
        // Arrange
        var storage = new CrashCaptureSqlStorageStrategy();
        await using (var engine = CharacterEngine(storage))
        {
            var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
            await using var session = await database.CreateSessionAsync(CancellationToken.None);
            await ExecAsync(session, $"CREATE TABLE t(v target_char DEFAULT {expression})");

            // Act
            await ExecAsync(session, "INSERT INTO t DEFAULT VALUES");
            await ExecAsync(session, "INSERT INTO t VALUES('ab'||'cd')");

            // Assert
            (await RowsAsync(session, "SELECT v FROM t")).ShouldAllBe(row => Equals(row[0], "abcd"));
        }
        await using var reopened = CharacterEngine(storage.CaptureDurableImages());
        var restored = await reopened.OpenDatabaseAsync("db", CancellationToken.None);
        await using var reader = await restored.CreateSessionAsync(CancellationToken.None);
        await ExecAsync(reader, "INSERT INTO t DEFAULT VALUES");
        (await RowsAsync(reader, "SELECT v FROM t")).ShouldAllBe(row => Equals(row[0], "abcd"));
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - ALTER TYPE second pass: application CHAR assignment defaults survive reopen")]
    [InlineData("'abcd'")]
    [InlineData("next_char()")]
    public async Task Reopen_RecastApplicationCharacterDefault_ShouldApplyAssignment(string expression)
    {
        // Arrange
        var storage = new CrashCaptureSqlStorageStrategy();
        await using (var engine = CharacterEngine(storage))
        {
            var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
            await using var session = await database.CreateSessionAsync(CancellationToken.None);
            await ExecAsync(session, "CREATE FUNCTION next_char() RETURNS TEXT VOLATILE RETURN 'abcd'");
            await ExecAsync(session, $"CREATE TABLE t(v TEXT DEFAULT {expression})");

            // Act
            await ExecAsync(session, "ALTER TABLE t ALTER v TYPE target_char");
            await ExecAsync(session, "INSERT INTO t DEFAULT VALUES");

            // Assert
            (await RowsAsync(session, "SELECT v FROM t")).ShouldHaveSingleItem()[0].ShouldBe("ABCD");
            Table(database).Columns[0].DefaultFunctionCalls.Count.ShouldBe(expression == "next_char()" ? 1 : 0);
            await ExecAsync(session, "ALTER TABLE t ADD other INT");
            await ExecAsync(session, "ALTER TABLE t ALTER v SET NOT NULL");
            await ExecAsync(session, "ALTER TABLE t RENAME COLUMN v TO renamed");
            await ExecAsync(session, "ALTER TABLE t ALTER other TYPE BIGINT");
            await ExecAsync(session, "ALTER TABLE t RENAME COLUMN renamed TO v");
        }
        await using var reopened = CharacterEngine(storage.CaptureDurableImages());
        var restored = await reopened.OpenDatabaseAsync("db", CancellationToken.None);
        await using var reader = await restored.CreateSessionAsync(CancellationToken.None);
        await ExecAsync(reader, "INSERT INTO t DEFAULT VALUES");
        (await RowsAsync(reader, "SELECT v FROM t")).ShouldAllBe(row => Equals(row[0], "ABCD"));
        await ExecAsync(reader, "ALTER TABLE t ALTER v SET DEFAULT ('ab'||'cd')");
        await ExecAsync(reader, "INSERT INTO t DEFAULT VALUES");
        (await RowsAsync(reader, "SELECT v FROM t WHERE v='abcd'")).ShouldHaveSingleItem();
    }

    private static SqlDatabaseEngine CharacterEngine(SqlStorageStrategy storage)
    {
        var builder = SqlDatabaseEngine.CreateBuilder("alter-second-char");
        builder.Options.StorageStrategy = storage;
        var domain = SqlType.CreateDomain("target_char", SqlType.Char(4));
        builder.Types.Add(domain).AddCast(SqlCast.Create<string, string>(SqlType.Text, domain,
            static value => value.ToUpperInvariant(), SqlCastContext.Assignment));
        return builder.Build();
    }
}
