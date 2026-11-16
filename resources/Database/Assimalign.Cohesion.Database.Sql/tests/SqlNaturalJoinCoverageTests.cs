using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed class SqlNaturalJoinCoverageTests
{
    public static IEnumerable<object[]> LeftInputs()
    {
        foreach (string kind in new[] { "INNER", "LEFT", "RIGHT", "FULL" })
        {
            foreach (string source in new[] { "stored", "derived", "view", "cte" }) { yield return [kind, source]; }
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - NATURAL left input variants assert merged values, including RIGHT and FULL (#1792)")]
    [MemberData(nameof(LeftInputs))]
    public async Task Execute_NaturalLeftVariants_ShouldPreserveMergedValues(string kind, string source)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("natural-left", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecAsync(session, "CREATE TABLE a(id INT NOT NULL,k INT NOT NULL,l INT); CREATE TABLE b(k INT NOT NULL,id BIGINT NOT NULL,r INT); INSERT INTO a VALUES(1,10,100),(3,30,300); INSERT INTO b VALUES(10,1,1000),(20,2,2000); CREATE VIEW v AS SELECT * FROM a");
        string prefix = source == "cte" ? "WITH c AS (SELECT a.* FROM a) " : "";
        string left = source switch { "derived" => "(SELECT a.* FROM a) d", "view" => "v", "cte" => "c", _ => "a" };
        // Act
        var rows = await RowsAsync(session, prefix + "SELECT id,k,l,r FROM " + left + " NATURAL " + kind + " JOIN b ORDER BY id");
        // Assert: RIGHT takes the right key; FULL coalesces both; no physical key can substitute.
        object?[][] expected = kind switch
        {
            "LEFT" => [[1L, 10, 100, 1000], [3L, 30, 300, null]],
            "RIGHT" => [[1L, 10, 100, 1000], [2L, 20, null, 2000]],
            "FULL" => [[1L, 10, 100, 1000], [2L, 20, null, 2000], [3L, 30, 300, null]],
            _ => [[1L, 10, 100, 1000]],
        };
        Check(rows, expected);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - NATURAL virtual left input keeps merged values under every join kind (#1792)")]
    [InlineData("INNER", 1)] [InlineData("LEFT", 1)] [InlineData("RIGHT", 2)] [InlineData("FULL", 2)]
    public async Task Execute_SystemLeftInput_ShouldMergeNames(string kind, int count)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("natural-system-left", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecAsync(session, "CREATE TABLE b(schema_name TEXT); INSERT INTO b VALUES('dbo'),('ghost')");
        // Act
        var rows = await RowsAsync(session, "SELECT schema_name FROM INFORMATION_SCHEMA.SCHEMATA a NATURAL " + kind + " JOIN b ORDER BY schema_name");
        // Assert
        rows.Count.ShouldBe(count);
        rows[0].ShouldBe(new object?[] { "dbo" });
        if (count == 2) { rows[1].ShouldBe(new object?[] { "ghost" }); }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - NATURAL duplicate names through CROSS join refuse on either side (#1792)")]
    [InlineData("a CROSS JOIN b NATURAL JOIN c")]
    [InlineData("c NATURAL JOIN (a CROSS JOIN b)")]
    public async Task Execute_DuplicateThroughJoin_ShouldRefuseAmbiguousUsing(string from)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("natural-cross-duplicate", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecAsync(session, "CREATE TABLE a(id INT); CREATE TABLE b(id INT); CREATE TABLE c(id INT)");
        // Act / Assert
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("SELECT * FROM " + from, cancellationToken: CancellationToken.None));
        failure.Diagnostics.ShouldContain(d => d.Code == "CS01015");
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - NATURAL quoted case follows the dialect and retains duplicate-name checks (#1792)")]
    public async Task Execute_QuotedNaturalNames_ShouldUseCaseInsensitiveRule()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("natural-case", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecAsync(session, "CREATE TABLE a(\"Id\" INT); CREATE TABLE b(id INT); INSERT INTO a VALUES(1),(2); INSERT INTO b VALUES(2)");
        // Act / Assert: this case-insensitive match is a documented departure from PostgreSQL.
        Check(await RowsAsync(session, "SELECT * FROM a NATURAL JOIN b"), [[2]]);
        foreach (string from in new[] { "(SELECT id AS \"x\",id AS \"X\" FROM a) d NATURAL JOIN (SELECT id AS x FROM b) e", "(SELECT id AS x FROM b) e NATURAL JOIN (SELECT id AS \"x\",id AS \"X\" FROM a) d" })
        {
            var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("SELECT * FROM " + from, cancellationToken: CancellationToken.None));
            failure.Diagnostics.ShouldContain(d => d.Code == "CS01015");
        }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - NATURAL and qualified stars freeze inside CTE, compound and subquery view bodies (#1792)")]
    public async Task Open_NestedNaturalViews_ShouldKeepFrozenBodies()
    {
        // Arrange
        string root = Path.Combine(AppContext.BaseDirectory, "natural-nested-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using (var engine = SqlDatabaseEngine.Create("natural-nested", new() { RootPath = root }))
            {
                var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
                await using var session = await database.CreateSessionAsync(CancellationToken.None);
                await ExecAsync(session, "CREATE TABLE a(id INT,v INT); CREATE TABLE b(id INT,v INT); INSERT INTO a VALUES(1,10),(2,20); INSERT INTO b VALUES(1,10);" +
                    "CREATE VIEW vc AS WITH c AS (SELECT a.* FROM a NATURAL JOIN b) SELECT c.* FROM c;" +
                    "CREATE VIEW vu AS SELECT a.* FROM a NATURAL JOIN b UNION ALL SELECT b.* FROM a NATURAL JOIN b;" +
                    "CREATE VIEW vs AS SELECT (SELECT COUNT(*) FROM (SELECT a.* FROM a NATURAL JOIN b) d) AS n;");
                foreach (var view in database.Catalog.Views)
                {
                    view.Definition.ShouldNotContain("NATURAL", Case.Sensitive);
                    view.Definition.ShouldNotContain(".*", Case.Sensitive);
                }
                await ExecAsync(session, "ALTER TABLE a ADD COLUMN extra INT DEFAULT 1; ALTER TABLE b ADD COLUMN extra INT DEFAULT 2");
            }
            // Act
            await using var reopened = SqlDatabaseEngine.Create("natural-nested", new() { RootPath = root });
            var restored = await reopened.OpenDatabaseAsync("db", CancellationToken.None);
            await using var reader = await restored.CreateSessionAsync(CancellationToken.None);
            // Assert
            Check(await RowsAsync(reader, "SELECT * FROM vc"), [[1, 10]]);
            Check(await RowsAsync(reader, "SELECT * FROM vu"), [[1, 10], [1, 10]]);
            Check(await RowsAsync(reader, "SELECT * FROM vs"), [[1L]]);
        }
        finally { if (Directory.Exists(root)) { Directory.Delete(root, true); } }
    }

    private static async Task ExecAsync(SqlDatabaseSession session, string sql)
    {
        foreach (string part in sql.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        { (await session.ExecuteAsync(part, cancellationToken: CancellationToken.None)).Status.ShouldBe(DatabaseResultStatus.Success); }
    }
    private static async Task<List<object?[]>> RowsAsync(SqlDatabaseSession session, string sql)
    {
        await using var result = (await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();
        var rows = new List<object?[]>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None)) { rows.Add(Enumerable.Range(0, row.FieldCount).Select(row.GetValue).ToArray()); }
        return rows;
    }
    private static void Check(List<object?[]> actual, object?[][] expected)
    {
        actual.Count.ShouldBe(expected.Length);
        for (int i = 0; i < expected.Length; i++) { actual[i].ShouldBe(expected[i]); }
    }
}
