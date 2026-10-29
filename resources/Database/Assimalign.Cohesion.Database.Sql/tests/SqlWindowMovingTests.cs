using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Sql.Internal;

namespace Assimalign.Cohesion.Database.Sql.Tests;

[Collection(nameof(SqlDatabaseEventSourceCollection))]
public sealed class SqlWindowMovingTests
{
    private readonly ITestOutputHelper _output;
    public SqlWindowMovingTests(ITestOutputHelper output) { _output = output; }

    [Theory(DisplayName = "Cohesion Test [Sql] - Moving frames: native transitions match ordinary recomputation, including FILTER and exclusion")]
    [InlineData("ROWS BETWEEN 3 PRECEDING AND 2 FOLLOWING")]
    [InlineData("RANGE BETWEEN 2 PRECEDING AND 1 FOLLOWING")]
    [InlineData("GROUPS BETWEEN 1 PRECEDING AND 2 FOLLOWING")]
    public async Task Execute_NativeMovingFrames_ShouldMatchRecomputationAsync(string frame)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("window-moving-values", new());
        var db = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await db.CreateSessionAsync(CancellationToken.None);
        await SqlWindowRangeGroupsTests.ExecuteAsync(session,"CREATE TABLE t(id INT,k INT,v DECIMAL(20,10),f DOUBLE PRECISION,b BOOLEAN)");
        await SqlWindowRangeGroupsTests.ExecuteAsync(session,"INSERT INTO t VALUES " + string.Join(",",Enumerable.Range(0,24).Select(i=>$"({i},{i/3},{(i%5==0 ? "NULL" : ((i*i-30)/10m).ToString(CultureInfo.InvariantCulture))},{(i%5==0 ? "NULL" : ((i*i-30)/10d).ToString(CultureInfo.InvariantCulture))},{(i%5==0 ? "NULL" : i%2==0 ? "TRUE" : "FALSE")})")));
        string[] calls = ["SUM(v)","AVG(v)","COUNT(*)","COUNT(v)","MIN(v)","MAX(v)","BOOL_AND(b)","BOOL_OR(b)","EVERY(b)","VAR_POP(v)","VAR_SAMP(v)","VARIANCE(v)","STDDEV_POP(v)","STDDEV_SAMP(v)","STDDEV(v)","VAR_POP(f)","VAR_SAMP(f)","STDDEV_POP(f)"];
        // Act / Assert: use an order-sensitive aggregate to enumerate membership, then
        // execute an ordinary aggregate query on those members as a separate reference.
        foreach(string exclusion in new[]{"NO OTHERS","CURRENT ROW","GROUP","TIES"})
        {
            string window=$"ORDER BY k {frame} EXCLUDE {exclusion}";
            var members=await SqlWindowRangeGroupsTests.RowsAsync(session,$"SELECT STRING_AGG(CAST(id AS TEXT),',' ORDER BY id) OVER({window}) FROM t ORDER BY id");
            var actual=await SqlWindowRangeGroupsTests.RowsAsync(session,$"SELECT {string.Join(",",calls.Select(call=>$"{call} FILTER(WHERE id%4<>0) OVER({window})"))} FROM t ORDER BY id");
            for(int i=0;i<actual.Count;i++)
            {
                string predicate=members[i][0] is string ids ? $"id IN({ids}) AND id%4<>0" : "FALSE";
                var expected=(await SqlWindowRangeGroupsTests.RowsAsync(session,$"SELECT {string.Join(",",calls)} FROM t WHERE {predicate}")).Single();
                for(int column=0;column<calls.Length;column++)
                {
                    if(expected[column] is double number) { ((double)actual[i][column]!).ShouldBe(number,Math.Max(1,Math.Abs(number))*1e-10); }
                    else
                    {
                        actual[i][column].ShouldBe(expected[column],$"{window}; row={i}; {calls[column]}");
                        if (expected[column] is decimal numberExact)
                        { decimal.GetBits((decimal)actual[i][column]!).ShouldBe(decimal.GetBits(numberExact),calls[column]); }
                    }
                }
            }
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Moving frames: 10,000-row aggregates have linear input, transition and boundary counters")]
    [InlineData("ROWS BETWEEN 99 PRECEDING AND CURRENT ROW",10000,9900)]
    [InlineData("ROWS BETWEEN CURRENT ROW AND UNBOUNDED FOLLOWING",10000,9999)]
    [InlineData("RANGE BETWEEN 99 PRECEDING AND CURRENT ROW",10000,9900)]
    [InlineData("GROUPS BETWEEN 99 PRECEDING AND CURRENT ROW",10000,9900)]
    public async Task Execute_LargeMovingAverage_ShouldUseInverseTransitionsAsync(string frame,int adds,int removes)
    {
        // Arrange
        int filters=0, arguments=0;
        var builder=SqlDatabaseEngine.CreateBuilder("window-moving-work");
        builder.Functions.Add(SqlScalarFunction.Create<int,bool>("observe_filter",value=>{filters++;return value%2==0;}));
        builder.Functions.Add(SqlScalarFunction.Create<int,int>("observe_input",value=>{arguments++;return value;}));
        await using var engine=await builder.BuildAsync(CancellationToken.None);
        var db=await engine.CreateDatabaseAsync("db",CancellationToken.None);
        await using var session=await db.CreateSessionAsync(CancellationToken.None);
        await SqlWindowRangeGroupsTests.ExecuteAsync(session,"CREATE TABLE t(id INT,v INT)");
        await SqlWindowRangeGroupsTests.ExecuteAsync(session,"INSERT INTO t VALUES "+string.Join(",",Enumerable.Range(0,10000).Select(i=>$"({i},1)")));
        // Act
        var rows=await SqlWindowRangeGroupsTests.RowsAsync(session,$"EXPLAIN ANALYZE SELECT AVG(observe_input(v)) FILTER(WHERE observe_filter(id)) OVER(ORDER BY id {frame}) FROM t LIMIT 1");
        // Assert
        string detail=rows.Single(r=>Equals(r[2],"WindowAgg"))[5]!.ToString()!;
        _output.WriteLine(detail);
        detail.ShouldContain($"frame_visits=10000; transitions={adds}; inverse_transitions={removes};",Case.Sensitive);
        long boundaries=long.Parse(detail.Split("boundary_visits=",StringSplitOptions.None)[1].Split(';')[0],CultureInfo.InvariantCulture);
        boundaries.ShouldBeLessThanOrEqualTo(40000);
        filters.ShouldBe(10000); arguments.ShouldBe(5000);
        session.LastStatementMetrics.ShouldNotBeNull().HashMemoryBytes.ShouldBe(0);
        SqlWindowMemory.Current.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Moving frames: floating inverse state recovers after masked extremes leave the frame")]
    public async Task Execute_MaskedFloatingExtremes_ShouldRestoreFiniteStatisticsAsync()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("window-floating-inverse",new());
        var db = await engine.CreateDatabaseAsync("db",CancellationToken.None);
        await using var session = await db.CreateSessionAsync(CancellationToken.None);
        await SqlWindowRangeGroupsTests.ExecuteAsync(session,"CREATE TABLE t(id INT,k INT,v DOUBLE PRECISION)");
        await SqlWindowRangeGroupsTests.ExecuteAsync(session,"INSERT INTO t VALUES(1,0,CAST('Infinity' AS DOUBLE)),(2,0,CAST('1e308' AS DOUBLE)),(3,1,CAST('-1e308' AS DOUBLE)),(4,2,NULL)");
        // Act
        var rows = await SqlWindowRangeGroupsTests.RowsAsync(session,"SELECT VAR_POP(v) OVER(ORDER BY k GROUPS 1 PRECEDING) FROM t ORDER BY id");
        // Assert
        rows.Take(3).ShouldAllBe(row=>row[0] is double && double.IsNaN((double)row[0]!));
        rows[3][0].ShouldBe(0d);
        await SqlWindowRangeGroupsTests.ExecuteAsync(session,"UPDATE t SET v=CAST('1e308' AS DOUBLE) WHERE id=1");
        (await Should.ThrowAsync<DatabaseException>(()=>SqlWindowRangeGroupsTests.ExecuteAsync(session,"SELECT VAR_POP(v) OVER(ORDER BY id ROWS 1 PRECEDING) FROM t"))).Diagnostics.ShouldContain(d=>d.Code=="CS02002");
        session.LastStatementMetrics.ShouldNotBeNull().HashMemoryBytes.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Moving frames: decimal inverse transitions do not lose low-order terms")]
    public async Task Execute_DecimalCancellation_ShouldRemainExactAsync()
    {
        // Arrange
        await using var engine=SqlDatabaseEngine.Create("window-moving-exact",new());
        var db=await engine.CreateDatabaseAsync("db",CancellationToken.None);
        await using var session=await db.CreateSessionAsync(CancellationToken.None);
        await SqlWindowRangeGroupsTests.ExecuteAsync(session,"CREATE TABLE t(id INT,v DECIMAL)");
        await SqlWindowRangeGroupsTests.ExecuteAsync(session,"INSERT INTO t VALUES(1,79228162514264337593543950335.0),(2,0.0000000000000000000000000001),(3,-79228162514264337593543950335.0),(4,2)");
        // Act
        var rows=await SqlWindowRangeGroupsTests.RowsAsync(session,"SELECT SUM(v) OVER(ORDER BY id ROWS 2 PRECEDING) FROM t ORDER BY id");
        // Assert
        rows[2][0].ShouldBe(0.0000000000000000000000000001m);
        rows[3][0].ShouldBe(-79228162514264337593543950333m);
    }
}
