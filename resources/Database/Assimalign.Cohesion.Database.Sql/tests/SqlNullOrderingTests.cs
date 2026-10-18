using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed class SqlNullOrderingTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - ORDER BY: NULL placement is independent of direction across relation shapes")]
    [InlineData("ASC NULLS FIRST", "10,50,30,20,40")]
    [InlineData("ASC NULLS LAST", "30,20,40,10,50")]
    [InlineData("DESC NULLS FIRST", "10,50,20,40,30")]
    [InlineData("DESC NULLS LAST", "20,40,30,10,50")]
    [InlineData("ASC", "10,50,30,20,40")]
    [InlineData("DESC", "20,40,30,10,50")]
    public async Task Execute_NullPlacement_ShouldOrderEveryRelation(string modifier, string expected)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("null-order", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);
        string[] queries =
        [
            $"SELECT id FROM t ORDER BY x {modifier}, id",
            $"SELECT id, x AS alias FROM t ORDER BY alias {modifier}, id",
            $"SELECT id, x FROM t ORDER BY 2 {modifier}, 1",
            $"SELECT a.id FROM t a INNER JOIN t b ON a.id = b.id ORDER BY b.x {modifier}, a.id",
            $"SELECT d.id FROM (SELECT id, x FROM t) d ORDER BY d.x {modifier}, d.id",
            $"SELECT id FROM t GROUP BY id ORDER BY MAX(x) {modifier}, id",
            $"SELECT id, x FROM t UNION ALL SELECT id, x FROM t WHERE id < 0 ORDER BY x {modifier}, id",
            $"SELECT id FROM t WHERE EXISTS (SELECT u.id FROM t u WHERE u.id = t.id ORDER BY u.x {modifier}, u.id) ORDER BY x {modifier}, id",
        ];
        // Act / Assert
        foreach (string query in queries)
        {
            Ids(await RowsAsync(session, query)).ShouldBe(expected, query);
        }
        // Correlated scalar sorting chooses a different first row for each placement/direction.
        string first = expected.Split(',')[0];
        var child = await RowsAsync(session, $"SELECT id, (SELECT u.id FROM t u WHERE u.id >= t.id ORDER BY u.x {modifier}, u.id LIMIT 1) FROM t WHERE id = 10 ORDER BY id");
        child.ShouldHaveSingleItem()[1].ShouldBe(int.Parse(first, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - ORDER BY: mixed placements precede DISTINCT and the row window")]
    public async Task Execute_MixedKeysAndWindow_ShouldApplyPlacementBeforeWindow()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("null-mixed", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);
        // Act
        var mixed = await RowsAsync(session, "SELECT id, x AS alias FROM t ORDER BY alias NULLS FIRST, y NULLS LAST, 1");
        var window = await RowsAsync(session, "SELECT DISTINCT x FROM t ORDER BY 1 NULLS LAST LIMIT 2 OFFSET 1");
        // Assert
        Ids(mixed).ShouldBe("50,10,30,20,40");
        window.Select(row => row[0]).ShouldBe(new object?[] { 2, null });
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - ORDER BY: NaN and infinity retain value order separately from NULL and collation")]
    public async Task Execute_ComparisonEdges_ShouldPreserveValueAndCollationOrder()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("null-edges", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);
        await ExecuteAsync(session, "CREATE TABLE numbers (id INT, n DOUBLE PRECISION)");
        await ExecuteAsync(session, "INSERT INTO numbers VALUES (6, NULL), (3, -2), (4, 0)");
        await session.ExecuteAsync("INSERT INTO numbers VALUES (1, @nan), (2, @neg), (5, @pos)",
            new Dictionary<string, object?> { ["nan"] = double.NaN, ["neg"] = double.NegativeInfinity, ["pos"] = double.PositiveInfinity }, CancellationToken.None);
        // Act / Assert
        Ids(await RowsAsync(session, "SELECT id FROM numbers ORDER BY n ASC NULLS LAST, id")).ShouldBe("1,2,3,4,5,6");
        Ids(await RowsAsync(session, "SELECT id FROM numbers ORDER BY n DESC NULLS FIRST, id")).ShouldBe("6,5,4,3,2,1");
        await ExecuteAsync(session, "INSERT INTO t VALUES (60, NULL, NULL, NULL)");
        Ids(await RowsAsync(session, "SELECT id FROM t ORDER BY name COLLATE case_insensitive NULLS LAST, id")).ShouldBe("30,10,20,50,40,60");
        Ids(await RowsAsync(session, "SELECT id FROM t ORDER BY name COLLATE case_insensitive DESC NULLS FIRST, id")).ShouldBe("60,40,50,20,10,30");
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - ORDER BY: system defaults sort as text with NULL last")]
    public async Task Execute_SystemDefaults_ShouldOrderVerbatimText()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("null-system", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE defaults (z INT, nine INT DEFAULT 9, ten INT DEFAULT 10, a INT)");
        // Act
        var rows = await RowsAsync(session, "SELECT COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'defaults' ORDER BY COLUMN_DEFAULT NULLS LAST, COLUMN_NAME");
        // Assert
        Ids(rows).ShouldBe("ten,nine,a,z");
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - ORDER BY: seek and scan preserve ordering on memory and file storage")]
    [InlineData("INT", "2", false)]
    [InlineData("INT", "2", true)]
    [InlineData("DOUBLE PRECISION", "2.0", false)]
    [InlineData("DOUBLE PRECISION", "2.0", true)]
    [InlineData("TEXT", "'b'", false)]
    [InlineData("TEXT", "'b'", true)]
    public async Task Execute_AccessPaths_ShouldPreserveStorageParity(string type, string value, bool files)
    {
        // Arrange
        string directory = Path.Combine(Path.GetTempPath(), "cohesion-null-order", Guid.NewGuid().ToString("N"));
        try
        {
            await using var engine = SqlDatabaseEngine.Create("null-parity", new()
            {
                RootPath = files ? FileSystemPath.Parse(directory) : (FileSystemPath?)null,
            });
            var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
            await using var session = await database.CreateSessionAsync(CancellationToken.None);
            foreach (string table in new[] { "indexed", "scanned" })
            {
                await ExecuteAsync(session, $"CREATE TABLE {table} (id INT, k {type}, x INT)");
                await ExecuteAsync(session, $"INSERT INTO {table} VALUES (40, {value}, 2), (10, {value}, NULL), (30, {value}, 1), (20, {value}, 2), (50, {value}, NULL), (60, NULL, NULL)");
            }
            await ExecuteAsync(session, "CREATE INDEX ix_k ON indexed (k)");
            // Equality drives a seek even for DOUBLE (whose range encoding is not comparator order).
            // k's ties allow the remaining nullable keys to exercise placement on the seek result.
            foreach (string placement in new[] { "NULLS FIRST", "NULLS LAST" })
            {
                // Act
                var seek = await RowsAsync(session, $"SELECT id FROM indexed WHERE k = {value} ORDER BY k {placement}, x {placement}, id");
                session.LastStatementMetrics.ShouldNotBeNull().AccessPath.ShouldBe("seek:ix_k");
                var scan = await RowsAsync(session, $"SELECT id FROM scanned WHERE k = {value} ORDER BY k {placement}, x {placement}, id");
                session.LastStatementMetrics.ShouldNotBeNull().AccessPath.ShouldBe("scan");
                // Assert
                string expected = placement == "NULLS FIRST" ? "10,50,30,20,40" : "30,20,40,10,50";
                Ids(seek).ShouldBe(expected);
                Ids(scan).ShouldBe(expected);
                var full = await RowsAsync(session, $"SELECT id FROM indexed ORDER BY k {placement}, x {placement}, id");
                Ids(full).ShouldBe(placement == "NULLS FIRST" ? "60," + expected : expected + ",60");
            }
        }
        finally
        {
            if (Directory.Exists(directory)) { Directory.Delete(directory, recursive: true); }
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Aggregates: input NULL placement participates in grouping expression identity")]
    [InlineData("STRING_AGG(name, ',' ORDER BY x NULLS FIRST)", "STRING_AGG(name, ',' ORDER BY x NULLS LAST)")]
    [InlineData("LISTAGG(name, ',') WITHIN GROUP (ORDER BY x NULLS FIRST)", "LISTAGG(name, ',') WITHIN GROUP (ORDER BY x NULLS LAST)")]
    public async Task Execute_AggregateOrdering_ShouldDistinguishPlacement(string first, string last)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("null-aggregate", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE a (g INT, name TEXT, x INT)");
        await ExecuteAsync(session, "INSERT INTO a VALUES (1, 'z', NULL), (1, 'a', 1), (2, 'b', 1), (2, 'a', NULL)");
        // Act
        var rows = await RowsAsync(session, $"SELECT g, {first}, {last} FROM a GROUP BY g ORDER BY {last}, g");
        var distinct = await RowsAsync(session, "SELECT STRING_AGG(DISTINCT name, ',' ORDER BY name NULLS LAST) FROM a ORDER BY 1 NULLS LAST");
        // Assert: matching the FIRST aggregate for the LAST ordering would reverse the groups.
        Ids(rows).ShouldBe("1,2");
        rows[0].ShouldBe(new object?[] { 1, "z,a", "a,z" });
        rows[1].ShouldBe(new object?[] { 2, "a,b", "b,a" });
        distinct.ShouldHaveSingleItem()[0].ShouldBe("a,b,z");
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Views: written NULL placement survives canonical persistence and reopen")]
    [InlineData("SELECT id, x FROM t ORDER BY x NULLS LAST, id", "30,20,40,10,50")]
    [InlineData("SELECT id, x FROM t ORDER BY x DESC NULLS FIRST, id", "10,50,20,40,30")]
    [InlineData("SELECT STRING_AGG(name, ',' ORDER BY x NULLS LAST, id) AS value FROM t", "Alice,dave,Zed,bob,Eve")]
    [InlineData("SELECT LISTAGG(name, ',') WITHIN GROUP (ORDER BY x DESC NULLS FIRST, id) AS value FROM t", "bob,Eve,dave,Zed,Alice")]
    [InlineData("SELECT id, x FROM t UNION ALL SELECT id, x FROM t WHERE id < 0 ORDER BY x NULLS LAST, id", "30,20,40,10,50")]
    public async Task Open_ViewNullPlacement_ShouldPreserveSyntaxAndResults(string body, string expected)
    {
        // Arrange
        var storage = new CrashCaptureSqlStorageStrategy();
        await using (var engine = SqlDatabaseEngine.Create("null-view", new() { StorageStrategy = storage }))
        {
            var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
            await using var session = await database.CreateSessionAsync(CancellationToken.None);
            await SeedAsync(session);
            await ExecuteAsync(session, "CREATE VIEW v AS " + body);
            Ids(await RowsAsync(session, "SELECT * FROM v")).ShouldBe(expected);
        }
        // Act
        await using var reopened = SqlDatabaseEngine.Create("null-view", new() { StorageStrategy = storage.CaptureDurableImages() });
        var restored = await reopened.OpenDatabaseAsync("db", CancellationToken.None);
        await using var reader = await restored.CreateSessionAsync(CancellationToken.None);
        // Assert
        Ids(await RowsAsync(reader, "SELECT * FROM v")).ShouldBe(expected);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Storage format: format 23 is refused before loading NULL placement in a view")]
    public async Task Open_PreviousFormat_ShouldRefuseBeforeViewBinding()
    {
        // Arrange
        var storage = new CrashCaptureSqlStorageStrategy();
        await using (var engine = SqlDatabaseEngine.Create("null-format", new() { StorageStrategy = storage }))
        {
            var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
            await using var session = await database.CreateSessionAsync(CancellationToken.None);
            await SeedAsync(session);
            await ExecuteAsync(session, "CREATE VIEW v AS SELECT id, x FROM t ORDER BY x NULLS LAST, id");
            await database.Catalog.SetRecordSpaceFormatVersionAsync(23, CancellationToken.None);
        }
        // Act
        await using var reopened = SqlDatabaseEngine.Create("null-format", new() { StorageStrategy = storage.CaptureDurableImages() });
        var error = await Should.ThrowAsync<SqlDataStorageFormatException>(async () =>
            await reopened.OpenDatabaseAsync("db", CancellationToken.None));
        // Assert
        error.Message.ShouldContain("uses data-storage format 23", Case.Sensitive);
        error.Message.ShouldContain("supports only format 24", Case.Sensitive);
    }

    private static async Task SeedAsync(SqlDatabaseSession session)
    {
        await ExecuteAsync(session, "CREATE TABLE t (id INT PRIMARY KEY, x INT, y INT, name TEXT)");
        await ExecuteAsync(session, "INSERT INTO t VALUES (40, 2, NULL, 'Zed'), (10, NULL, 2, 'bob'), (30, 1, 1, 'Alice'), (20, 2, 2, 'dave'), (50, NULL, 1, 'Eve')");
    }

    private static async Task ExecuteAsync(SqlDatabaseSession session, string sql)
    {
        var result = await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None);
        result.Status.ShouldBe(DatabaseResultStatus.Success, sql);
        result.Diagnostics?.ShouldBeEmpty(sql);
    }

    private static async Task<List<object?[]>> RowsAsync(SqlDatabaseSession session, string sql)
    {
        var result = await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None);
        result.Status.ShouldBe(DatabaseResultStatus.Success, sql);
        result.Diagnostics?.ShouldBeEmpty(sql);
        await using var rows = result.ShouldBeAssignableTo<DatabaseResultSet>();
        var values = new List<object?[]>();
        await foreach (var row in rows.GetRowsAsync(CancellationToken.None))
        {
            values.Add(Enumerable.Range(0, row.FieldCount).Select(row.GetValue).ToArray());
        }
        return values;
    }

    private static string Ids(IEnumerable<object?[]> rows) => string.Join(",", rows.Select(row => row[0]));
}
