using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed class SqlWindowExecutionTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - Windows: generated peer ranks equal an independent quadratic computation")]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Execute_GeneratedPeers_ShouldMatchIndependentRanksAsync(bool descending, bool nullsFirst)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("window-ranks", new());
        var db = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await db.CreateSessionAsync(CancellationToken.None);
        var data = Enumerable.Range(0, 65).Select(i => (Id: i, P: i % 5, K: i % 7 == 0 ? (int?)null : i % 4)).ToArray();
        await ExecuteAsync(session, "CREATE TABLE t(id INT,p INT,k INT);");
        await ExecuteAsync(session, "INSERT INTO t VALUES " + string.Join(",", data.Select(r => $"({r.Id},{r.P},{r.K?.ToString(CultureInfo.InvariantCulture) ?? "NULL"})")) + ";");
        string order = "k " + (descending ? "DESC" : "ASC") + (nullsFirst ? " NULLS FIRST" : " NULLS LAST");

        // Act
        var rows = await RowsAsync(session, $"SELECT id,RANK() OVER w,DENSE_RANK() OVER w,PERCENT_RANK() OVER w,CUME_DIST() OVER w,ROW_NUMBER() OVER (PARTITION BY p ORDER BY id),NTILE(3) OVER (PARTITION BY p ORDER BY id) FROM t WINDOW w AS (PARTITION BY p ORDER BY {order}) ORDER BY id;");

        // Assert
        rows.Count.ShouldBe(data.Length);
        foreach (var row in rows)
        {
            var value = data[(int)row[0]!];
            var partition = data.Where(r => r.P == value.P).ToArray();
            int earlier = partition.Count(r => Compare(r.K, value.K) < 0);
            int through = partition.Count(r => Compare(r.K, value.K) <= 0);
            row[1].ShouldBe((long)earlier + 1);
            row[2].ShouldBe((long)partition.Where(r => Compare(r.K, value.K) < 0).Select(r => r.K).Distinct().Count() + 1);
            row[3].ShouldBe((double)earlier / (partition.Length - 1));
            row[4].ShouldBe((double)through / partition.Length);
            row[5].ShouldBe((long)partition.Count(r => r.Id <= value.Id));
            var sizes = Enumerable.Range(0, 3).Select(bucket => partition.Length / 3 + (bucket < partition.Length % 3 ? 1 : 0)).ToArray();
            int index = partition.Count(r => r.Id < value.Id), tile = 0;
            while (index >= sizes[tile]) { index -= sizes[tile++]; }
            row[6].ShouldBe(tile + 1);
        }

        int Compare(int? a, int? b) => a is null ? b is null ? 0 : nullsFirst ? -1 : 1
            : b is null ? nullsFirst ? 1 : -1 : (descending ? -1 : 1) * a.Value.CompareTo(b.Value);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Windows: navigation uses per-row signed offsets, NULL and unified defaults")]
    public async Task Execute_Navigation_ShouldRespectOffsetsAndDefaultsAsync()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("window-navigation", new());
        var db = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await db.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE t(id INT,v INT,n BIGINT);");
        await ExecuteAsync(session, "INSERT INTO t VALUES(1,10,0),(2,20,-1),(3,30,99),(4,40,NULL),(5,NULL,-9223372036854775808);");

        // Act
        var rows = await RowsAsync(session, "SELECT id,LAG(v,n,1.5) OVER w,LEAD(v,n,2.5) OVER w FROM t WINDOW w AS (ORDER BY id) ORDER BY id;");

        // Assert
        object?[][] expected = [[1,10m,10m],[2,30m,10m],[3,1.5m,2.5m],[4,null,null],[5,1.5m,2.5m]];
        for (int i = 0; i < rows.Count; i++) { rows[i].ShouldBe(expected[i]); }
        (await RowsAsync(session, "SELECT LAG(1,1,'8') OVER ()"))[0][0].ShouldBe(8);
        (await RowsAsync(session, "SELECT LAG(DATE '2025-01-01',1,TIMESTAMP '2025-02-01 00:00:00') OVER()"))[0][0].ShouldBe(new DateTime(2025,2,1));
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Windows: a singleton, empty input and all-peer partitions retain their boundaries")]
    [InlineData("SELECT ROW_NUMBER() OVER (),RANK() OVER (),DENSE_RANK() OVER (),PERCENT_RANK() OVER (),CUME_DIST() OVER (),NTILE(9) OVER ()", 1)]
    [InlineData("SELECT ROW_NUMBER() OVER () FROM t WHERE false", 0)]
    public async Task Execute_PartitionEdges_ShouldReturnExpectedRowsAsync(string sql, int count)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("window-edges", new());
        var db = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await db.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE t(id INT);");
        // Act
        var rows = await RowsAsync(session, sql);
        // Assert
        rows.Count.ShouldBe(count);
        if (count > 0) { rows[0].ShouldBe([1L,1L,1L,0d,1d,1]); }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Windows: NULL partitions and collated CHAR peers retain engine comparison rules")]
    public async Task Execute_CharacterPeers_ShouldUseEngineIdentityAsync()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("window-char", new());
        var db = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await db.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE t(id INT,p INT,k CHAR(3) COLLATE case_insensitive);");
        await ExecuteAsync(session, "INSERT INTO t VALUES(1,NULL,'a'),(2,NULL,'A'),(3,NULL,'b'),(4,1,NULL);");
        // Act
        var rows = await RowsAsync(session, "SELECT id,RANK() OVER(PARTITION BY p ORDER BY k),LAG(k) OVER(PARTITION BY p ORDER BY id) FROM t ORDER BY id;");
        var grouped = await RowsAsync(session, "SELECT k,RANK() OVER(ORDER BY k) FROM t GROUP BY k ORDER BY k;");
        var defaults = await RowsAsync(session, "SELECT id,RANK() OVER(ORDER BY k),RANK() OVER(ORDER BY k DESC) FROM t ORDER BY id;");
        // Assert
        rows[0].ShouldBe([1,1L,null]); rows[1].ShouldBe([2,1L,"a  "]);
        rows[2].ShouldBe([3,3L,"A  "]); rows[3].ShouldBe([4,1L,null]);
        grouped.Select(row => row[1]).ShouldBe([1L,2L,3L]);
        defaults[0].ShouldBe([1,2L,2L]); defaults[1].ShouldBe([2,2L,2L]);
        defaults[2].ShouldBe([3,4L,1L]); defaults[3].ShouldBe([4,1L,4L]);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Windows: named inheritance, grouped inputs, CTEs, DISTINCT and ordering compose")]
    public async Task Execute_GroupedComposition_ShouldRunAfterHavingAsync()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("window-groups", new());
        var db = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await db.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE t(p INT,v INT);");
        await ExecuteAsync(session, "INSERT INTO t VALUES(1,10),(1,20),(2,40),(3,1);");
        // Act
        var rows = await RowsAsync(session, "SELECT p,SUM(v),RANK() OVER (ORDER BY SUM(v) DESC),LAG(SUM(v)) OVER (ORDER BY p) FROM t GROUP BY p HAVING SUM(v)>1 ORDER BY p;");
        var subtotal = await RowsAsync(session, "SELECT GROUPING(p),p,COUNT(*),ROW_NUMBER() OVER (ORDER BY GROUPING(p),p) FROM t GROUP BY GROUPING SETS((p),()) ORDER BY 4;");
        var nested = await RowsAsync(session, "WITH q AS (SELECT p,ROW_NUMBER() OVER b AS n FROM t WINDOW a AS (PARTITION BY p),b AS(a ORDER BY v)) SELECT DISTINCT p FROM q WHERE n=1 ORDER BY RANK() OVER (ORDER BY p DESC) LIMIT 2;");
        var namedSubtotal = await RowsAsync(session, "SELECT ROW_NUMBER() OVER w+1 FROM t GROUP BY ROLLUP(p) WINDOW w AS(ORDER BY GROUPING(p),p) ORDER BY 1;");
        var scalar = await RowsAsync(session, "SELECT p,(SELECT RANK() OVER() FROM t x WHERE x.v=t.v) FROM t WHERE v=40;");
        await ExecuteAsync(session, "CREATE TABLE u(id INT,n BIGINT);");
        await ExecuteAsync(session, "INSERT INTO u VALUES(1,0),(2,0),(3,0);");
        await ExecuteAsync(session, "UPDATE u SET n=w.n FROM (SELECT p,ROW_NUMBER() OVER(ORDER BY SUM(v)) AS n FROM t GROUP BY p) w WHERE u.id=w.p;");
        var updated = await RowsAsync(session, "SELECT id,n FROM u ORDER BY id;");
        // Assert
        rows[0].ShouldBe([1,30L,2L,null]);
        rows[1].ShouldBe([2,40L,1L,30L]);
        subtotal.Count.ShouldBe(4);
        subtotal[3].ShouldBe([1,null,4L,4L]);
        nested.Select(r => r[0]).ShouldBe([3,2]);
        namedSubtotal.Select(r => r[0]).ShouldBe([2L,3L,4L,5L]);
        scalar.ShouldHaveSingleItem().ShouldBe([2,1L]);
        updated[0].ShouldBe([1,2L]); updated[1].ShouldBe([2,3L]); updated[2].ShouldBe([3,1L]);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Windows: invalid names, copies, signatures, placement and offsets have stable codes")]
    [InlineData("SELECT RANK() OVER missing FROM t", "CS01065")]
    [InlineData("SELECT 1 WINDOW a AS(b),b AS()", "CS01065")]
    [InlineData("SELECT 1 WINDOW a AS(),a AS()", "CS01064")]
    [InlineData("SELECT RANK() OVER (a PARTITION BY id) FROM t WINDOW a AS()", "CS01066")]
    [InlineData("SELECT RANK() OVER (a ORDER BY id) FROM t WINDOW a AS(ORDER BY id)", "CS01066")]
    [InlineData("SELECT RANK(id) OVER() FROM t", "CS00011")]
    [InlineData("SELECT LAG(id,1.2) OVER() FROM t", "CS02068")]
    [InlineData("SELECT LAG(id,1,TRUE) OVER() FROM t", "CS02069")]
    [InlineData("SELECT LAG(id,0,'bad') OVER() FROM t", "CS02022")]
    [InlineData("SELECT NTILE(0) OVER() FROM t", "CS05078")]
    [InlineData("SELECT NTILE(-1) OVER() FROM t", "CS05078")]
    [InlineData("SELECT RANK() FROM t", "CS05076")]
    [InlineData("SELECT id FROM t WHERE RANK() OVER()>1", "CS05076")]
    [InlineData("SELECT id FROM t GROUP BY RANK() OVER()", "CS05076")]
    [InlineData("SELECT COUNT(*) FROM t HAVING RANK() OVER()>1", "CS05076")]
    [InlineData("SELECT t.id FROM t JOIN t x ON RANK() OVER()=1", "CS05076")]
    [InlineData("SELECT LAG(RANK() OVER()) OVER() FROM t", "CS05077")]
    [InlineData("SELECT RANK() OVER(ORDER BY RANK() OVER()) FROM t", "CS05077")]
    [InlineData("SELECT RANK() OVER w FROM t WINDOW w AS(ORDER BY RANK() OVER())", "CS05076")]
    [InlineData("SELECT SUM(RANK() OVER()) FROM t", "CS05076")]
    [InlineData("SELECT id FROM t LIMIT RANK() OVER()", "CS05076")]
    [InlineData("UPDATE t SET id=2 RETURNING RANK() OVER()", "CS05076")]
    [InlineData("SELECT LAG(NEXT VALUE FOR s) OVER() FROM t", "CS03076")]
    [InlineData("SELECT RANK() OVER(ORDER BY NEXT VALUE FOR s) FROM t", "CS03076")]
    public async Task Execute_InvalidWindows_ShouldReturnCodeAsync(string sql, string code)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("window-errors", new());
        var db = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await db.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE t(id INT);");
        await ExecuteAsync(session, "INSERT INTO t VALUES(1);");
        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () => await ExecuteAsync(session, sql));
        // Assert
        error.Diagnostics.ShouldContain(d => d.Code == code);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Windows: whole partitions survive LIMIT and NTILE evaluates once per partition")]
    public async Task Execute_NtileAndLimit_ShouldConsumeWholePartitionAsync()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("window-ntile", new());
        var db = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await db.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE t(id INT,n INT);");
        await ExecuteAsync(session, "INSERT INTO t VALUES(1,3),(2,0),(3,NULL),(4,-2),(5,9);");
        // Act
        var rows = await RowsAsync(session, "SELECT NTILE(n) OVER(ORDER BY id),CUME_DIST() OVER(ORDER BY id),PERCENT_RANK() OVER(ORDER BY id) FROM t LIMIT 1;");
        var tiles = await RowsAsync(session, "SELECT NTILE(n) OVER(ORDER BY id) FROM t ORDER BY id;");
        // Assert
        rows[0].ShouldBe([1,0.2d,0d]);
        tiles.Select(r => r[0]).ShouldBe([1,1,2,2,3]);
        (await RowsAsync(session, "SELECT NTILE(NULL) OVER() FROM t")).ShouldAllBe(r => r[0] == null);
        (await RowsAsync(session, "SELECT NTILE(n) OVER(ORDER BY id) FROM t WHERE id>=3")).ShouldAllBe(r => r[0] == null);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Windows: budget refusal releases every reservation and permits later reads")]
    public async Task Execute_BudgetRefusal_ShouldReleaseMemoryAsync()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("window-budget", new() { StatementMemoryBudget = 4096 });
        var db = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await db.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE t(id INT);");
        await ExecuteAsync(session, "INSERT INTO t VALUES " + string.Join(",", Enumerable.Range(0, 128).Select(i => $"({i})")) + ";");
        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () => await ExecuteAsync(session, "SELECT RANK() OVER(ORDER BY id) FROM t;"));
        // Assert
        error.Diagnostics.ShouldContain(d => d.Code == "CS05079");
        session.LastStatementMetrics.ShouldNotBeNull().HashMemoryBytes.ShouldBe(0);
        SqlWindowMemory.Current.ShouldBeNull();
        (await RowsAsync(session, "SELECT id FROM t LIMIT 1")).Count.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Windows: cancellation during key evaluation releases the complete grant")]
    public async Task Execute_CancelledWindowKeys_ShouldReleaseMemoryAsync()
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        int calls = 0;
        var builder = SqlDatabaseEngine.CreateBuilder("window-cancellation");
        builder.Functions.Add(SqlScalarFunction.Create<int,int>("cancel_window_key", value =>
        {
            if (++calls == 3) { cancellation.Cancel(); }
            return value;
        }));
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var db = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await db.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE t(id INT);");
        await ExecuteAsync(session, "INSERT INTO t VALUES(1),(2),(3),(4),(5);");
        // Act
        var error = await Should.ThrowAsync<OperationCanceledException>(async () => await session.ExecuteAsync(
            "SELECT RANK() OVER(ORDER BY cancel_window_key(id)) FROM t;", cancellationToken: cancellation.Token));
        // Assert
        calls.ShouldBe(3);
        error.CancellationToken.ShouldBe(cancellation.Token);
        session.LastStatementMetrics.ShouldNotBeNull().HashMemoryBytes.ShouldBe(0);
        SqlWindowMemory.Current.ShouldBeNull();
        (await RowsAsync(session, "SELECT COUNT(*) FROM t")).ShouldHaveSingleItem()[0].ShouldBe(5L);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Windows: prefix-compatible stages share one sort with deterministic work counts")]
    public async Task Execute_SortSharing_ShouldSortOnceAsync()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("window-sorts", new());
        var db = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await db.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE t(id INT,p INT,k INT);");
        await ExecuteAsync(session, "INSERT INTO t VALUES(1,1,2),(2,1,2),(3,1,3);");
        // Act
        var plan = await RowsAsync(session, "EXPLAIN ANALYZE SELECT ROW_NUMBER() OVER(PARTITION BY p ORDER BY k,id),RANK() OVER(PARTITION BY p ORDER BY k) FROM t;");
        var separate = await RowsAsync(session, "EXPLAIN ANALYZE SELECT ROW_NUMBER() OVER(PARTITION BY p ORDER BY k,id),RANK() OVER(PARTITION BY p ORDER BY id,k) FROM t;");
        var boundaries = await RowsAsync(session, "EXPLAIN ANALYZE SELECT RANK() OVER(PARTITION BY p ORDER BY k,id),RANK() OVER(PARTITION BY p,k ORDER BY id) FROM t;");
        var values = await RowsAsync(session, "SELECT RANK() OVER(PARTITION BY p ORDER BY k,id),RANK() OVER(PARTITION BY p,k ORDER BY id) FROM t ORDER BY id;");
        // Assert
        var windows = plan.Where(row => Equals(row[2], "WindowAgg")).ToArray();
        windows.Length.ShouldBe(2);
        windows[0][5]!.ToString()!.ShouldContain("functions=RANK; sort_shared=true");
        windows[1][5]!.ToString()!.ShouldContain("functions=ROW_NUMBER; sort_shared=false");
        windows[1][1].ShouldBe(windows[0][0]);
        windows.Select(row => row[5]).ShouldContain(value => value!.ToString()!.Contains("sort_shared=true", StringComparison.Ordinal));
        windows.Select(row => row[7]).ShouldBe([3L,3L]);
        windows.Sum(Sorts).ShouldBe(1);
        separate.Where(row => Equals(row[2], "WindowAgg")).Sum(Sorts).ShouldBe(2);
        boundaries.Where(row => Equals(row[2], "WindowAgg")).Sum(Sorts).ShouldBe(1);
        values[0].ShouldBe([1L,1L]); values[1].ShouldBe([2L,2L]); values[2].ShouldBe([3L,1L]);

        static int Sorts(object?[] row) => int.Parse(row[5]!.ToString()!.Split("sort_count=", StringSplitOptions.None)[1], CultureInfo.InvariantCulture);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Windows: benchmark category ranks and monthly growth execute natively")]
    public async Task Execute_BenchmarkQueries_ShouldReturnAnalyticalResultsAsync()
    {
        // Arrange: deterministic fixtures for the PostgreSql queries in benchmark SQL.md.
        await using var engine = SqlDatabaseEngine.Create("window-benchmarks", new());
        var db = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await db.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE products(category_id INT,product_id INT);");
        await ExecuteAsync(session, "CREATE TABLE order_items(product_id INT,line_total DECIMAL);");
        await ExecuteAsync(session, "INSERT INTO products VALUES(1,1),(1,2),(1,3),(1,4),(1,5),(2,6),(3,7);");
        await ExecuteAsync(session, "INSERT INTO order_items VALUES(1,60),(1,40),(2,100),(3,50),(4,40),(5,30),(6,10);");
        await ExecuteAsync(session, "CREATE TABLE orders(order_date DATE,channel TEXT,total DECIMAL);");
        await ExecuteAsync(session, "INSERT INTO orders VALUES(DATE '2025-01-01','web',10),(DATE '2025-01-12','app',20),(DATE '2025-02-01','web',50),(DATE '2025-03-01','app',40);");

        // Act
        var ranked = await RowsAsync(session, "WITH revenues AS (SELECT p.category_id, p.product_id, SUM(i.line_total) AS revenue FROM products p INNER JOIN order_items i ON p.product_id = i.product_id GROUP BY p.category_id, p.product_id), ranked AS (SELECT category_id, product_id, revenue, RANK() OVER (PARTITION BY category_id ORDER BY revenue DESC) AS revenue_rank FROM revenues) SELECT category_id, product_id, revenue, revenue_rank FROM ranked WHERE revenue_rank <= 3 ORDER BY category_id, revenue_rank, product_id;");
        var growth = await RowsAsync(session, "WITH monthly AS (SELECT CAST(EXTRACT(YEAR FROM order_date) AS INTEGER) AS year_no, CAST(EXTRACT(MONTH FROM order_date) AS INTEGER) AS month_no, SUM(total) AS revenue FROM orders WHERE order_date >= DATE '2025-01-01' AND order_date < DATE '2026-01-01' GROUP BY EXTRACT(YEAR FROM order_date), EXTRACT(MONTH FROM order_date)), growth AS (SELECT year_no, month_no, revenue, LAG(revenue) OVER (ORDER BY year_no, month_no) AS previous_revenue FROM monthly) SELECT year_no, month_no, revenue, previous_revenue, revenue - previous_revenue AS growth FROM growth ORDER BY year_no, month_no;");

        // Assert
        ranked.Count.ShouldBe(4);
        ranked[0].ShouldBe([1,1,100m,1L]); ranked[1].ShouldBe([1,2,100m,1L]);
        ranked[2].ShouldBe([1,3,50m,3L]); ranked[3].ShouldBe([2,6,10m,1L]);
        growth[0].ShouldBe([2025,1,30m,null,null]);
        growth[1].ShouldBe([2025,2,50m,30m,20m]);
        growth[2].ShouldBe([2025,3,40m,50m,-10m]);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Windows: named window views retain canonical definitions after catalog reload")]
    public async Task Open_NamedWindowView_ShouldReloadAsync()
    {
        // Arrange
        string root = Path.Combine(Path.GetTempPath(), "cohesion-window-view-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using (var engine = SqlDatabaseEngine.Create("window-views", new() { RootPath = root }))
            {
                var db = await engine.CreateDatabaseAsync("db", CancellationToken.None);
                await using var session = await db.CreateSessionAsync(CancellationToken.None);
                await ExecuteAsync(session, "CREATE TABLE t(id INT,p INT);");
                await ExecuteAsync(session, "INSERT INTO t VALUES(1,1),(2,1),(3,2);");
                await ExecuteAsync(session, "CREATE VIEW v AS SELECT id,ROW_NUMBER() OVER b AS n,LAG(id) OVER b AS previous FROM t WINDOW a AS(PARTITION BY p),b AS(a ORDER BY id);");
                await ExecuteAsync(session, "CREATE VIEW v2 AS SELECT id,SUM(id) OVER(ORDER BY p RANGE 1 PRECEDING EXCLUDE TIES) AS n,LAST_VALUE(id) IGNORE NULLS OVER(ORDER BY p GROUPS CURRENT ROW) AS last FROM t;");
            }
            // Act
            await using var reopened = SqlDatabaseEngine.Create("window-views", new() { RootPath = root });
            var next = await reopened.OpenDatabaseAsync("db", CancellationToken.None);
            await using var reader = await next.CreateSessionAsync(CancellationToken.None);
            var rows = await RowsAsync(reader, "SELECT id,n,previous FROM v ORDER BY id;");
            // Assert
            rows[0].ShouldBe([1,1L,null]); rows[1].ShouldBe([2,2L,1]); rows[2].ShouldBe([3,1L,null]);
            var advanced = await RowsAsync(reader, "SELECT id,n,last FROM v2 ORDER BY id");
            advanced[0].ShouldBe([1,1L,2]); advanced[1].ShouldBe([2,2L,2]); advanced[2].ShouldBe([3,6L,3]);
        }
        finally { if (Directory.Exists(root)) { Directory.Delete(root, recursive: true); } }
    }

    private static async Task<DatabaseResult> ExecuteAsync(SqlDatabaseSession session, string sql)
        => await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None);

    private static async Task<List<object?[]>> RowsAsync(SqlDatabaseSession session, string sql)
    {
        await using var result = (await ExecuteAsync(session, sql)).ShouldBeAssignableTo<DatabaseResultSet>();
        var rows = new List<object?[]>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None))
        {
            var values = new object?[row.FieldCount];
            for (int i = 0; i < values.Length; i++) { values[i] = row.GetValue(i); }
            rows.Add(values);
        }
        return rows;
    }
}
