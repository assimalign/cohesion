using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Connections.InMemory;
using Assimalign.Cohesion.Connections.Tcp;
using Assimalign.Cohesion.Database.Client;
using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Client;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed class SqlNaturalJoinTests
{
    [Fact(DisplayName = "Cohesion Test [Sql] - Persisted scalar definitions: qualified stars retain the invalid row-expression guard (#1792)")]
    public void Bind_PersistedQualifiedStar_ShouldRefuseRowExpression()
    {
        // Arrange
        var statement = (SqlQueryStatement)new SqlQueryParser().Parse("SELECT t.*");
        var star = statement.SqlExpression.ShouldBeOfType<SqlSelectExpression>().Columns[0].Expression;
        var evaluator = new SqlExpressionEvaluator([], null);
        // Act
        var error = Should.Throw<SqlExecutionException>(() => SqlPersistedExpression.Bind(star, evaluator));
        // Assert
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS03059");
    }

    public static IEnumerable<object[]> OracleCases()
    {
        foreach (string kind in new[] { "INNER", "LEFT", "RIGHT", "FULL" })
        {
            foreach (string source in new[] { "stored", "derived", "view", "system" })
            {
                foreach (int common in new[] { 0, 1, 2 })
                {
                    foreach (bool differentCase in new[] { false, true })
                    { yield return [kind, source, common, differentCase]; }
                }
            }
        }
    }

    // Independent hand oracle: PostgreSQL parse_clause.c:1242-1274 derives USING in
    // left output order, and :1735-1756 preserves RIGHT/FULL representatives.
    // Zero-name LEFT/RIGHT joins preserve their outer side (parse_clause.c:1408-1411).
    [Theory(DisplayName = "Cohesion Test [Sql] - NATURAL oracle: join types, source kinds, common names and case (#1792)")]
    [MemberData(nameof(OracleCases))]
    public async Task Execute_NaturalOracle_ShouldMatchHandDerivedRows(string kind, string source, int common, bool differentCase)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("natural-oracle", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        string from;
        string query;
        object?[][] expected;
        if (source == "system")
        {
            string schema = differentCase ? "schema_name" : "SCHEMA_NAME";
            string catalog = differentCase ? "catalog_name" : "CATALOG_NAME";
            await ExecAsync(session, "CREATE SCHEMA alpha; CREATE SCHEMA beta;");
            string keys = common == 0 ? "x INT" : common == 1 ? schema + " TEXT" : schema + " TEXT," + catalog + " TEXT";
            await ExecAsync(session, "CREATE TABLE a(left_value INT," + keys + "); INSERT INTO a VALUES" +
                (common == 0 ? "(1,1),(9,9)" : common == 1 ? "(1,'dbo'),(9,'ghost')" : "(1,'dbo','db'),(9,'ghost','db')"));
            from = "a NATURAL " + kind + " JOIN INFORMATION_SCHEMA.SCHEMATA b";
            query = "SELECT a.left_value,b.SCHEMA_NAME FROM " + from;
            expected = common == 0
                ? [[1, "dbo"], [1, "alpha"], [1, "beta"], [9, "dbo"], [9, "alpha"], [9, "beta"]]
                : kind switch
                {
                    "LEFT" => [[1, "dbo"], [9, null]],
                    "RIGHT" => [[1, "dbo"], [null, "alpha"], [null, "beta"]],
                    "FULL" => [[1, "dbo"], [9, null], [null, "alpha"], [null, "beta"]],
                    _ => [[1, "dbo"]],
                };
        }
        else
        {
            string id = differentCase ? "ID" : "id";
            string key = differentCase ? "K" : "k";
            string leftId = common > 0 ? "id" : "left_id";
            string leftKey = common > 1 ? "k" : "left_key";
            await ExecAsync(session, $"CREATE TABLE a(left_value TEXT,{leftId} INT,{leftKey} INT); CREATE TABLE b({key} INT,{id} INT,right_value TEXT); INSERT INTO a VALUES('L1',1,10),('L3',3,30); INSERT INTO b VALUES(10,1,'R1'),(20,3,'R2');");
            string right = source switch
            {
                "derived" => "(SELECT * FROM b) b",
                "view" => "v b",
                _ => "b",
            };
            if (source == "view") { await ExecAsync(session, "CREATE VIEW v AS SELECT * FROM b"); }
            from = "a NATURAL " + kind + " JOIN " + right;
            query = "SELECT a.left_value,b.right_value FROM " + from;
            expected = common == 0 ? [["L1", "R1"], ["L1", "R2"], ["L3", "R1"], ["L3", "R2"]]
                : common == 1 ? [["L1", "R1"], ["L3", "R2"]]
                : kind switch
                {
                    "LEFT" => [["L1", "R1"], ["L3", null]],
                    "RIGHT" => [["L1", "R1"], [null, "R2"]],
                    "FULL" => [["L1", "R1"], ["L3", null], [null, "R2"]],
                    _ => [["L1", "R1"]],
                };
        }
        // Act / Assert
        if (common == 0 && kind == "FULL")
        {
            var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(query, cancellationToken: CancellationToken.None));
            error.Diagnostics.ShouldContain(item => item.Code == "CS05070");
            return;
        }
        Bag(await RowsAsync(session, query)).ShouldBe(Bag(expected));
        if (source != "system" && common == 2)
        {
            await using var result = (await session.ExecuteAsync("SELECT * FROM " + from, cancellationToken: CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();
            result.Columns.Select(column => column.Name).ShouldBe(["id", "k", "left_value", "right_value"]);
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - NATURAL: no common names preserve the outer side with an empty input (#1792)")]
    [InlineData("INNER", false)]
    [InlineData("LEFT", false)]
    [InlineData("RIGHT", false)]
    [InlineData("INNER", true)]
    [InlineData("LEFT", true)]
    [InlineData("RIGHT", true)]
    public async Task Execute_NaturalEmptyInput_ShouldPreserveOuterSide(string kind, bool leftEmpty)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("natural-empty", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecAsync(session, "CREATE TABLE a(x INT NOT NULL); CREATE TABLE b(y INT NOT NULL); INSERT INTO " + (leftEmpty ? "b" : "a") + " VALUES(1)");
        // Act / Assert
        var rows = await RowsAsync(session, "SELECT * FROM a NATURAL " + kind + " JOIN b");
        if (kind == "LEFT" && !leftEmpty) { Check(rows, [[1, null]]); }
        else if (kind == "RIGHT" && leftEmpty) { Check(rows, [[null, 1]]); }
        else { rows.ShouldBeEmpty(); }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - NATURAL: repeated common names fail as USING (#1792)")]
    [InlineData("(SELECT id,id FROM a) x NATURAL JOIN b")]
    [InlineData("a NATURAL JOIN (SELECT id,id FROM b) x")]
    public async Task Execute_NaturalDuplicate_ShouldRefuseBeforeRows(string from)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("natural-duplicates", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecAsync(session, "CREATE TABLE a(id INT); CREATE TABLE b(id INT)");
        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("SELECT * FROM " + from, cancellationToken: CancellationToken.None));
        // Assert
        error.Diagnostics.ShouldContain(item => item.Code == "CS01015");
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Qualified stars: physical USING columns retain types and outer nullability (#1792)")]
    [InlineData("LEFT", false, true)]
    [InlineData("RIGHT", true, false)]
    [InlineData("FULL", true, true)]
    public async Task Execute_QualifiedStars_ShouldKeepPhysicalInputs(string kind, bool leftNullable, bool rightNullable)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("qualified-using", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecAsync(session, "CREATE TABLE a(id INT NOT NULL, v INT NOT NULL); CREATE TABLE b(id BIGINT NOT NULL, w INT NOT NULL); INSERT INTO a VALUES(1,10),(3,30); INSERT INTO b VALUES(1,100),(2,200)");
        // Act
        await using var result = (await session.ExecuteAsync("SELECT a.*,b.*,99 AS tail FROM a " + kind + " JOIN b USING(id) ORDER BY tail,a.id", cancellationToken: CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();
        var rows = await ReadAsync(result);
        // Assert
        result.Columns.Select(column => column.Name).ShouldBe(["id", "v", "id", "w", "tail"]);
        result.Columns.Take(4).Select(column => column.Type).ShouldBe([DatabaseType.Int32, DatabaseType.Int32, DatabaseType.Int64, DatabaseType.Int32]);
        result.Columns.Take(4).Select(column => column.IsNullable).ShouldBe([leftNullable, leftNullable, rightNullable, rightNullable]);
        object?[][] expected = kind switch
        {
            "LEFT" => [[1, 10, 1L, 100, 99], [3, 30, null, null, 99]],
            "RIGHT" => [[null, null, 2L, 200, 99], [1, 10, 1L, 100, 99]],
            _ => [[null, null, 2L, 200, 99], [1, 10, 1L, 100, 99], [3, 30, null, null, 99]],
        };
        Check(rows, expected);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Qualified stars: derived, view, system, schema, compound and ordering targets expand (#1792)")]
    public async Task Execute_QualifiedSources_ShouldExpandAndCountOutputs()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("qualified-sources", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecAsync(session, "CREATE SCHEMA s; CREATE TABLE s.t(id INT,v INT); CREATE TABLE dbo.t(id INT); INSERT INTO s.t VALUES(1,20),(2,10); CREATE VIEW s.v AS SELECT * FROM s.t");
        // Act / Assert
        foreach (string from in new[] { "s.t", "s.v", "(SELECT * FROM s.t) AS d" })
        {
            string name = from.StartsWith('(') ? "d" : from;
            Check(await RowsAsync(session, "SELECT " + name + ".*, 3-v AS tail FROM " + from + " ORDER BY tail"), [[1, 20, -17], [2, 10, -7]]);
        }
        Check(await RowsAsync(session, "SELECT s.t.* FROM s.t UNION SELECT d.* FROM (SELECT id,v FROM s.t) d ORDER BY 1"), [[1, 20], [2, 10]]);
        Check(await RowsAsync(session, "SELECT t.*, ROW_NUMBER() OVER(ORDER BY id) AS n FROM s.t ORDER BY n DESC"), [[2, 10, 2L], [1, 20, 1L]]);
        (await RowsAsync(session, "SELECT c.* FROM INFORMATION_SCHEMA.COLUMNS c WHERE TABLE_SCHEMA='s' AND TABLE_NAME='t'")).Count.ShouldBe(2);
        foreach (string sql in new[] { "SELECT missing.* FROM s.t", "SELECT s.t.* FROM s.t hidden", "SELECT s.d.* FROM (SELECT 1 AS id) d", "SELECT t.*", "SELECT t.* FROM s.t, dbo.t" })
        {
            var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None));
            error.Diagnostics.ShouldContain(item => item.Code == "CS01015" || item.Code == "CS01014");
        }
        Check(await RowsAsync(session, "SELECT * FROM (s.t a NATURAL JOIN s.t b) NATURAL JOIN s.t c ORDER BY id"), [[1, 20], [2, 10]]);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Views: NATURAL and qualified stars stay frozen after ADD COLUMN and reopen (#1792)")]
    public async Task Open_NaturalAndQualifiedView_ShouldKeepFrozenRowsAndColumns()
    {
        // Arrange
        string root = Path.Combine(AppContext.BaseDirectory, "natural-views-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using (var engine = SqlDatabaseEngine.Create("natural-views", new() { RootPath = root }))
            {
                var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
                await using var session = await database.CreateSessionAsync(CancellationToken.None);
                await ExecAsync(session, "CREATE TABLE a(id INT,v INT); CREATE TABLE b(id INT,tail INT); INSERT INTO a VALUES(1,10),(2,20); INSERT INTO b VALUES(1,100); CREATE VIEW n AS SELECT * FROM a NATURAL LEFT JOIN b; CREATE VIEW q AS SELECT a.*, b.tail AS tail FROM a NATURAL LEFT JOIN b; CREATE VIEW one AS SELECT a.* FROM a; CREATE TABLE names(natural INT,lateral INT); INSERT INTO names VALUES(4,5); CREATE VIEW quoted AS SELECT natural,lateral FROM names; CREATE VIEW z AS SELECT a.id,b.tail FROM a NATURAL RIGHT JOIN (SELECT tail FROM b) b;");
                foreach (var view in database.Catalog.Views)
                {
                    view.Definition.ShouldNotContain("NATURAL", Case.Sensitive);
                    view.Definition.ShouldNotContain(".*", Case.Sensitive);
                }
                database.Catalog.Views.Single(view => view.Name == "n").Definition.ShouldContain("USING (id)", Case.Sensitive);
                database.Catalog.Views.Single(view => view.Name == "quoted").Definition.ShouldContain("\"natural\"", Case.Sensitive);
                await ExecAsync(session, "ALTER TABLE a ADD COLUMN tail INT DEFAULT 999; ALTER TABLE b ADD COLUMN v INT DEFAULT 999");
                foreach (string view in new[] { "n", "q" }) { Check(await RowsAsync(session, "SELECT * FROM " + view + " ORDER BY id"), [[1, 10, 100], [2, 20, null]]); }
                Check(await RowsAsync(session, "SELECT a.id,b.tail FROM a NATURAL LEFT JOIN b ORDER BY a.id"), [[1, null], [2, null]]);
            }
            // Act
            await using var reopened = SqlDatabaseEngine.Create("natural-views", new() { RootPath = root });
            var restored = await reopened.OpenDatabaseAsync("db", CancellationToken.None);
            await using var reader = await restored.CreateSessionAsync(CancellationToken.None);
            // Assert
            foreach (string view in new[] { "n", "q" }) { Check(await RowsAsync(reader, "SELECT * FROM " + view + " ORDER BY id"), [[1, 10, 100], [2, 20, null]]); }
            Check(await RowsAsync(reader, "SELECT * FROM one ORDER BY id"), [[1, 10], [2, 20]]);
            Check(await RowsAsync(reader, "SELECT * FROM z ORDER BY id"), [[1, 100], [2, 100]]);
            Check(await RowsAsync(reader, "SELECT * FROM quoted"), [[4, 5]]);
        }
        finally { if (Directory.Exists(root)) { Directory.Delete(root, true); } }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Qualified stars: mutation sources accept SELECT bodies and preserve aggregate namespaces (#1792)")]
    public async Task Execute_QualifiedQueryBodies_ShouldKeepSelectScope()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("qualified-dml", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecAsync(session, "CREATE TABLE t(id INT,v INT); CREATE TABLE u(id INT,v INT); INSERT INTO t VALUES(1,0); INSERT INTO u VALUES(1,10)");
        const string source = "(SELECT u.* FROM u NATURAL JOIN u x) src";
        // Act / Assert
        await ExecAsync(session, "UPDATE t SET v=src.v FROM " + source + " WHERE t.id=src.id");
        Check(await RowsAsync(session, "SELECT * FROM t"), [[1, 10]]);
        await ExecAsync(session, "DELETE FROM t USING " + source + " WHERE t.id=src.id");
        (await RowsAsync(session, "SELECT * FROM t")).ShouldBeEmpty();
        await ExecAsync(session, "MERGE INTO t USING " + source + " ON t.id=src.id WHEN NOT MATCHED THEN INSERT(id,v) VALUES(src.id,src.v)");
        Check(await RowsAsync(session, "SELECT * FROM t"), [[1, 10]]);
        Check(await RowsAsync(session, "SELECT (SELECT SUM(id) FROM t NATURAL JOIN u) FROM (SELECT 1 AS marker) outer_input"), [[1L]]);
        Check(await RowsAsync(session, "SELECT (SELECT SUM(d.id) FROM (SELECT t.* FROM t) d) FROM u"), [[1L]]);
        Check(await RowsAsync(session, "SELECT (SELECT SUM(id) FROM (SELECT * FROM t NATURAL JOIN u) d) FROM u"), [[1L]]);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - NATURAL and qualified stars: InMemory and TCP smoke (#1792)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueryAsync_NaturalJoin_ShouldPreserveWireRows(bool tcp)
    {
        // Arrange
        using var ceiling = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = SqlDatabaseEngine.Create("natural-wire", new());
        var database = await engine.CreateDatabaseAsync("db", ceiling.Token);
        await using var session = await database.CreateSessionAsync(ceiling.Token);
        await ExecAsync(session, "CREATE TABLE a(id INT); CREATE TABLE b(id INT); INSERT INTO a VALUES(1),(2); INSERT INTO b VALUES(1)");
        await using IDuplexConnectionListener listener = tcp
            ? TcpConnectionListener.Create(options => options.EndPoint = new IPEndPoint(IPAddress.Loopback, 0)) : new InMemoryConnectionListener();
        await using var server = SqlDatabaseServer.Create(engine, new() { Listener = listener });
        await server.StartAsync(ceiling.Token);
        await using var client = SqlClient.Create(new()
        {
            Settings = new DatabaseConnectionSettings { Database = "db", Principal = "natural", EndPoint = listener.EndPoint },
            ConnectionFactory = tcp ? new TcpConnectionFactory() : ((InMemoryConnectionListener)listener).CreateFactory(),
        });
        await using var connection = await client.ConnectAsync(ceiling.Token);
        // Act
        var rows = await connection.QueryAsync("SELECT a.*,b.* FROM a NATURAL LEFT JOIN b ORDER BY a.id", cancellationToken: ceiling.Token);
        // Assert
        rows.Count.ShouldBe(2);
        rows[0][0].ShouldBe(1); rows[0][1].ShouldBe(1);
        rows[1][0].ShouldBe(2); rows[1][1].ShouldBeNull();
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
        await foreach (var row in result.GetRowsAsync(CancellationToken.None)) { rows.Add(Enumerable.Range(0, row.FieldCount).Select(row.GetValue).ToArray()); }
        return rows;
    }

    private static string[] Bag(IEnumerable<object?[]> rows) => rows.Select(row => string.Join("|", row.Select(value => value?.ToString() ?? "<NULL>"))).Order(StringComparer.Ordinal).ToArray();

    private static void Check(IReadOnlyList<object?[]> rows, object?[][] expected)
    {
        rows.Count.ShouldBe(expected.Length);
        for (int i = 0; i < rows.Count; i++) { rows[i].ShouldBe(expected[i]); }
    }
}
