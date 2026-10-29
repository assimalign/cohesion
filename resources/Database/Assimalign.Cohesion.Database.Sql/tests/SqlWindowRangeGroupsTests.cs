using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Internal;

namespace Assimalign.Cohesion.Database.Sql.Tests;

[Collection(nameof(SqlDatabaseEventSourceCollection))]
public sealed class SqlWindowRangeGroupsTests
{
    private readonly ITestOutputHelper _output;
    public SqlWindowRangeGroupsTests(ITestOutputHelper output) { _output = output; }

    [Theory(DisplayName = "Cohesion Test [Sql] - Frames: RANGE distances and exclusions match an independent membership evaluator for every key domain")]
    [InlineData("TINYINT")]
    [InlineData("SMALLINT")]
    [InlineData("INT")]
    [InlineData("BIGINT")]
    [InlineData("DECIMAL(12,2)")]
    [InlineData("REAL")]
    [InlineData("DOUBLE PRECISION")]
    [InlineData("DATE")]
    [InlineData("TIME")]
    [InlineData("TIMESTAMP")]
    [InlineData("TIMESTAMPTZ")]
    [InlineData("INTERVAL")]
    [InlineData("GROUPS")]
    [InlineData("ROWS")]
    public async Task Execute_AllFrameBounds_ShouldMatchMembershipOracleAsync(string type)
    {
        // Arrange: two partitions, ties, NULLs, and a singleton. The oracle operates in
        // abstract distance units, independently of runtime boundary calculations.
        int?[] keys = [null,-3,-1,-1,0,3,7,null,2];
        bool temporal = type is "DATE" or "TIME" or "TIMESTAMP" or "TIMESTAMPTZ" or "INTERVAL";
        string keyType = type is "GROUPS" or "ROWS" ? "INT" : type;
        string mode = type is "GROUPS" or "ROWS" ? type : "RANGE";
        await using var engine = SqlDatabaseEngine.Create("window-range-oracle", new());
        var db = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await db.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, $"CREATE TABLE t(id INT,p INT,k {keyType},v INT)");
        await ExecuteAsync(session, "INSERT INTO t VALUES " + string.Join(",", keys.Select((k,i) => $"({i},{(i==8 ? 1 : 0)},{Literal(k)}, {(i%3==0 ? "NULL" : (i+1).ToString(CultureInfo.InvariantCulture))})")));
        (int Kind,int Offset)[] bounds = [(0,0),(1,0),(1,1),(1,3),(2,0),(3,0),(3,1),(3,3),(4,0)];
        int cases = 0;
        // Act / Assert
        foreach (bool descending in new[] { false,true })
        {
            foreach (bool nullsFirst in new[] { false,true })
            {
                foreach (var first in bounds.Where(b => b.Kind != 4))
                {
                    foreach (var last in bounds.Where(b => b.Kind != 0 && b.Kind >= first.Kind))
                    {
                        foreach (string exclusion in new[] { "NO OTHERS","CURRENT ROW","GROUP","TIES" })
                        {
                            string frame = $"{mode} BETWEEN {Bound(first)} AND {Bound(last)} EXCLUDE {exclusion}";
                            string query = $"SELECT id,COUNT(*) OVER w,SUM(v) OVER w,MIN(v) OVER w,MAX(v) OVER w,FIRST_VALUE(id) OVER w,LAST_VALUE(id) OVER w,NTH_VALUE(id,2) OVER w,STRING_AGG(CAST(id AS TEXT),',' ORDER BY id) OVER w FROM t WINDOW w AS(PARTITION BY p ORDER BY k {(descending ? "DESC" : "ASC")} NULLS {(nullsFirst ? "FIRST" : "LAST")} {frame}) ORDER BY id";
                            var actual = await RowsAsync(session, query);
                            foreach (int partition in new[] {0,1})
                            {
                                int[] sorted = Enumerable.Range(0,keys.Length).Where(i => (i==8 ? 1 : 0)==partition).OrderBy(i => i, Comparer<int>.Create((a,b) => Compare(a,b))).ToArray();
                                var peers = sorted.Select(i => keys[i]).Distinct().ToArray();
                                for (int position = 0; position < sorted.Length; position++)
                                {
                                    int id = sorted[position];
                                    int group = Array.IndexOf(peers,keys[id]);
                                    var members = Enumerable.Range(0,sorted.Length).Where(j => Included(first,j,false) && Included(last,j,true))
                                        .Where(j => exclusion == "NO OTHERS" || exclusion == "CURRENT ROW" && j != position ||
                                            exclusion == "GROUP" && keys[sorted[j]] != keys[id] || exclusion == "TIES" && (j==position || keys[sorted[j]] != keys[id]))
                                        .Select(j => sorted[j]).ToArray();
                                    var values = members.Where(i => i%3!=0).Select(i => i+1).ToArray();
                                    object?[] expected = [id,(long)members.Length,values.Length==0 ? null : (object)(long)values.Sum(),
                                        values.Length==0 ? null : values.Min(),values.Length==0 ? null : values.Max(),
                                        members.Length==0 ? null : members[0],members.Length==0 ? null : members[^1],members.Length<2 ? null : members[1],
                                        members.Length==0 ? null : string.Join(",",members.Order())];
                                    actual[id].ShouldBe(expected, query + $"; id={id}");

                                    bool Included((int Kind,int Offset) bound, int at, bool ending)
                                    {
                                        if (bound.Kind==0 || bound.Kind==4) { return true; }
                                        int direction = bound.Kind==1 ? -1 : bound.Kind==3 ? 1 : 0;
                                        if (mode == "ROWS") { return ending ? at <= position + direction*bound.Offset : at >= position + direction*bound.Offset; }
                                        if (mode == "GROUPS")
                                        { int other = Array.IndexOf(peers,keys[sorted[at]]); return ending ? other <= group+direction*bound.Offset : other >= group+direction*bound.Offset; }
                                        if (bound.Kind==2 || keys[id] is null)
                                        { return ending ? Compare(sorted[at],id) <= 0 : Compare(sorted[at],id) >= 0; }
                                        if (keys[sorted[at]] is null) { return ending ? nullsFirst : !nullsFirst; }
                                        int boundary = keys[id]!.Value + direction*(descending ? -1 : 1)*bound.Offset;
                                        int comparison = keys[sorted[at]]!.Value.CompareTo(boundary)*(descending ? -1 : 1);
                                        return ending ? comparison<=0 : comparison>=0;
                                    }
                                }
                                int Compare(int a, int b) => keys[a] is null ? keys[b] is null ? 0 : nullsFirst ? -1 : 1
                                    : keys[b] is null ? nullsFirst ? 1 : -1 : keys[a]!.Value.CompareTo(keys[b]!.Value)*(descending ? -1 : 1);
                            }
                            session.LastStatementMetrics.ShouldNotBeNull().HashMemoryBytes.ShouldBe(0);
                            cases++;
                        }
                    }
                }
            }
        }
        _output.WriteLine($"{type}: {cases} frame/direction/NULL/exclusion cases.");

        string Bound((int Kind,int Offset) bound) => bound.Kind switch
        {
            0 => "UNBOUNDED PRECEDING",4 => "UNBOUNDED FOLLOWING",2 => "CURRENT ROW",
            _ => (temporal ? $"CAST('{bound.Offset} {(type=="TIME" ? "hours" : "days")}' AS INTERVAL)" : bound.Offset.ToString(CultureInfo.InvariantCulture)) + (bound.Kind==1 ? " PRECEDING" : " FOLLOWING"),
        };
        string Literal(int? key)
        {
            if (key is null) { return "NULL"; }
            int n = key.Value;
            string text = type switch
            {
                "DATE" => $"2024-01-{10+n:00}","TIME" => $"{10+n:00}:00:00",
                "TIMESTAMP" => $"2024-01-{10+n:00} 12:00:00", "TIMESTAMPTZ" => $"2024-01-{10+n:00} 12:00:00+02:00",
                "INTERVAL" => $"{n} days",_ => n.ToString(CultureInfo.InvariantCulture),
            };
            return temporal ? $"CAST('{text}' AS {type})" : text;
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Frames: invalid distance domains, ordering and NULL treatment have specific codes")]
    [InlineData("ORDER BY id,id RANGE 1 PRECEDING", "CS05085")]
    [InlineData("RANGE 1 PRECEDING", "CS05085")]
    [InlineData("GROUPS CURRENT ROW", "CS05086")]
    [InlineData("ORDER BY CAST(id AS TEXT) RANGE 1 PRECEDING", "CS02074")]
    [InlineData("ORDER BY id RANGE 1.5 PRECEDING", "CS02075")]
    [InlineData("ORDER BY id RANGE CAST('1 day' AS INTERVAL) PRECEDING", "CS02075")]
    [InlineData("ORDER BY id RANGE -1 PRECEDING", "CS02076")]
    [InlineData("ORDER BY id RANGE NULL PRECEDING", "CS02071")]
    [InlineData("ORDER BY id GROUPS -1 PRECEDING", "CS02072")]
    [InlineData("ORDER BY id GROUPS 1.5 PRECEDING", "CS02070")]
    public async Task Execute_InvalidFrame_ShouldRefuseAndReleaseAsync(string frame, string code)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("window-range-errors", new());
        var db = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await db.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE t(id INT)"); await ExecuteAsync(session, "INSERT INTO t VALUES(1)");
        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(() => ExecuteAsync(session,$"SELECT SUM(id) OVER({frame}) FROM t"));
        // Assert
        failure.Diagnostics.ShouldContain(d => d.Code==code);
        session.LastStatementMetrics.ShouldNotBeNull().HashMemoryBytes.ShouldBe(0);
        SqlWindowMemory.Current.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Frames: NULL treatment, infinities, exact overflow boundaries and empty inputs retain their semantics")]
    public async Task Execute_SpecialValues_ShouldPreserveDomainSemanticsAsync()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("window-range-special", new());
        var db = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await db.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session,"CREATE TABLE t(id INT,v INT)");
        await ExecuteAsync(session,"INSERT INTO t VALUES(1,NULL),(2,20),(3,NULL),(4,40),(5,NULL)");
        // Act
        var rows = await RowsAsync(session,"SELECT LAG(v,1,-1) IGNORE NULLS OVER w,LEAD(v,-1,-1) IGNORE NULLS OVER w,LAG(v,0) IGNORE NULLS OVER w,FIRST_VALUE(v) IGNORE NULLS OVER w,LAST_VALUE(v) IGNORE NULLS OVER w,NTH_VALUE(v,2) IGNORE NULLS OVER w FROM t WINDOW w AS(ORDER BY id ROWS BETWEEN UNBOUNDED PRECEDING AND UNBOUNDED FOLLOWING EXCLUDE CURRENT ROW) ORDER BY id");
        // Assert
        rows.Select(r=>r[0]).ShouldBe(new object?[]{-1,-1,20,20,40});
        rows.Select(r=>r[1]).ShouldBe(rows.Select(r=>r[0]));
        rows.Select(r=>r[2]).ShouldBe(new object?[]{null,20,null,40,null});
        rows.Select(r=>r[3]).ShouldBe(new object?[]{20,40,20,20,20});
        rows.Select(r=>r[4]).ShouldBe(new object?[]{40,40,40,20,40});
        rows.Select(r=>r[5]).ShouldBe(new object?[]{40,null,40,null,40});
        (await RowsAsync(session,"SELECT COUNT(*) OVER(ROWS 1 PRECEDING EXCLUDE GROUP),COUNT(*) OVER(ROWS 1 PRECEDING EXCLUDE TIES) FROM t ORDER BY id")).ShouldAllBe(row=>Equals(row[0],0L) && Equals(row[1],1L));
        foreach (string sql in new[]{"SELECT RANK() IGNORE NULLS OVER()", "SELECT SUM(id) RESPECT NULLS OVER() FROM t"})
        { (await Should.ThrowAsync<DatabaseException>(()=>ExecuteAsync(session,sql))).Diagnostics.ShouldContain(d=>d.Code=="CS05087"); }
        await ExecuteAsync(session,"CREATE TABLE f(id INT,k DOUBLE PRECISION)");
        await ExecuteAsync(session,"INSERT INTO f VALUES(1,CAST('-Infinity' AS DOUBLE)),(2,0),(3,CAST('Infinity' AS DOUBLE)),(4,CAST('NaN' AS DOUBLE)),(5,NULL)");
        (await RowsAsync(session,"SELECT COUNT(*) OVER(ORDER BY k NULLS LAST RANGE 0 PRECEDING) FROM f ORDER BY id")).Select(r=>r[0]).ShouldBe([1L,1L,1L,1L,1L]);
        (await RowsAsync(session,"SELECT COUNT(*) OVER(ORDER BY k NULLS LAST RANGE BETWEEN CAST('Infinity' AS DOUBLE) PRECEDING AND CURRENT ROW) FROM f ORDER BY id")).Select(r=>r[0]).ShouldBe([1L,2L,3L,1L,1L]);
        (await Should.ThrowAsync<DatabaseException>(()=>ExecuteAsync(session,"SELECT COUNT(*) OVER(ORDER BY k RANGE CAST('NaN' AS DOUBLE) PRECEDING) FROM f"))).Diagnostics.ShouldContain(d=>d.Code=="CS02076");
        foreach (string direction in new[]{"ASC","DESC"})
        {
            foreach (string nulls in new[]{"FIRST","LAST"})
            {
                (await RowsAsync(session,$"SELECT COUNT(*) OVER(ORDER BY k {direction} NULLS {nulls} RANGE BETWEEN CAST('Infinity' AS DOUBLE) PRECEDING AND CAST('Infinity' AS DOUBLE) FOLLOWING) FROM f ORDER BY id")).Select(r=>r[0]).ShouldBe([3L,3L,3L,1L,1L]);
            }
        }
        (await RowsAsync(session,"SELECT COUNT(*) OVER(ORDER BY CAST(CASE WHEN id=1 THEN '00:00' WHEN id=2 THEN '01:00' ELSE '23:00' END AS TIME) RANGE BETWEEN CAST('2 hours' AS INTERVAL) PRECEDING AND CAST('2 hours' AS INTERVAL) FOLLOWING) FROM t WHERE id<4 ORDER BY id")).Select(r=>r[0]).ShouldBe([2L,2L,1L]);
        (await Should.ThrowAsync<DatabaseException>(()=>ExecuteAsync(session,"SELECT COUNT(*) OVER(ORDER BY CAST('9999-12-31' AS DATE) RANGE BETWEEN CURRENT ROW AND CAST('1 day' AS INTERVAL) FOLLOWING)"))).Diagnostics.ShouldContain(d=>d.Code=="CS02048");
        // Empty input must not initialize a NULL offset.
        (await RowsAsync(session,"SELECT COUNT(*) OVER(ORDER BY id RANGE NULL PRECEDING) FROM t WHERE FALSE")).ShouldBeEmpty();
        await ExecuteAsync(session,"CREATE TABLE big(k BIGINT)"); await ExecuteAsync(session,"INSERT INTO big VALUES(-9223372036854775808),(0),(9223372036854775807)");
        (await RowsAsync(session,"SELECT COUNT(*) OVER(ORDER BY k RANGE BETWEEN 9223372036854775807 PRECEDING AND 9223372036854775807 FOLLOWING) FROM big ORDER BY k")).Select(r=>r[0]).ShouldBe([1L,2L,2L]);
    }

    internal static async Task ExecuteAsync(SqlDatabaseSession session,string sql)
        => await session.ExecuteAsync(sql,cancellationToken:CancellationToken.None);
    internal static async Task<List<object?[]>> RowsAsync(SqlDatabaseSession session,string sql)
    {
        await using var result=(await session.ExecuteAsync(sql,cancellationToken:CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();
        var rows=new List<object?[]>();
        await foreach(var row in result.GetRowsAsync(CancellationToken.None)) { rows.Add(Enumerable.Range(0,row.FieldCount).Select(row.GetValue).ToArray()); }
        return rows;
    }
}
