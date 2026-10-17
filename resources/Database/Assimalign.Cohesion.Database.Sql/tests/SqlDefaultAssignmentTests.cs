using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Language;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed class SqlDefaultAssignmentTests
{
    [Theory(DisplayName = "Cohesion Test [SqlEngine] - DEFAULT: explicit items and DEFAULT VALUES use column defaults")]
    [InlineData("INSERT INTO defaults VALUES (DEFAULT, DEFAULT, DEFAULT)")]
    [InlineData("INSERT INTO defaults DEFAULT VALUES")]
    [InlineData("INSERT INTO defaults (label) DEFAULT VALUES")]
    public async Task ExecuteAsync_DefaultInsert_ShouldUseColumnDefaults(string sql)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("default-tests", new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("defaults", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE defaults (id INT DEFAULT 7 NOT NULL, label TEXT DEFAULT 'seed', note INT)", cancellationToken: CancellationToken.None);

        // Act
        var result = await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None);

        // Assert
        result.AffectedCount.ShouldBe(1);
        (await RowsAsync(session)).ShouldBe([[7, "seed", null]]);
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - DEFAULT: reordered columns, omitted columns, explicit NULL and UPDATE work on both request seams")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_MixedDefaultAssignments_ShouldUseTargetColumns(bool typed)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("default-tests", new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("defaults", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE defaults (id INT DEFAULT 7 NOT NULL, label TEXT DEFAULT 'seed', note INT DEFAULT 9)", cancellationToken: CancellationToken.None);

        // Act
        var inserted = await ExecuteAsync(session, "INSERT INTO defaults (label, id, note) VALUES (DEFAULT, 1, DEFAULT), (NULL, 2, NULL), (NULL, 4, NULL)", typed);
        var omitted = await ExecuteAsync(session, "INSERT INTO defaults (id) VALUES (3)", typed);
        var updated = await ExecuteAsync(session, "UPDATE defaults SET label = DEFAULT, note = DEFAULT, id = id + 10 WHERE id = 2", typed);
        var allDefaults = await ExecuteAsync(session, "INSERT INTO defaults (note, id) DEFAULT VALUES", typed);

        // Assert
        inserted.AffectedCount.ShouldBe(3);
        omitted.AffectedCount.ShouldBe(1);
        updated.AffectedCount.ShouldBe(1);
        allDefaults.AffectedCount.ShouldBe(1);
        (await RowsAsync(session)).ShouldBe([[1, "seed", 9], [3, "seed", 9], [4, null, null], [7, "seed", 9], [12, "seed", 9]]);
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - DEFAULT: missing nullable defaults become NULL for INSERT and UPDATE")]
    [InlineData("INSERT INTO defaults (id, label, note) VALUES (1, DEFAULT, DEFAULT)")]
    [InlineData("UPDATE defaults SET label = DEFAULT, note = DEFAULT WHERE id = 1")]
    public async Task ExecuteAsync_MissingNullableDefault_ShouldAssignNull(string sql)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("default-tests", new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("defaults", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE defaults (id INT, label TEXT, note INT)", cancellationToken: CancellationToken.None);
        if (sql.StartsWith("UPDATE", System.StringComparison.Ordinal))
        {
            await session.ExecuteAsync("INSERT INTO defaults VALUES (1, 'old', 8)", cancellationToken: CancellationToken.None);
        }

        // Act
        var result = await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None);

        // Assert
        result.AffectedCount.ShouldBe(1);
        (await RowsAsync(session)).ShouldBe([[1, null, null]]);
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - DEFAULT: a NOT NULL failure preserves earlier transaction work and the whole failed statement")]
    [InlineData("INSERT INTO defaults VALUES (2, 'ok', 2), (3, DEFAULT, 3)")]
    [InlineData("INSERT INTO defaults DEFAULT VALUES")]
    [InlineData("UPDATE defaults SET label = DEFAULT")]
    [InlineData("INSERT INTO defaults VALUES (2, NULL, 2)")]
    public async Task ExecuteAsync_NotNullDefaultFailure_ShouldKeepStatementAtomic(string sql)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("default-tests", new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("defaults", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE defaults (id INT DEFAULT 7, label TEXT NOT NULL, note INT)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("BEGIN", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO defaults VALUES (1, 'old', 1)", cancellationToken: CancellationToken.None);

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(() => session.ExecuteAsync(sql, cancellationToken: CancellationToken.None).AsTask());

        // Assert
        failure.Message.ShouldContain("NULL", Case.Sensitive);
        session.CurrentTransaction.ShouldNotBeNull();
        await session.ExecuteAsync("COMMIT", cancellationToken: CancellationToken.None);
        (await RowsAsync(session)).ShouldBe([[1, "old", 1]]);
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - DEFAULT: DEFAULT VALUES still validates listed target columns")]
    [InlineData("INSERT INTO defaults (id, id) DEFAULT VALUES")]
    [InlineData("INSERT INTO defaults (missing) DEFAULT VALUES")]
    public async Task ExecuteAsync_InvalidDefaultValuesTargets_ShouldFailBeforeWriting(string sql)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("default-tests", new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("defaults", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE defaults (id INT, label TEXT, note INT)", cancellationToken: CancellationToken.None);

        // Act
        await Should.ThrowAsync<DatabaseException>(() => session.ExecuteAsync(sql, cancellationToken: CancellationToken.None).AsTask());

        // Assert
        (await RowsAsync(session)).ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [SqlEngine] - DEFAULT: existing catalog function defaults evaluate for each assigned row")]
    public async Task ExecuteAsync_StoredFunctionDefault_ShouldEvaluatePerRow()
    {
        // Arrange
        int calls = 0;
        var builder = SqlDatabaseEngine.CreateBuilder("default-tests");
        builder.Functions.Add(SqlScalarFunction.Create("next_default", (long value) => (long)Interlocked.Increment(ref calls)));
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var database = await engine.CreateDatabaseAsync("defaults", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE FUNCTION seed_label() RETURNS TEXT RETURN 'stored'", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE defaults (id INT DEFAULT 7, label TEXT DEFAULT seed_label(), note BIGINT DEFAULT next_default(0))", cancellationToken: CancellationToken.None);

        // Act
        await session.ExecuteAsync("INSERT INTO defaults VALUES (1, DEFAULT, DEFAULT), (2, DEFAULT, DEFAULT)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO defaults DEFAULT VALUES", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("UPDATE defaults SET label = DEFAULT, note = DEFAULT", cancellationToken: CancellationToken.None);

        // Assert
        calls.ShouldBe(6);
        (await RowsAsync(session)).Select(row => row[1]).ShouldBe(["stored", "stored", "stored"]);
        (await RowsAsync(session)).Select(row => (long)row[2]!).Order().ShouldBe([4L, 5L, 6L]);
    }

    [Theory(DisplayName = "Cohesion Test [SqlEngine] - DEFAULT: CHECK, UNIQUE and foreign keys validate resolved defaults atomically")]
    [InlineData("CREATE TABLE defaults (id INT DEFAULT 0 CHECK (id > 0), label TEXT, note INT)", "INSERT INTO defaults VALUES (1, NULL, NULL), (DEFAULT, NULL, NULL)")]
    [InlineData("CREATE TABLE defaults (id INT DEFAULT 1 UNIQUE, label TEXT, note INT)", "INSERT INTO defaults VALUES (DEFAULT, NULL, NULL), (DEFAULT, NULL, NULL)")]
    [InlineData("CREATE TABLE defaults (id INT DEFAULT 9 REFERENCES parent(id), label TEXT, note INT)", "INSERT INTO defaults DEFAULT VALUES")]
    public async Task ExecuteAsync_DefaultConstraintFailure_ShouldWriteNothing(string ddl, string sql)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("default-tests", new SqlDatabaseEngineOptions());
        var database = await engine.CreateDatabaseAsync("defaults", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE parent (id INT PRIMARY KEY)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync(ddl, cancellationToken: CancellationToken.None);

        // Act
        await Should.ThrowAsync<DatabaseException>(() => session.ExecuteAsync(sql, cancellationToken: CancellationToken.None).AsTask());

        // Assert
        (await RowsAsync(session)).ShouldBeEmpty();
    }

    private static ValueTask<DatabaseResult> ExecuteAsync(SqlDatabaseSession session, string sql, bool typed)
        => typed
            ? session.ExecuteAsync(new SqlQueryRequest((SqlQueryStatement)new SqlQueryParser().Parse(sql)), CancellationToken.None)
            : session.ExecuteAsync(sql, cancellationToken: CancellationToken.None);

    private static async Task<List<object?[]>> RowsAsync(SqlDatabaseSession session)
    {
        await using var result = (await session.ExecuteAsync("SELECT id, label, note FROM defaults ORDER BY id", cancellationToken: CancellationToken.None))
            .ShouldBeAssignableTo<DatabaseResultSet>();
        var rows = new List<object?[]>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None))
        {
            rows.Add(Enumerable.Range(0, row.FieldCount).Select(row.GetValue).ToArray());
        }
        return rows;
    }
}
