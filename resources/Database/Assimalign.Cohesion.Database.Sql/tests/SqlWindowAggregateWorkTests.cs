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

public sealed class SqlWindowAggregateWorkTests
{
    private readonly ITestOutputHelper _output;

    public SqlWindowAggregateWorkTests(ITestOutputHelper output) { _output = output; }

    [Fact(DisplayName = "Cohesion Test [Sql] - Window work: ordered SUM uses one transition visit per row under LIMIT")]
    public async Task Execute_OrderedGrowingSum_ShouldVisitEachRowOnceAsync()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("window-ordered-work", new());
        var db = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await db.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session, 10000);

        // Act
        var rows = await RowsAsync(session, "EXPLAIN ANALYZE SELECT SUM(v ORDER BY id DESC) FILTER(WHERE id%2=0) OVER(ORDER BY id ROWS UNBOUNDED PRECEDING) FROM t LIMIT 1");

        // Assert
        string detail = rows.Single(r => Equals(r[2], "WindowAgg"))[5]!.ToString()!;
        long visits = long.Parse(detail.Split("frame_visits=", StringSplitOptions.None)[1].Split(';')[0], CultureInfo.InvariantCulture);
        _output.WriteLine($"Ordered SUM frame visits: {visits}.");
        visits.ShouldBe(10000);
        session.LastStatementMetrics.ShouldNotBeNull().HashMemoryBytes.ShouldBe(0);
        var values = await RowsAsync(session, "SELECT id,SUM(v ORDER BY id DESC) FILTER(WHERE id%2=0) OVER(ORDER BY id ROWS UNBOUNDED PRECEDING) FROM t ORDER BY id DESC LIMIT 1");
        values.ShouldHaveSingleItem().ShouldBe([9999,5000L]);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Window work: recomputed frames refuse at the statement visit cap")]
    [InlineData("application_sum(v)", 1000000, 1413, 0)]
    [InlineData("application_sum(v) FILTER(WHERE observe_filter(id))", 0, 0, 1000000)]
    [InlineData("application_sum(v ORDER BY id DESC)", 998991, 1413, 0)]
    public async Task Execute_LargeApplicationFrames_ShouldRefuseAtWorkCapAsync(string aggregate, int expectedTransitions, int expectedFinishes, int expectedFilters)
    {
        // Arrange
        int transitions = 0, finishes = 0, filters = 0;
        var builder = SqlDatabaseEngine.CreateBuilder("window-capped-work");
        builder.Functions.Add(SqlScalarFunction.Create<int,bool>("observe_filter", value => { filters++; return false; }));
        builder.Functions.Add(SqlAggregateFunction.Create<long,int,long>("application_sum", () => 0, (s,v) => { transitions++; return s+v; }, s => { finishes++; return s; }));
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var db = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await db.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session, 1414);

        // Act
        DatabaseException? error = null;
        try { await session.ExecuteAsync($"SELECT {aggregate} OVER(ORDER BY id ROWS UNBOUNDED PRECEDING) FROM t LIMIT 1", cancellationToken: CancellationToken.None); }
        catch (DatabaseException failure) { error = failure; }

        // Assert
        _output.WriteLine($"Application transitions: {transitions}; finishes: {finishes}; filter calls: {filters}.");
        error.ShouldNotBeNull().Diagnostics.ShouldContain(d => d.Code == "CS05084");
        transitions.ShouldBe(expectedTransitions);
        finishes.ShouldBe(expectedFinishes);
        filters.ShouldBe(expectedFilters);
        session.LastStatementMetrics.ShouldNotBeNull().HashMemoryBytes.ShouldBe(0);
        SqlWindowMemory.Current.ShouldBeNull();
        (await RowsAsync(session, "SELECT COUNT(*) FROM t")).ShouldHaveSingleItem()[0].ShouldBe(1414L);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Window work: small application frames preserve once-only finalization")]
    public async Task Execute_SmallApplicationFrames_ShouldSucceedAsync()
    {
        // Arrange
        int transitions = 0, finishes = 0;
        var builder = SqlDatabaseEngine.CreateBuilder("window-small-work");
        builder.Functions.Add(SqlAggregateFunction.Create<long,int,long>("application_sum", () => 0, (s,v) => { transitions++; return s+v; }, s => { finishes++; return s; }));
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var db = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await db.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session, 8);

        // Act
        var rows = await RowsAsync(session, "SELECT application_sum(v) OVER(ORDER BY id ROWS UNBOUNDED PRECEDING) FROM t ORDER BY id");

        // Assert
        rows.Select(r => r[0]).ShouldBe(Enumerable.Range(1,8).Select(i => (object)(long)i));
        transitions.ShouldBe(36);
        finishes.ShouldBe(8);
        session.LastStatementMetrics.ShouldNotBeNull().HashMemoryBytes.ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Window work: order-insensitive native aggregates ignore local ordering and preserve FILTER results")]
    [InlineData("SUM", "v")]
    [InlineData("COUNT", "v")]
    [InlineData("MIN", "v")]
    [InlineData("MAX", "v")]
    [InlineData("AVG", "v")]
    [InlineData("BOOL_AND", "v>0")]
    [InlineData("BOOL_OR", "v>0")]
    [InlineData("EVERY", "v>0")]
    [InlineData("VAR_POP", "v")]
    [InlineData("VAR_SAMP", "v")]
    [InlineData("VARIANCE", "v")]
    [InlineData("STDDEV_POP", "v")]
    [InlineData("STDDEV_SAMP", "v")]
    [InlineData("STDDEV", "v")]
    public async Task Execute_OrderedNativeAggregates_ShouldMatchUnorderedFramesAsync(string name, string argument)
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("window-native-order", new());
        var db = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await db.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t(id INT,v INT)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO t VALUES(1,NULL),(2,4),(3,-1),(4,0),(5,9),(6,NULL),(7,2),(8,7)", cancellationToken: CancellationToken.None);
        string query = $"SELECT {name}({argument} ORDER BY id DESC) FILTER(WHERE id%2=0) OVER w,{name}({argument}) FILTER(WHERE id%2=0) OVER w FROM t WINDOW w AS(ORDER BY id ROWS UNBOUNDED PRECEDING) ORDER BY id";

        // Act
        var rows = await RowsAsync(session, query);
        var analysis = await RowsAsync(session, "EXPLAIN ANALYZE " + query);

        // Assert
        rows.Count.ShouldBe(8);
        rows.ShouldAllBe(row => Equals(row[0], row[1]));
        string detail = analysis.Single(r => Equals(r[2], "WindowAgg"))[5]!.ToString()!;
        detail.ShouldContain("frame_visits=16;", Case.Sensitive);
        session.LastStatementMetrics.ShouldNotBeNull().HashMemoryBytes.ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Window work: window groups and stages share one statement recomputation cap")]
    [InlineData("SELECT application_sum(v) OVER(ORDER BY id ROWS UNBOUNDED PRECEDING),application_sum(v) OVER(ORDER BY id DESC ROWS UNBOUNDED PRECEDING) FROM t LIMIT 1")]
    [InlineData("SELECT application_sum(v) OVER(ORDER BY id ROWS UNBOUNDED PRECEDING) FROM t UNION ALL SELECT application_sum(v) OVER(ORDER BY id ROWS UNBOUNDED PRECEDING) FROM t")]
    public async Task Execute_MultipleWindowGroups_ShouldShareWorkCapAsync(string sql)
    {
        // Arrange
        int transitions = 0, finishes = 0;
        var builder = SqlDatabaseEngine.CreateBuilder("window-shared-work");
        builder.Functions.Add(SqlAggregateFunction.Create<long,int,long>("application_sum", () => 0, (s,v) => { transitions++; return s+v; }, s => { finishes++; return s; }));
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var db = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await db.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session, 1000);

        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(sql, cancellationToken: CancellationToken.None));

        // Assert
        error.Diagnostics.ShouldContain(d => d.Code == "CS05084");
        transitions.ShouldBe(1000000);
        finishes.ShouldBe(1999);
        session.LastStatementMetrics.ShouldNotBeNull().HashMemoryBytes.ShouldBe(0);
        SqlWindowMemory.Current.ShouldBeNull();
        (await RowsAsync(session, "SELECT application_sum(v) OVER(ORDER BY id ROWS UNBOUNDED PRECEDING) FROM t WHERE id<4 ORDER BY id")).Select(r => r[0]).ShouldBe([1L,2L,3L,4L]);
        transitions.ShouldBe(1000010);
        finishes.ShouldBe(2003);
    }

    private static async Task SeedAsync(SqlDatabaseSession session, int count)
    {
        await session.ExecuteAsync("CREATE TABLE t(id INT,v INT)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO t VALUES " + string.Join(",", Enumerable.Range(0,count).Select(i => $"({i},1)")), cancellationToken: CancellationToken.None);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Window work: ordered strings and moving native frames obey the recomputation cap")]
    [InlineData("STRING_AGG('x',',' ORDER BY observe_input(id) DESC) OVER(ORDER BY id ROWS UNBOUNDED PRECEDING)", 1414, 1000000)]
    [InlineData("SUM(observe_input(v)) OVER(ORDER BY id ROWS BETWEEN CURRENT ROW AND UNBOUNDED FOLLOWING)", 2000, 1002000)]
    public async Task Execute_NativeFallbackFrames_ShouldRefuseAtWorkCapAsync(string window, int count, int expectedCalls)
    {
        // Arrange
        int calls = 0;
        var builder = SqlDatabaseEngine.CreateBuilder("window-native-fallback");
        builder.Functions.Add(SqlScalarFunction.Create<int,int>("observe_input", value => { calls++; return value; }));
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var db = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await db.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session, count);

        // Act
        DatabaseException? error = null;
        try { await session.ExecuteAsync($"SELECT {window} FROM t LIMIT 1", cancellationToken: CancellationToken.None); }
        catch (DatabaseException failure) { error = failure; }

        // Assert
        _output.WriteLine($"Native fallback input calls: {calls}.");
        error.ShouldNotBeNull().Diagnostics.ShouldContain(d => d.Code == "CS05084");
        calls.ShouldBe(expectedCalls);
        session.LastStatementMetrics.ShouldNotBeNull().HashMemoryBytes.ShouldBe(0);
        SqlWindowMemory.Current.ShouldBeNull();
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
