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

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed class SqlWindowFrameTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - Frames: generated ROWS bounds match an independent brute force evaluator")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(31)]
    public async Task Execute_AllFrameBounds_ShouldMatchBruteForceAsync(int count)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("window-frames", new());
        var db = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await db.CreateSessionAsync(CancellationToken.None);
        var data = Enumerable.Range(0, count).Select(i => new Item(i, i == 0 ? 99 : i % 4 == 0 ? null : i % 3,
            i % 5 == 0 ? null : i % 4, i % 7 == 0 ? null : i % 9 - 4, i % 3 == 0)).ToArray();
        await ExecuteAsync(session, "CREATE TABLE t(id INT,p INT,k INT,v INT,flag BOOLEAN);");
        if (count > 0)
        {
            await ExecuteAsync(session, "INSERT INTO t VALUES " + string.Join(",", data.Select(r =>
                $"({r.Id},{Number(r.P)},{Number(r.K)},{Number(r.V)},{(r.Flag ? "TRUE" : "FALSE")})")) + ";");
        }
        (string Sql, int Category, int Offset)[] bounds =
        [
            ("UNBOUNDED PRECEDING",0,0), ("99 PRECEDING",1,99), ("2 PRECEDING",1,2), ("0 PRECEDING",1,0),
            ("CURRENT ROW",2,0), ("0 FOLLOWING",3,0), ("2 FOLLOWING",3,2), ("99 FOLLOWING",3,99), ("UNBOUNDED FOLLOWING",4,0),
        ];

        var cases = from isDescending in new[] { false, true }
                    from nullsFirst in new[] { false, true }
                    from first in bounds.Where(b => b.Category != 4)
                    from last in bounds.Where(b => b.Category != 0 && b.Category >= first.Category)
                    select (isDescending, nullsFirst, first, last);
        foreach (var (descending, nullsFirst, first, last) in cases)
        {
            string ordering = $"k {(descending ? "DESC" : "ASC")} NULLS {(nullsFirst ? "FIRST" : "LAST")},id";
            string frame = $"PARTITION BY p ORDER BY {ordering} ROWS BETWEEN {first.Sql} AND {last.Sql}";
            // Act: several functions share this frame, while the last window has different keys.
            var rows = await RowsAsync(session, $"SELECT id,SUM(v) FILTER(WHERE flag) OVER w,COUNT(*) OVER w,COUNT(v) OVER w,MIN(v) OVER w,MAX(v) OVER w,AVG(v) OVER w,FIRST_VALUE(v) OVER w,LAST_VALUE(v) OVER w,NTH_VALUE(v,2) OVER w,SUM(v) OVER(ORDER BY id ROWS UNBOUNDED PRECEDING) FROM t WINDOW w AS({frame}) ORDER BY id;");

            // Assert: enumerate frame members directly, without the executor's clamping algorithm.
            rows.Count.ShouldBe(count);
            foreach (var row in rows)
            {
                var item = data[(int)row[0]!];
                var partition = data.Where(r => r.P == item.P).OrderBy(r => r.K, Comparer<int?>.Create(Compare)).ThenBy(r => r.Id).ToArray();
                int current = Array.IndexOf(partition, item);
                var members = partition.Where((_, index) => InFrame(index, current, first, true) && InFrame(index, current, last, false)).ToArray();
                var values = members.Where(r => r.V.HasValue).Select(r => (decimal)r.V!.Value).ToArray();
                var filtered = members.Where(r => r.Flag && r.V.HasValue).Select(r => (long)r.V!.Value).ToArray();
                row[1].ShouldBe(filtered.Length == 0 ? null : filtered.Sum(), frame);
                row[2].ShouldBe((long)members.Length, frame);
                row[3].ShouldBe((long)values.Length, frame);
                row[4].ShouldBe(values.Length == 0 ? null : (int)values.Min(), frame);
                row[5].ShouldBe(values.Length == 0 ? null : (int)values.Max(), frame);
                row[6].ShouldBe(values.Length == 0 ? null : values.Average(), frame);
                row[7].ShouldBe(members.FirstOrDefault()?.V, frame);
                row[8].ShouldBe(members.LastOrDefault()?.V, frame);
                row[9].ShouldBe(members.Length < 2 ? null : members[1].V, frame);
                var running = data.Where(r => r.Id <= item.Id && r.V.HasValue).Select(r => (long)r.V!.Value).ToArray();
                row[10].ShouldBe(running.Length == 0 ? null : running.Sum(), frame);
            }

            int Compare(int? a, int? b) => a is null ? b is null ? 0 : nullsFirst ? -1 : 1
                : b is null ? nullsFirst ? 1 : -1 : (descending ? -1 : 1) * a.Value.CompareTo(b.Value);
        }

        foreach (var (descending, nullsFirst) in new[] { (false,false), (false,true), (true,false), (true,true) })
        {
            // Act: the peer frame deliberately omits the unique tie-breaking key.
            var peers = await RowsAsync(session, $"SELECT id,SUM(v) OVER w,COUNT(*) OVER w,LAST_VALUE(v) OVER w FROM t WINDOW w AS(PARTITION BY p ORDER BY k {(descending ? "DESC" : "ASC")} NULLS {(nullsFirst ? "FIRST" : "LAST")}) ORDER BY id");
            // Assert
            foreach (var row in peers)
            {
                var item = data[(int)row[0]!];
                var members = data.Where(r => r.P == item.P && Compare(r.K,item.K) <= 0)
                    .OrderBy(r => r.K, Comparer<int?>.Create(Compare)).ToArray();
                var values = members.Where(r => r.V.HasValue).Select(r => (decimal)r.V!.Value).ToArray();
                row[1].ShouldBe(values.Length == 0 ? null : values.Sum());
                row[2].ShouldBe((long)members.Length);
                row[3].ShouldBe(members[^1].V);
            }
            int Compare(int? a, int? b) => a is null ? b is null ? 0 : nullsFirst ? -1 : 1
                : b is null ? nullsFirst ? 1 : -1 : (descending ? -1 : 1) * a.Value.CompareTo(b.Value);
        }

        static bool InFrame(int index, int current, (string Sql, int Category, int Offset) bound, bool start)
        {
            if (bound.Category is 0 or 4) { return true; }
            int boundary = current + (bound.Category == 1 ? -bound.Offset : bound.Category == 3 ? bound.Offset : 0);
            return start ? index >= boundary : index <= boundary;
        }
        static string Number(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "NULL";
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Frames: default RANGE includes peers and no ordering means the complete partition")]
    public async Task Execute_DefaultFrame_ShouldIncludeCurrentPeersAsync()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("window-default", new());
        var db = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await db.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE t(id INT,k INT,v INT);");
        await ExecuteAsync(session, "INSERT INTO t VALUES(1,NULL,3),(2,1,10),(3,1,20),(4,2,NULL),(5,2,7);");
        // Act
        var rows = await RowsAsync(session, "SELECT id,SUM(v) OVER w,LAST_VALUE(v) OVER w,SUM(v) OVER(),LAST_VALUE(v) OVER(ORDER BY id ROWS BETWEEN UNBOUNDED PRECEDING AND UNBOUNDED FOLLOWING),SUM(v) OVER(ORDER BY k RANGE BETWEEN CURRENT ROW AND UNBOUNDED FOLLOWING) FROM t WINDOW w AS(ORDER BY k) ORDER BY id;");
        // Assert
        rows[0].ShouldBe([1,3L,3,40L,7,40L]);
        rows[1].ShouldBe([2,33L,20,40L,7,37L]);
        rows[2].ShouldBe([3,33L,20,40L,7,37L]);
        rows[3].ShouldBe([4,40L,7,40L,7,7L]);
        rows[4].ShouldBe([5,40L,7,40L,7,7L]);
        (await RowsAsync(session, "SELECT NTH_VALUE(v,NULL) OVER(),NTH_VALUE(v,9223372036854775807) OVER() FROM t")).ShouldAllBe(r => r[0] == null && r[1] == null);
        var descending = await RowsAsync(session, "SELECT id,SUM(v) OVER(ORDER BY k DESC),LAST_VALUE(v) OVER(ORDER BY k DESC) FROM t ORDER BY id");
        descending[0].ShouldBe([1,40L,3]); descending[1].ShouldBe([2,37L,20]); descending[4].ShouldBe([5,7L,7]);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Frames: Boolean, statistical, string and application aggregates execute over frames")]
    public async Task Execute_AllAggregateKinds_ShouldPreserveAggregateContractsAsync()
    {
        // Arrange
        int finishes = 0;
        var builder = SqlDatabaseEngine.CreateBuilder("window-aggregate-kinds");
        builder.Functions.Add(SqlAggregateFunction.Create<long, int, long>("application_sum", () => 0, (s,v) => s + v, s => { finishes++; return s; }));
        builder.Functions.Add(SqlAggregateFunction.Create<long, int, long>("application_sum_2", () => 0, (s,v) => s + v, s => s));
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var db = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await db.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE t(id INT,v INT,b BOOLEAN,label TEXT);");
        await ExecuteAsync(session, "INSERT INTO t VALUES(1,2,TRUE,'b'),(2,4,FALSE,'a'),(3,6,NULL,NULL);");
        // Act
        var rows = await RowsAsync(session, "SELECT id,BOOL_AND(b) OVER w,BOOL_OR(b) OVER w,EVERY(b) OVER w,VAR_POP(v) OVER w,VAR_SAMP(v) OVER w,STDDEV_POP(v) OVER w,STDDEV_SAMP(v) OVER w,VARIANCE(v) OVER w,STDDEV(v) OVER w,STRING_AGG(label,',') OVER w,application_sum(v) OVER w FROM t WINDOW w AS(ORDER BY id ROWS 1 PRECEDING) ORDER BY id;");
        var ordered = await RowsAsync(session, "SELECT id,STRING_AGG(label,',' ORDER BY label) FILTER(WHERE v>0) OVER w,LISTAGG(label,',') WITHIN GROUP(ORDER BY label) OVER w FROM t WINDOW w AS(ORDER BY id ROWS UNBOUNDED PRECEDING) ORDER BY id;");
        // Assert
        rows[0].ShouldBe([1,true,true,true,0m,null,0m,null,null,null,"b",2L]);
        rows[1][1].ShouldBe(false); rows[1][2].ShouldBe(true); rows[1][3].ShouldBe(false);
        rows[1][4].ShouldBe(1m); rows[1][5].ShouldBe(2m); rows[1][6].ShouldBe(1m);
        rows[1][7].ShouldBe(rows[1][9]); rows[1][8].ShouldBe(2m);
        rows[2][1].ShouldBe(false); rows[2][2].ShouldBe(false); rows[2][10].ShouldBe("a"); rows[2][11].ShouldBe(10L);
        finishes.ShouldBe(3);
        await ExecuteAsync(session, "CREATE VIEW custom_window AS SELECT id,\"application_sum_2\"(v) OVER(ORDER BY id ROWS UNBOUNDED PRECEDING) AS s FROM t;");
        (await RowsAsync(session, "SELECT s FROM custom_window ORDER BY id")).Select(r => r[0]).ShouldBe([2L,6L,12L]);
        var refused = await session.ExecuteAsync("SELECT SUM(DISTINCT v) OVER() FROM t", cancellationToken: CancellationToken.None);
        refused.Status.ShouldBe(DatabaseResultStatus.Error);
        refused.Diagnostics.ShouldContain(d => d.Code == "CK00001");
        ordered[0].ShouldBe([1,"b","b"]); ordered[1].ShouldBe([2,"a,b","a,b"]); ordered[2].ShouldBe([3,"a,b","a,b"]);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Frames: invalid boundaries, offsets, modifiers and value arguments return stable codes")]
    [InlineData("SELECT SUM(v) OVER(ROWS UNBOUNDED FOLLOWING) FROM t", "CS05080")]
    [InlineData("SELECT SUM(v) OVER(ROWS 1 FOLLOWING) FROM t", "CS05080")]
    [InlineData("SELECT SUM(v) OVER(ROWS BETWEEN CURRENT ROW AND 1 PRECEDING) FROM t", "CS05080")]
    [InlineData("SELECT SUM(v) OVER(ROWS BETWEEN 1 FOLLOWING AND CURRENT ROW) FROM t", "CS05080")]
    [InlineData("SELECT SUM(v) OVER(ROWS BETWEEN UNBOUNDED PRECEDING AND UNBOUNDED PRECEDING) FROM t", "CS05080")]
    [InlineData("SELECT SUM(v) OVER(ROWS -1 PRECEDING) FROM t", "CS02072")]
    [InlineData("SELECT SUM(v) OVER(ROWS BETWEEN CURRENT ROW AND -1 FOLLOWING) FROM t", "CS02072")]
    [InlineData("SELECT SUM(v) OVER(ROWS NULL PRECEDING) FROM t", "CS02071")]
    [InlineData("SELECT SUM(v) OVER(ROWS BETWEEN CURRENT ROW AND NULL FOLLOWING) FROM t", "CS02071")]
    [InlineData("SELECT SUM(v) OVER(ROWS 1.5 PRECEDING) FROM t", "CS02070")]
    [InlineData("SELECT SUM(v) OVER(ROWS v PRECEDING) FROM t", "CS05081")]
    [InlineData("SELECT SUM(v) OVER(ROWS (SELECT t.v) PRECEDING) FROM t", "CS05081")]
    [InlineData("SELECT SUM(v) OVER w FROM t WINDOW w AS(ROWS (SELECT t.v) PRECEDING)", "CS05081")]
    [InlineData("UPDATE t SET v=1 RETURNING SUM(v) OVER()", "CS05076")]
    [InlineData("SELECT SUM(v) OVER(ROWS SUM(v) PRECEDING) FROM t", "CS05081")]
    [InlineData("SELECT SUM(v) OVER(ROWS RANK() OVER() PRECEDING) FROM t", "CS05077")]
    [InlineData("SELECT SUM(v) OVER w FROM t WINDOW w AS(ROWS v PRECEDING)", "CS05081")]
    [InlineData("SELECT SUM(v) OVER(w) FROM t WINDOW w AS(ROWS CURRENT ROW)", "CS01066")]
    [InlineData("SELECT 1 WINDOW w AS(ROWS CURRENT ROW),x AS(w)", "CS01066")]
    [InlineData("SELECT NTH_VALUE(v,0) OVER() FROM t", "CS05082")]
    [InlineData("SELECT NTH_VALUE(v,-1) OVER(ROWS BETWEEN 9 FOLLOWING AND 10 FOLLOWING) FROM t", "CS05082")]
    [InlineData("SELECT NTH_VALUE(v,1.5) OVER() FROM t", "CS02073")]
    [InlineData("SELECT NTH_VALUE(v) OVER() FROM t", "CS00011")]
    [InlineData("SELECT FIRST_VALUE(v,1) OVER() FROM t", "CS00011")]
    [InlineData("SELECT FIRST_VALUE(v) FILTER(WHERE TRUE) OVER() FROM t", "CS00011")]
    [InlineData("SELECT LAST_VALUE(v) FROM t", "CS05076")]
    [InlineData("SELECT LOWER('a') OVER() FROM t", "CS05083")]
    [InlineData("SELECT SUM(v) FILTER(WHERE RANK() OVER()>1) OVER() FROM t", "CS05077")]
    [InlineData("SELECT SUM(v) OVER(ROWS NEXT VALUE FOR s PRECEDING) FROM t", "CS03076")]
    public async Task Execute_InvalidFrame_ShouldReturnCodeAsync(string sql, string code)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("frame-errors", new());
        var db = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await db.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE t(v INT); INSERT INTO t VALUES(1);");
        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () => await ExecuteAsync(session, sql));
        // Assert
        error.Diagnostics.ShouldContain(d => d.Code == code);
        (await RowsAsync(session, "SELECT COUNT(*) FROM t")).ShouldHaveSingleItem()[0].ShouldBe(1L);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Frames: running native aggregates visit each input once and frames share their sort")]
    [InlineData(64, "UNBOUNDED PRECEDING")]
    [InlineData(256, "UNBOUNDED PRECEDING")]
    [InlineData(64, "65536 PRECEDING")]
    [InlineData(256, "65536 PRECEDING")]
    public async Task Execute_GrowingFrames_ShouldHaveLinearTransitionsAsync(int count, string frame)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("frame-work", new());
        var db = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await db.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE t(id INT,v INT,b BOOLEAN);");
        await ExecuteAsync(session, "INSERT INTO t VALUES " + string.Join(",", Enumerable.Range(0,count).Select(i => $"({i},1,TRUE)")));
        // Act
        var rows = await RowsAsync(session, $"EXPLAIN ANALYZE SELECT SUM(v) OVER w,COUNT(*) OVER w,MIN(v) OVER w,MAX(v) OVER w,AVG(v) OVER w,BOOL_AND(b) OVER w,VAR_POP(v) OVER w,LAST_VALUE(v) OVER(ORDER BY id ROWS CURRENT ROW) FROM t WINDOW w AS(ORDER BY id ROWS {frame}) LIMIT 1;");
        // Assert
        var stage = rows.Single(r => Equals(r[2], "WindowAgg"));
        string detail = stage[5]!.ToString()!;
        long visits = long.Parse(detail.Split("frame_visits=", StringSplitOptions.None)[1].Split(';')[0], CultureInfo.InvariantCulture);
        visits.ShouldBe(7L * count);
        long bruteForceVisits = Enumerable.Range(1,count).Sum(size => 7L * size);
        bruteForceVisits.ShouldBe(7L * count * (count + 1) / 2);
        visits.ShouldBeLessThan(bruteForceVisits);
        detail.ShouldContain($"ROWS BETWEEN {frame} AND CURRENT ROW");
        detail.ShouldContain("sort_count=1");
        stage[7].ShouldBe((long)count);
        session.LastStatementMetrics.ShouldNotBeNull().HashMemoryBytes.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Frames: frame aggregates compose with grouping, FILTER and scalar offset subqueries")]
    public async Task Execute_GroupedAndParameterizedFrames_ShouldBindInputsAsync()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("frame-grouping", new());
        var db = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await db.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE t(p INT,v INT); INSERT INTO t VALUES(1,2),(1,3),(2,7),(3,1);");
        // Act
        var rows = await RowsAsync(session, "SELECT p,SUM(SUM(v)) FILTER(WHERE SUM(v)>1) OVER w,FIRST_VALUE(SUM(v)) OVER w FROM t GROUP BY p WINDOW w AS(ORDER BY p ROWS (SELECT 1) PRECEDING) ORDER BY p;");
        var masks = await RowsAsync(session, "SELECT GROUPING(p),SUM(COUNT(*)) OVER(ORDER BY GROUPING(p),p ROWS UNBOUNDED PRECEDING) FROM t GROUP BY ROLLUP(p) ORDER BY GROUPING(p),p;");
        // Assert
        var lazy = await RowsAsync(session, "SELECT SUM((SELECT 1/0)) FILTER(WHERE FALSE) OVER(ORDER BY p ROWS UNBOUNDED PRECEDING),STRING_AGG('a',',' ORDER BY (SELECT 1/0)) FILTER(WHERE NULL) OVER() FROM t");
        lazy.ShouldAllBe(r => r[0] == null && r[1] == null);
        rows[0].ShouldBe([1,5m,5L]); rows[1].ShouldBe([2,12m,5L]); rows[2].ShouldBe([3,7m,7L]);
        masks.Select(r => r[1]).ShouldBe([2m,3m,4m,8m]);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Frames: framed named-window views preserve their definitions after catalog reload")]
    public async Task Open_FrameView_ShouldReloadCanonicalFramesAsync()
    {
        // Arrange
        string root = Path.Combine(Path.GetTempPath(), "cohesion-frame-view-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using (var engine = SqlDatabaseEngine.Create("frame-view", new() { RootPath = root }))
            {
                var db = await engine.CreateDatabaseAsync("db", CancellationToken.None);
                await using var session = await db.CreateSessionAsync(CancellationToken.None);
                await ExecuteAsync(session, "CREATE TABLE t(id INT,v INT); INSERT INTO t VALUES(1,10),(2,20),(3,NULL);");
                await ExecuteAsync(session, "CREATE VIEW v AS SELECT id,SUM(v) OVER w AS running,LAST_VALUE(v) OVER w AS last FROM t WINDOW w AS(ORDER BY id ROWS 1 PRECEDING);");
            }
            // Act
            await using var reopened = SqlDatabaseEngine.Create("frame-view", new() { RootPath = root });
            var next = await reopened.OpenDatabaseAsync("db", CancellationToken.None);
            await using var reader = await next.CreateSessionAsync(CancellationToken.None);
            var rows = await RowsAsync(reader, "SELECT id,running,last FROM v ORDER BY id;");
            // Assert
            rows[0].ShouldBe([1,10L,10]); rows[1].ShouldBe([2,30L,20]); rows[2].ShouldBe([3,20L,null]);
        }
        finally { if (Directory.Exists(root)) { Directory.Delete(root, recursive: true); } }
    }

    private sealed record Item(int Id, int? P, int? K, int? V, bool Flag);

    [Fact(DisplayName = "Cohesion Test [Sql] - Frames: value functions preserve character facets and comparison collation")]
    public async Task Execute_CharacterValues_ShouldPreserveMetadataAndCollationAsync()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("frame-character", new());
        var db = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await db.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE t(id INT,k CHAR(3) COLLATE case_insensitive); INSERT INTO t VALUES(1,'a'),(2,'A'),(3,'b');");
        await ExecuteAsync(session, "CREATE VIEW v AS SELECT id,FIRST_VALUE(k) OVER w AS first,LAST_VALUE(k) OVER w AS last,NTH_VALUE(k,1) OVER w AS nth FROM t WINDOW w AS(ORDER BY k);");
        // Act
        var rows = await RowsAsync(session, "SELECT id,first,last,nth,first='A',last='A' FROM v ORDER BY id");
        // Assert
        rows[0].ShouldBe([1,"a  ","A  ","a  ",true,true]);
        rows[1].ShouldBe([2,"a  ","A  ","a  ",true,true]);
        rows[2].ShouldBe([3,"a  ","b  ","a  ",true,false]);
        db.Catalog.Views.ShouldHaveSingleItem().Columns.Skip(1).ShouldAllBe(column => column.Type.MaxLength == 3);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Frames: offsets initialize once across partitions and preserve Int64 extremes")]
    public async Task Execute_FrameOffsets_ShouldInitializeOnceAndClampAsync()
    {
        // Arrange
        int calls = 0;
        var builder = SqlDatabaseEngine.CreateBuilder("frame-offset-once");
        builder.Functions.Add(SqlScalarFunction.Create<long,long>("observe_offset", value => { calls++; return value; }));
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var db = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await db.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE t(id INT,p INT,v INT); INSERT INTO t VALUES(1,1,10),(2,1,20),(3,2,30),(4,2,40);");
        // Act
        var request = SqlQueryRequest.FromSql("SELECT id,SUM(v) OVER w,COUNT(*) OVER w FROM t WINDOW w AS(PARTITION BY p ORDER BY id ROWS observe_offset(@n) PRECEDING) ORDER BY id", new Dictionary<string,object?> { ["n"] = 1L });
        await using var result = (await session.ExecuteAsync(request, CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();
        int rows = 0;
        await foreach (var row in result.GetRowsAsync(CancellationToken.None)) { rows++; }
        var extremes = await RowsAsync(session, "SELECT SUM(v) OVER(ROWS BETWEEN 9223372036854775807 PRECEDING AND 9223372036854775807 FOLLOWING),NTH_VALUE(v,id) OVER(ORDER BY id) FROM t ORDER BY id");
        // Assert
        calls.ShouldBe(1);
        rows.ShouldBe(4);
        extremes.Select(r => r[0]).ShouldBe([100L,100L,100L,100L]);
        extremes.Select(r => r[1]).ShouldBe([10,20,30,40]);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Frames: cancellation inside aggregate inputs releases all frame reservations")]
    public async Task Execute_CancelledAggregateFrame_ShouldReleaseGrantAsync()
    {
        // Arrange
        int calls = 0;
        using var cancellation = new CancellationTokenSource();
        var builder = SqlDatabaseEngine.CreateBuilder("frame-cancel");
        builder.Functions.Add(SqlScalarFunction.Create<int,int>("cancel_frame", value =>
        {
            if (++calls == 3) { cancellation.Cancel(); }
            return value;
        }));
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var db = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await db.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE t(v INT); INSERT INTO t VALUES(1),(2),(3),(4);");
        // Act
        var failure = await Should.ThrowAsync<OperationCanceledException>(async () =>
            await session.ExecuteAsync("SELECT SUM(cancel_frame(v)) OVER(ORDER BY v ROWS UNBOUNDED PRECEDING) FROM t", cancellationToken: cancellation.Token));
        // Assert
        calls.ShouldBe(3);
        failure.CancellationToken.ShouldBe(cancellation.Token);
        session.LastStatementMetrics.ShouldNotBeNull().HashMemoryBytes.ShouldBe(0);
        (await RowsAsync(session, "SELECT COUNT(*) FROM t")).ShouldHaveSingleItem()[0].ShouldBe(4L);
    }

    private static async Task ExecuteAsync(SqlDatabaseSession session, string sql)
    {
        foreach (string statement in sql.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        { _ = await session.ExecuteAsync(statement, cancellationToken: CancellationToken.None); }
    }

    private static async Task<List<object?[]>> RowsAsync(SqlDatabaseSession session, string sql)
    {
        await using var result = (await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();
        var rows = new List<object?[]>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None))
        { rows.Add(Enumerable.Range(0,row.FieldCount).Select(row.GetValue).ToArray()); }
        return rows;
    }
}
