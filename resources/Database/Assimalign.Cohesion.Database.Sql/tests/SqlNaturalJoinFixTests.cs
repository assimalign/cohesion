using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed class SqlNaturalJoinFixTests
{
    [Fact(DisplayName = "Cohesion Test [Sql] - Format 37: old same-name view column lists conservatively refuse NATURAL after reopen (#1792)")]
    public async Task Open_OldExplicitViewColumnList_ShouldRequireFreshAlias()
    {
        // Arrange: 19dd61626 stored the body alone, plus the output contract in kind 17.
        // Use that unchanged catalog writer to persist the exact old shape, without passing
        // through today's CREATE VIEW binder, which preserves the authored list in a wrapper.
        const string oldBody = "SELECT CAST(1 AS BIGINT)";
        string root = Path.Combine(AppContext.BaseDirectory, "natural-old-list-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using (var engine = SqlDatabaseEngine.Create("natural-old-list", new() { RootPath = root }))
            {
                var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
                await database.Catalog.CreateViewAsync(new SqlCatalogView("dbo", "legacy", oldBody,
                    [new SqlCatalogColumn("column1", new(DatabaseType.Int64))], [], [], []), cancellationToken: CancellationToken.None);
                await using var session = await database.CreateSessionAsync(CancellationToken.None);
                await ExecAsync(session, "CREATE VIEW fresh(column1) AS SELECT CAST(1 AS BIGINT)");
                database.Catalog.Views.Single(v => v.Name == "fresh").Definition.ShouldContain("(column1)", Case.Sensitive);
            }
            // Act: reopen both actual catalog records before testing provenance.
            await using var reopened = SqlDatabaseEngine.Create("natural-old-list", new() { RootPath = root });
            var restored = await reopened.OpenDatabaseAsync("db", CancellationToken.None);
            await using var reader = await restored.CreateSessionAsync(CancellationToken.None);
            // Assert
            restored.Catalog.Views.Single(v => v.Name == "legacy").Definition.ShouldBe(oldBody);
            Check(await RowsAsync(reader, "SELECT * FROM legacy"), [[1L]]);
            var failure = await Should.ThrowAsync<DatabaseException>(async () =>
            {
                await using var unexpected = (await reader.ExecuteAsync("SELECT * FROM legacy NATURAL JOIN (SELECT CAST(1 AS BIGINT) AS column1) x", cancellationToken: CancellationToken.None)) as DatabaseResultSet;
            });
            failure.Diagnostics.ShouldContain(d => d.Code == "CS01015");
            failure.Message.ShouldContain("alias");
            Check(await RowsAsync(reader, "SELECT * FROM fresh NATURAL JOIN (SELECT CAST(1 AS BIGINT) AS column1) x"), [[1L]]);
            Check(await RowsAsync(reader, "SELECT * FROM (SELECT * FROM legacy) l(column1) NATURAL JOIN (SELECT CAST(1 AS BIGINT) AS column1) x"), [[1L]]);
        }
        finally { if (Directory.Exists(root)) { Directory.Delete(root, true); } }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Zero-name outer views: null extension survives added columns and reopen (#1792)")]
    [InlineData("LEFT", "a NATURAL LEFT JOIN b", "LEFT OUTER JOIN b ON TRUE")]
    [InlineData("RIGHT", "b NATURAL RIGHT JOIN a", "RIGHT OUTER JOIN a ON TRUE")]
    public async Task Open_ZeroNameOuterView_ShouldPreserveRowsAndNullability(string kind, string from, string frozen)
    {
        // Arrange
        string root = Path.Combine(AppContext.BaseDirectory, "natural-empty-" + Guid.NewGuid().ToString("N"));
        object?[][] expected = kind == "LEFT" ? [[1, null]] : [[null, 1]];
        object?[][] frozenRows = kind == "LEFT" ? [[1, 2]] : [[2, 1]];
        try
        {
            await using (var engine = SqlDatabaseEngine.Create("natural-empty", new() { RootPath = root }))
            {
                var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
                await using var session = await database.CreateSessionAsync(CancellationToken.None);
                await ExecAsync(session, "CREATE TABLE a(x INT NOT NULL); CREATE TABLE b(y INT NOT NULL); INSERT INTO a VALUES(1)");
                await using var result = (await session.ExecuteAsync("SELECT * FROM " + from, cancellationToken: CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();
                // Act / Assert
                result.Columns.Select(c => c.IsNullable).ShouldBe(kind == "LEFT" ? [false, true] : new[] { true, false });
                Check(await ReadAsync(result), expected);
                await ExecAsync(session, "CREATE VIEW v AS SELECT * FROM " + from);
                database.Catalog.Views.Single().Definition.ShouldContain(frozen, Case.Sensitive);
                Check(await RowsAsync(session, "SELECT * FROM v"), expected);
                await ExecAsync(session, "ALTER TABLE b ADD COLUMN x INT DEFAULT 99; INSERT INTO b(y) VALUES(2)");
                Check(await RowsAsync(session, "SELECT * FROM v"), frozenRows);
            }
            await using var reopened = SqlDatabaseEngine.Create("natural-empty", new() { RootPath = root });
            var restored = await reopened.OpenDatabaseAsync("db", CancellationToken.None);
            await using var reader = await restored.CreateSessionAsync(CancellationToken.None);
            Check(await RowsAsync(reader, "SELECT * FROM v"), frozenRows);
        }
        finally { if (Directory.Exists(root)) { Directory.Delete(root, true); } }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Star decoration: alias is discarded for ordering and view storage (#1792)")]
    [InlineData("*")]
    [InlineData("t.*")]
    public async Task Execute_StarAlias_ShouldDiscardDecoration(string star)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("star-alias", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecAsync(session, "CREATE TABLE t(a INT,y INT); INSERT INTO t VALUES(1,2),(2,1)");
        // Act / Assert
        Check(await RowsAsync(session, "SELECT " + star + " AS y FROM t ORDER BY y"), [[2, 1], [1, 2]]);
        await ExecAsync(session, "CREATE VIEW v AS SELECT " + star + " AS y FROM t");
        Check(await RowsAsync(session, "SELECT * FROM v ORDER BY y"), [[2, 1], [1, 2]]);
        var error = await Should.ThrowAsync<DatabaseException>(async () =>
            await session.ExecuteAsync("SELECT " + star + " AS missing FROM t ORDER BY missing", cancellationToken: CancellationToken.None));
        error.Diagnostics.ShouldContain(d => d.Code == "CS01015");
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - NATURAL: INSERT conflict tail and aliased UPDATE group execute (#1792)")]
    public async Task Execute_NaturalMutationQuery_ShouldKeepStatementTails()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("natural-tail", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecAsync(session, "CREATE TABLE a(id INT); CREATE TABLE b(id INT); CREATE TABLE t(id INT PRIMARY KEY,v INT DEFAULT 0); INSERT INTO a VALUES(1),(2); INSERT INTO b VALUES(1)");
        // Act
        await ExecAsync(session, "INSERT INTO t(id) SELECT * FROM a NATURAL JOIN b ON CONFLICT DO NOTHING; INSERT INTO t(id) SELECT * FROM a NATURAL JOIN b ON CONFLICT DO NOTHING; UPDATE t SET v=1 FROM (a NATURAL JOIN b) AS j WHERE t.id=j.id");
        // Assert
        Check(await RowsAsync(session, "SELECT * FROM t"), [[1, 1]]);
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(
            "INSERT INTO t(id) SELECT a.id FROM a CROSS JOIN b ON CONFLICT DO NOTHING", cancellationToken: CancellationToken.None));
        error.Diagnostics.ShouldContain(d => d.Code == "CS00003");
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Window targets after USING star count visible outputs (#1792)")]
    [InlineData("p")]
    [InlineData("r")]
    public async Task Execute_WindowUsingStar_ShouldBindFollowingAliases(string order)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("star-window", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecAsync(session, "CREATE TABLE a(x INT); CREATE TABLE b(x INT); INSERT INTO a VALUES(2),(1),(3); INSERT INTO b VALUES(1),(2),(3)");
        // Act / Assert
        var rows = await RowsAsync(session, "SELECT *, x * 10 AS p, ROW_NUMBER() OVER (ORDER BY x) AS r FROM a JOIN b USING(x) ORDER BY " + order + " DESC");
        Check(rows, [[3, 30, 3L], [2, 20, 2L], [1, 10, 1L]]);
    }

    private static async Task ExecAsync(SqlDatabaseSession session, string sql)
    {
        foreach (string part in sql.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        { (await session.ExecuteAsync(part, cancellationToken: CancellationToken.None)).Status.ShouldBe(DatabaseResultStatus.Success); }
    }

    private static async Task<List<object?[]>> RowsAsync(SqlDatabaseSession session, string sql)
    {
        await using var result = (await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();
        return await ReadAsync(result);
    }

    private static async Task<List<object?[]>> ReadAsync(DatabaseResultSet result)
    {
        var rows = new List<object?[]>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None))
        { rows.Add(Enumerable.Range(0, row.FieldCount).Select(row.GetValue).ToArray()); }
        return rows;
    }

    private static void Check(List<object?[]> actual, object?[][] expected)
    {
        actual.Count.ShouldBe(expected.Length);
        for (int i = 0; i < expected.Length; i++) { actual[i].ShouldBe(expected[i]); }
    }
}
