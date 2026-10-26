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
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

[CollectionDefinition("FullJoinProgress", DisableParallelization = true)]
public sealed class SqlFullJoinProgressCollection;

[Collection("FullJoinProgress")]
public sealed class SqlFullJoinTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - FULL hash growth keeps NULL-key entries, duplicate matches and residual misses (#1525)")]
    [InlineData(17)]
    [InlineData(80)]
    public async Task Join_FullGrowth_ShouldRetainEveryBuildRow(int count)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("full-grow", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecAsync(session, "CREATE TABLE a(id INT, k INT); CREATE TABLE b(id INT, k INT);");
        foreach (string table in new[] { "a", "b" })
        {
            await ExecAsync(session, "INSERT INTO " + table + " VALUES " + string.Join(',', Enumerable.Range(1, count)
                .Select(i => $"({i},{(i % 7 == 0 ? "NULL" : (i % 5).ToString(System.Globalization.CultureInfo.InvariantCulture))})")));
        }
        var expected = new List<object?[]>();
        var matched = new HashSet<int>();
        for (int a = 1; a <= count; a++)
        {
            var matches = Enumerable.Range(1, count).Where(b => a % 7 != 0 && b % 7 != 0 && a % 5 == b % 5 && a < b).ToArray();
            if (matches.Length == 0) { expected.Add([a, null]); }
            foreach (int b in matches) { expected.Add([a, b]); matched.Add(b); }
        }
        foreach (int b in Enumerable.Range(1, count).Where(b => !matched.Contains(b))) { expected.Add([null, b]); }

        // Act
        var rows = await RowsAsync(session, "SELECT a.id,b.id FROM a FULL JOIN b ON a.k = b.k AND a.id < b.id");

        // Assert
        Bag(rows).ShouldBe(Bag(expected));
        session.LastStatementMetrics!.HashMemoryBytes.ShouldBe(0);
        session.LastStatementMetrics.RecordsExamined.ShouldBe(2L * count);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - FULL bags preserve duplicate, NULL, residual and empty-side rows (#1525)")]
    [InlineData(false, "a.k = b.k")]
    [InlineData(true, "a.k = b.k")]
    [InlineData(false, "a.k = b.k AND a.v < b.v")]
    [InlineData(false, "a.k = b.k AND FALSE")]
    [InlineData(false, "a.k = b.k AND NULL")]
    public async Task Join_FullBags_ShouldEqualBothLeftJoinBags(bool indexed, string condition)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("full-bags", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecAsync(session, "CREATE TABLE a (id INT NOT NULL, k INT, v INT); CREATE TABLE b (id INT NOT NULL, k INT, v INT); INSERT INTO a VALUES (1,1,0),(2,1,2),(3,NULL,3),(4,4,4); INSERT INTO b VALUES (1,1,1),(2,1,2),(3,NULL,3),(4,2,4);");
        if (indexed) { await ExecAsync(session, "CREATE INDEX ix_b ON b(k);"); }
        for (int empty = 0; empty < 4; empty++)
        {
            // Act
            var actual = await RowsAsync(session, $"SELECT a.id,b.id FROM a FULL OUTER JOIN b ON {condition}");
            var metrics = session.LastStatementMetrics!;
            var expected = await RowsAsync(session, $"SELECT a.id,b.id FROM a LEFT JOIN b ON {condition}");
            expected.AddRange(await RowsAsync(session, $"SELECT a.id,b.id FROM b LEFT JOIN a ON {condition} WHERE a.id IS NULL"));

            // Assert
            Bag(actual).ShouldBe(Bag(expected));
            metrics.JoinAccessPaths.ShouldBe(["join-hash"]);
            metrics.HashMemoryBytes.ShouldBe(0);
            await ExecAsync(session, empty == 0 ? "DELETE FROM a;" : empty == 1 ? "INSERT INTO a VALUES (1,1,0); DELETE FROM b;" : "DELETE FROM a;");
        }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - FULL USING coalesces common types, spelling, collation and written ordinals (#1525)")]
    public async Task Join_FullUsing_ShouldCoalesceBothInputs()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("full-using", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecAsync(session, "CREATE TABLE a (extra TEXT NOT NULL, id DECIMAL NOT NULL, name VARCHAR(8) COLLATE case_insensitive NOT NULL); CREATE TABLE b (id INT NOT NULL, name VARCHAR(12) COLLATE case_insensitive NOT NULL, tail TEXT NOT NULL); INSERT INTO a VALUES ('L',1,'Ada'),('only',3,'Lin'); INSERT INTO b VALUES (1,'ADA','R'),(2,'Grace','only');");

        // Act
        await using var result = (await session.ExecuteAsync("SELECT *,a.id AS aid,b.id AS bid FROM a FULL JOIN b USING(name,id) ORDER BY id", cancellationToken: CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();
        var rows = await ReadAsync(result);

        // Assert
        Check(rows, [["Ada", 1m, "L", "R", 1m, 1], ["Grace", 2m, null, "only", null, 2], ["Lin", 3m, "only", null, 3m, null]]);
        result.Columns.Select(column => column.Name).ShouldBe(["name", "id", "extra", "tail", "aid", "bid"]);
        result.Columns.Select(column => column.IsNullable).ShouldBe([false, false, true, true, true, true]);
        result.Columns[0].MaxLength.ShouldBeNull();
        result.Columns[1].Type.ShouldBe(DatabaseType.Decimal);
        Check(await RowsAsync(session, "SELECT id FROM a FULL JOIN b USING(name,id) WHERE name = 'ADA'"), [[1m]]);
        Check(await RowsAsync(session, "SELECT COUNT(*),COUNT(a.id),COUNT(b.id) FROM a FULL JOIN b USING(name,id)"), [[3L, 2L, 2L]]);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - FULL chains and parenthesized subtrees retain computed USING keys (#1525)")]
    [InlineData("a FULL JOIN b USING(id) FULL JOIN c USING(id)")]
    [InlineData("a FULL JOIN (b FULL JOIN c USING(id)) USING(id)")]
    [InlineData("(a FULL JOIN b USING(id)) FULL JOIN c USING(id)")]
    public async Task Join_FullUsingChain_ShouldKeepMergedKeys(string from)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("full-chain", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecAsync(session, "CREATE TABLE a(id INT NOT NULL); CREATE TABLE b(id BIGINT NOT NULL); CREATE TABLE c(id DECIMAL NOT NULL); INSERT INTO a VALUES(1); INSERT INTO b VALUES(2); INSERT INTO c VALUES(2),(3);");

        // Act / Assert
        Check(await RowsAsync(session, "SELECT id,a.id,b.id,c.id FROM " + from + " ORDER BY id"), [[1m, 1, null, null], [2m, null, 2L, 2m], [3m, null, null, 3m]]);
        session.LastStatementMetrics!.HashMemoryBytes.ShouldBe(0);
        Check(await RowsAsync(session, "SELECT id FROM (" + from + ") q ORDER BY id"), [[1m], [2m], [3m]]);
        await ExecAsync(session, "DELETE FROM a;");
        Check(await RowsAsync(session, "SELECT id FROM " + from + " ORDER BY id"), [[2m], [3m]]);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - FULL unsupported physical conditions fail before reads, even on empty inputs (#1525)")]
    [InlineData("a.id < b.id", "CS05070")]
    [InlineData("TRUE", "CS05070")]
    [InlineData("a.id = b.id OR a.id = 1", "CS05070")]
    [InlineData("a.id = b.id AND a.id / 0 = 1", "CS05070")]
    [InlineData("a.id = b.id AND EXISTS(SELECT 1)", "CS05070")]
    [InlineData("missing.id = b.id", "CS01015")]
    public async Task Join_UnsupportedFullCondition_ShouldRefuseBeforeReading(string condition, string code)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("full-refuse", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecAsync(session, "CREATE TABLE a(id INT); CREATE TABLE b(id INT);");

        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("SELECT * FROM a FULL JOIN b ON " + condition, cancellationToken: CancellationToken.None));

        // Assert
        error.Diagnostics.ShouldContain(item => item.Code == code);
        session.LastStatementMetrics!.RecordsExamined.ShouldBe(0);
        session.LastStatementMetrics.HashMemoryBytes.ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - FULL direct DML sources refuse before reads; derived FULL sources keep target identities (#1525)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Execute_FullDmlSource_ShouldKeepTargetOuter(bool delete)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("full-dml", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecAsync(session, "CREATE TABLE t(id INT PRIMARY KEY,v INT); CREATE TABLE a(id INT); CREATE TABLE b(id INT); INSERT INTO t VALUES(1,0),(2,0),(3,0); INSERT INTO a VALUES(1); INSERT INTO b VALUES(2);");
        string prefix = delete ? "DELETE FROM t USING " : "UPDATE t SET v = 9 FROM ";

        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(prefix + "a FULL JOIN b USING(id)", cancellationToken: CancellationToken.None));

        // Assert
        error.Diagnostics.ShouldContain(item => item.Code == "CS05069");
        session.LastStatementMetrics!.RecordsExamined.ShouldBe(0);
        Check(await RowsAsync(session, "SELECT v FROM t ORDER BY id"), [[0], [0], [0]]);
        await ExecAsync(session, prefix + "(SELECT id FROM a FULL JOIN b USING(id)) q WHERE t.id = q.id;");
        Check(await RowsAsync(session, "SELECT id,v FROM t ORDER BY id"), delete ? [[3, 0]] : [[1, 9], [2, 9], [3, 0]]);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - FULL null-key and nested build reservations fail within the statement grant (#1525)")]
    [InlineData("b")]
    [InlineData("(b FULL JOIN c USING(id))")]
    public async Task Join_FullBudget_ShouldReleaseEveryReservation(string right)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("full-budget", new() { StatementMemoryBudget = 1024 });
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecAsync(session, "CREATE TABLE a(id INT); CREATE TABLE b(id INT); CREATE TABLE c(id INT); INSERT INTO c VALUES(1);");
        await ExecAsync(session, "INSERT INTO b VALUES " + string.Join(',', Enumerable.Repeat("(NULL)", 100)));

        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("SELECT * FROM a FULL JOIN " + right + " USING(id)", cancellationToken: CancellationToken.None));

        // Assert
        error.Diagnostics.ShouldContain(item => item.Code == "CS05067");
        var metrics = session.LastStatementMetrics!;
        metrics.HashMemoryBytes.ShouldBe(0);
        metrics.PeakHashMemoryBytes.ShouldBeInRange(1, 1024);
        if (right == "b") { metrics.RecordsExamined.ShouldBeLessThan(100); }
        else { metrics.RecordsExamined.ShouldBe(101); } // The child's preserved outer is buffered before its bounded inner build.
        Check(await RowsAsync(session, "SELECT COUNT(*) FROM b"), [[100L]]);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - FULL cancellation during build, match and unmatched drain releases live grants (#1525)")]
    [InlineData("build")]
    [InlineData("match")]
    [InlineData("unmatched")]
    public async Task Join_FullCancellation_ShouldReleaseLiveGrant(string phase)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("full-cancel", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecAsync(session, "CREATE TABLE a(id INT); CREATE TABLE b(id INT); INSERT INTO a VALUES(1); INSERT INTO b VALUES(1),(NULL),(2);");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        int calls = 0;
        SqlPlanExecutor.FullJoinProgress = (at, metrics, _) =>
        {
            if (at != phase) { return; }
            metrics.HashMemoryBytes.ShouldBeGreaterThan(0);
            calls++;
            cancellation.Cancel();
        };
        try
        {
            // Act
            await Should.ThrowAsync<OperationCanceledException>(async () => await session.ExecuteAsync("SELECT * FROM a FULL JOIN b USING(id)", cancellationToken: cancellation.Token));

            // Assert
            calls.ShouldBe(1);
            session.LastStatementMetrics!.HashMemoryBytes.ShouldBe(0);
        }
        finally { SqlPlanExecutor.FullJoinProgress = null; }
        Check(await RowsAsync(session, "SELECT COUNT(*) FROM b"), [[3L]]);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - FULL wire timeout in each physical phase releases memory and preserves connection reuse (#1525)")]
    [InlineData(false, "build")]
    [InlineData(true, "build")]
    [InlineData(false, "match")]
    [InlineData(true, "match")]
    [InlineData(false, "unmatched")]
    [InlineData(true, "unmatched")]
    public async Task QueryAsync_FullWireTimeout_ShouldReleaseLiveGrant(bool tcp, string phase)
    {
        // Arrange
        using var ceiling = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = SqlDatabaseEngine.Create("full-wire-timeout", new() { StatementTimeout = TimeSpan.FromSeconds(1) });
        var database = await engine.CreateDatabaseAsync("db", ceiling.Token);
        await using var session = await database.CreateSessionAsync(ceiling.Token);
        await ExecAsync(session, "CREATE TABLE a(id INT); CREATE TABLE b(id INT); INSERT INTO a VALUES(1); INSERT INTO b VALUES(1),(2),(NULL);");
        await using IDuplexConnectionListener listener = tcp
            ? TcpConnectionListener.Create(options => options.EndPoint = new IPEndPoint(IPAddress.Loopback, 0))
            : new InMemoryConnectionListener();
        await using var server = SqlDatabaseServer.Create(engine, new() { Listener = listener });
        await server.StartAsync(ceiling.Token);
        await using var client = SqlClient.Create(new()
        {
            Settings = new DatabaseConnectionSettings { Database = "db", Principal = "full-timeout", EndPoint = listener.EndPoint },
            ConnectionFactory = tcp ? new TcpConnectionFactory() : ((InMemoryConnectionListener)listener).CreateFactory(),
        });
        await using var connection = await client.ConnectAsync(ceiling.Token);
        SqlStatementMetrics? observed = null;
        int calls = 0;
        SqlPlanExecutor.FullJoinProgress = (at, metrics, token) =>
        {
            if (at != phase) { return; }
            observed = metrics;
            metrics.HashMemoryBytes.ShouldBeGreaterThan(0);
            calls++;
            token.WaitHandle.WaitOne(TimeSpan.FromSeconds(30)).ShouldBeTrue();
            token.ThrowIfCancellationRequested();
        };
        try
        {
            // Act
            var error = await Should.ThrowAsync<SqlClientException>(async () => await connection.QueryAsync(
                "SELECT * FROM a FULL JOIN b USING(id)", cancellationToken: ceiling.Token));

            // Assert
            calls.ShouldBe(1);
            error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS05043");
            error.ConnectionUsable.ShouldBeTrue();
            observed.ShouldNotBeNull().HashMemoryBytes.ShouldBe(0);
            ceiling.IsCancellationRequested.ShouldBeFalse();
        }
        finally { SqlPlanExecutor.FullJoinProgress = null; }
        (await connection.QueryAsync("SELECT COUNT(*) FROM b", cancellationToken: ceiling.Token)).ShouldHaveSingleItem()[0].ShouldBe(3L);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - FULL hash has linear candidate work; EXPLAIN identifies its operator (#1525)")]
    public async Task Join_FullWork_ShouldAvoidCartesianCandidates()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("full-work", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        foreach (int count in new[] { 64, 640 })
        {
            await ExecAsync(session, $"CREATE TABLE a{count}(id INT); CREATE TABLE b{count}(id INT);");
            await ExecAsync(session, $"INSERT INTO a{count} VALUES " + string.Join(',', Enumerable.Range(1, count).Select(i => $"({i})")));
            await ExecAsync(session, $"INSERT INTO b{count} VALUES " + string.Join(',', Enumerable.Range(1, count).Select(i => $"({i})")));
            string query = $"SELECT * FROM a{count} FULL JOIN b{count} USING(id)";

            // Act / Assert
            var plain = await RowsAsync(session, "EXPLAIN " + query);
            plain.Single(row => Equals(row[2], "JoinStep"))[5]!.ToString()!.ShouldContain("FullHashJoin; unmatched=both", Case.Sensitive);
            session.LastStatementMetrics!.RecordsExamined.ShouldBe(0);
            (await RowsAsync(session, $"SELECT a.id,b.id FROM a{count} a LEFT JOIN b{count} b ON a.id + 0 = b.id")).Count.ShouldBe(count);
            session.LastStatementMetrics!.JoinCandidatePairs.ShouldBe((long)count * count);
            (await RowsAsync(session, query)).Count.ShouldBe(count);
            session.LastStatementMetrics!.JoinCandidatePairs.ShouldBe(count);
            session.LastStatementMetrics.RecordsExamined.ShouldBe(2L * count);
            var analyzed = await RowsAsync(session, "EXPLAIN ANALYZE " + query);
            Convert.ToInt64(analyzed.Single(row => Equals(row[2], "JoinStep"))[7]).ShouldBe(count);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - FULL views preserve merged expressions, domains and metadata through reopen (#1525)")]
    public async Task Open_FullView_ShouldRetainMergedMetadata()
    {
        // Arrange
        string root = Path.Combine(AppContext.BaseDirectory, "full-views-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using (var engine = SqlDatabaseEngine.Create("files", new() { RootPath = root }))
            {
                var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
                await using var session = await database.CreateSessionAsync(CancellationToken.None);
                await ExecAsync(session, "CREATE DOMAIN positive AS INT CHECK(VALUE > 0); CREATE TABLE a(id positive NOT NULL); CREATE TABLE b(id positive NOT NULL); INSERT INTO a VALUES(1); INSERT INTO b VALUES(2); CREATE VIEW v AS SELECT * FROM a FULL JOIN b USING(id);");
                database.Catalog.Views.Single(view => view.Name == "v").Columns.Single().DomainName.ShouldBe("positive");
            }

            // Act / Assert
            await using var reopened = SqlDatabaseEngine.Create("files", new() { RootPath = root });
            var next = await reopened.OpenDatabaseAsync("db", CancellationToken.None);
            await using var reader = await next.CreateSessionAsync(CancellationToken.None);
            Check(await RowsAsync(reader, "SELECT * FROM v ORDER BY id"), [[1], [2]]);
        }
        finally { if (Directory.Exists(root)) { Directory.Delete(root, true); } }
    }

    public static IEnumerable<object[]> TypedKeys() => SqlHashJoinDifferentialTests.Cases().Where(row => !(bool)row[4]).Select(row => row.Take(4).ToArray());

    [Theory(DisplayName = "Cohesion Test [Sql] - FULL typed hash equality matches two LEFT bags with duplicate and NULL keys (#1525)")]
    [MemberData(nameof(TypedKeys))]
    public async Task Join_FullTypedKeys_ShouldMatchBothLeftBags(string leftType, string rightType, object a, object b)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("full-types", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecAsync(session, $"CREATE TABLE a(id INT,k {leftType}); CREATE TABLE b(id INT,k {rightType});");
        var parameters = new Dictionary<string, object?> { ["a"] = a, ["b"] = b };
        await session.ExecuteAsync("INSERT INTO a VALUES(1,@a),(2,@a),(3,NULL)", parameters, CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO b VALUES(1,@b),(2,@b),(3,NULL)", parameters, CancellationToken.None);

        // Act
        var actual = await RowsAsync(session, "SELECT a.id,b.id FROM a FULL JOIN b ON a.k = b.k");
        var expected = await RowsAsync(session, "SELECT a.id,b.id FROM a LEFT JOIN b ON a.k = b.k");
        expected.AddRange(await RowsAsync(session, "SELECT a.id,b.id FROM b LEFT JOIN a ON a.k = b.k WHERE a.id IS NULL"));

        // Assert
        Bag(actual).ShouldBe(Bag(expected));
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

    private static string[] Bag(IEnumerable<object?[]> rows) => rows.Select(row => string.Join("|", row.Select(value => value?.ToString() ?? "<NULL>"))).Order(StringComparer.Ordinal).ToArray();

    private static void Check(IReadOnlyList<object?[]> rows, object?[][] expected)
    {
        rows.Count.ShouldBe(expected.Length);
        for (int i = 0; i < rows.Count; i++) { rows[i].ShouldBe(expected[i]); }
    }
}
