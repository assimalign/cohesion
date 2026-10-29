using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Sql.Internal;

namespace Assimalign.Cohesion.Database.Sql.Tests;

[Collection(nameof(SqlDatabaseEventSourceCollection))]
public sealed class SqlWindowReviewRegressionTests
{
    private readonly ITestOutputHelper _output;
    public SqlWindowReviewRegressionTests(ITestOutputHelper output) { _output = output; }

    [Fact(DisplayName = "Cohesion Test [Sql] - Window review: discarded correlated LIMIT rows drop live payloads before releasing charges")]
    public async Task Execute_DiscardedCorrelatedRows_ShouldKeepLivePayloadsChargedAsync()
    {
        // Arrange
        var payloads = new List<WeakReference<string>>();
        long liveBytes = 0;
        var builder = SqlDatabaseEngine.CreateBuilder("window-discarded-review");
        builder.Options.StatementMemoryBudget = 131072;
        builder.Functions.Add(SqlScalarFunction.Create<int,string>("wide_probe", value =>
        {
            string text = new('x',4096);
            payloads.Add(new(text));
            return text;
        },SqlFunctionVolatility.Stable));
        builder.Functions.Add(SqlScalarFunction.Create<string,string>("observe_live", value =>
        {
            liveBytes = CollectLiveBytes(payloads);
            return value;
        }));
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var db = await engine.CreateDatabaseAsync("db",CancellationToken.None);
        await using var session = await db.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);

        // Act: the secondary output key requires each fresh payload before collection;
        // the unique id key still selects the final auxiliary row.
        var rows = await SqlWindowRangeGroupsTests.RowsAsync(session,
            "SELECT COUNT(observe_live((SELECT wide_probe(a.id) FROM aux a WHERE a.id>=t.id ORDER BY a.id DESC,1 LIMIT 1))) OVER() FROM t");
        long peak = session.LastStatementMetrics!.PeakHashMemoryBytes;

        // Assert
        _output.WriteLine($"Discarded rows: live={liveBytes}; peak charge={peak}; allocations={payloads.Count}.");
        rows.ShouldHaveSingleItem()[0].ShouldBe(1L);
        payloads.Count.ShouldBe(500);
        liveBytes.ShouldBeGreaterThan(0);
        liveBytes.ShouldBeLessThanOrEqualTo(3 * 8216L);
        peak.ShouldBeGreaterThanOrEqualTo(liveBytes);
        session.LastStatementMetrics.HashMemoryBytes.ShouldBe(0);
        SqlWindowMemory.Current.ShouldBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Window review: eager child vectors and relation caches are admitted to the grant")]
    [InlineData("SELECT COUNT(CASE WHEN 'x' IN(SELECT wide_probe(id) FROM aux) THEN 1 ELSE 0 END) OVER() FROM t")]
    [InlineData("SELECT COUNT(*) FILTER(WHERE 'x' IN(SELECT wide_probe(id) FROM aux)) OVER() FROM t")]
    [InlineData("SELECT COUNT(*) OVER(PARTITION BY CASE WHEN 'x' IN(SELECT wide_probe(id) FROM aux) THEN 1 ELSE 0 END) FROM t")]
    [InlineData("SELECT COUNT(*) OVER(ORDER BY CASE WHEN 'x' IN(SELECT wide_probe(id) FROM aux) THEN 1 ELSE 0 END) FROM t")]
    [InlineData("WITH c AS(SELECT wide_probe(id) AS v FROM aux) SELECT COUNT(CASE WHEN 'x' IN(SELECT v FROM c) THEN 1 ELSE 0 END) OVER() FROM t")]
    [InlineData("WITH c AS(SELECT wide_probe(id) AS v FROM aux) SELECT COUNT(*) OVER() FROM c")]
    public async Task Execute_EagerWindowChildren_ShouldRefuseUnchargedPayloadsAsync(string sql)
    {
        // Arrange
        var builder = SqlDatabaseEngine.CreateBuilder("window-eager-review");
        builder.Options.StatementMemoryBudget = 131072;
        builder.Functions.Add(SqlScalarFunction.Create<int,string>("wide_probe", value => new string('x',4096)));
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var db = await engine.CreateDatabaseAsync("db",CancellationToken.None);
        await using var session = await db.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(() => SqlWindowRangeGroupsTests.RowsAsync(session,sql));

        // Assert
        _output.WriteLine($"Eager child refusal: {string.Join(",",failure.Diagnostics.Select(d=>d.Code))}; peak={session.LastStatementMetrics!.PeakHashMemoryBytes}.");
        failure.Diagnostics.ShouldContain(d=>d.Code==(sql.Contains("FILTER",StringComparison.Ordinal) ? "CS05079" : "CS05067"));
        session.LastStatementMetrics!.HashMemoryBytes.ShouldBe(0);
        SqlWindowMemory.Current.ShouldBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Window review: eager scalar and set payloads remain charged while the window observes them")]
    [InlineData("'x' IN(SELECT wide_probe(id) FROM aux)",500)]
    [InlineData("(SELECT wide_probe(1))='x'",1)]
    public async Task Execute_AdmittedEagerChildren_ShouldChargeLivePayloadsAsync(string predicate,int count)
    {
        // Arrange
        var payloads = new List<WeakReference<string>>();
        long liveBytes = 0;
        var builder = SqlDatabaseEngine.CreateBuilder("window-live-eager-review");
        builder.Options.StatementMemoryBudget = 8388608;
        builder.Functions.Add(SqlScalarFunction.Create<int,string>("wide_probe",value=>
        {
            string text = new('x',4096);
            payloads.Add(new(text));
            return text;
        }));
        builder.Functions.Add(SqlScalarFunction.Create<int,int>("observe_live",value=>
        { liveBytes = CollectLiveBytes(payloads); return value; }));
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var db = await engine.CreateDatabaseAsync("db",CancellationToken.None);
        await using var session = await db.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);

        // Act
        var rows = await SqlWindowRangeGroupsTests.RowsAsync(session,
            $"SELECT COUNT(CASE WHEN {predicate} THEN observe_live(1) ELSE observe_live(0) END) OVER() FROM t");
        long peak = session.LastStatementMetrics!.PeakHashMemoryBytes;

        // Assert
        _output.WriteLine($"Eager payloads: live={liveBytes}; peak charge={peak}; allocations={payloads.Count}.");
        rows.ShouldHaveSingleItem()[0].ShouldBe(1L);
        payloads.Count.ShouldBe(count);
        liveBytes.ShouldBe(count*8216L);
        peak.ShouldBeGreaterThanOrEqualTo(liveBytes);
        session.LastStatementMetrics.HashMemoryBytes.ShouldBe(0);
        SqlWindowMemory.Current.ShouldBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Window review: EXISTS and computed IN lists retain only their scalar results")]
    [InlineData("EXISTS(SELECT wide_probe(id) FROM aux)")]
    [InlineData("'x' IN(wide_probe(id),wide_probe(CAST(id+1 AS INT)),wide_probe(CAST(id+2 AS INT)))")]
    public async Task Execute_ScalarPredicates_ShouldReleaseTemporaryPayloadsAsync(string predicate)
    {
        // Arrange
        var payloads = new List<WeakReference<string>>();
        long liveBytes = -1;
        var builder = SqlDatabaseEngine.CreateBuilder("window-temporary-review");
        builder.Options.StatementMemoryBudget = 131072;
        builder.Functions.Add(SqlScalarFunction.Create<int,string>("wide_probe",value=>
        {
            string text = new('x',4096);
            payloads.Add(new(text));
            return text;
        }));
        builder.Functions.Add(SqlScalarFunction.Create<int,int>("observe_live",value=>
        { liveBytes = CollectLiveBytes(payloads); return value; }));
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var db = await engine.CreateDatabaseAsync("db",CancellationToken.None);
        await using var session = await db.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);

        // Act
        var rows = await SqlWindowRangeGroupsTests.RowsAsync(session,
            $"SELECT COUNT(CASE WHEN {predicate} THEN observe_live(1) ELSE observe_live(0) END) OVER() FROM t");

        // Assert
        rows.ShouldHaveSingleItem()[0].ShouldBe(1L);
        liveBytes.ShouldBe(0);
        session.LastStatementMetrics!.HashMemoryBytes.ShouldBe(0);
        SqlWindowMemory.Current.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Window review: volatile sequence arguments restart when the frame head moves")]
    public async Task Execute_NextvalMovingFrame_ShouldRestartArgumentsAsync()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("window-sequence-review",new());
        var db = await engine.CreateDatabaseAsync("db",CancellationToken.None);
        await using var session = await db.CreateSessionAsync(CancellationToken.None);
        await SqlWindowRangeGroupsTests.ExecuteAsync(session,"CREATE TABLE t(id INT)");
        await SqlWindowRangeGroupsTests.ExecuteAsync(session,"INSERT INTO t VALUES(1),(2),(3)");
        await SqlWindowRangeGroupsTests.ExecuteAsync(session,"CREATE SEQUENCE s START WITH 1");

        // Act
        var rows = await SqlWindowRangeGroupsTests.RowsAsync(session,"SELECT SUM(nextval('s')) OVER(ORDER BY id ROWS 1 PRECEDING) FROM t ORDER BY id");
        var current = await SqlWindowRangeGroupsTests.RowsAsync(session,"SELECT currval('s')");

        // Assert
        rows.Select(r=>Convert.ToDecimal(r[0],CultureInfo.InvariantCulture)).ShouldBe([1m,3m,7m]);
        current.ShouldHaveSingleItem()[0].ShouldBe(4L);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Window review: setval arguments restart and preserve sequence state")]
    public async Task Execute_SetvalMovingFrame_ShouldRestartArgumentsAsync()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("window-setval-review",new());
        var db = await engine.CreateDatabaseAsync("db",CancellationToken.None);
        await using var session = await db.CreateSessionAsync(CancellationToken.None);
        await SqlWindowRangeGroupsTests.ExecuteAsync(session,"CREATE TABLE t(id INT)");
        await SqlWindowRangeGroupsTests.ExecuteAsync(session,"INSERT INTO t VALUES(1),(2),(3)");
        await SqlWindowRangeGroupsTests.ExecuteAsync(session,"CREATE SEQUENCE s START WITH 1");

        // Act
        var rows = await SqlWindowRangeGroupsTests.RowsAsync(session,"SELECT SUM(setval('s',nextval('s')+1)) OVER(ORDER BY id ROWS 1 PRECEDING) FROM t ORDER BY id");
        var current = await SqlWindowRangeGroupsTests.RowsAsync(session,"SELECT currval('s')");

        // Assert
        rows.Select(r=>Convert.ToDecimal(r[0],CultureInfo.InvariantCulture)).ShouldBe([2m,6m,14m]);
        current.ShouldHaveSingleItem()[0].ShouldBe(8L);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Window review: random inputs use restart candidate counters")]
    public async Task Execute_RandomMovingFrame_ShouldUseRestartCountersAsync()
    {
        // Arrange
        await using var engine = SqlDatabaseEngine.Create("window-random-review",new());
        var db = await engine.CreateDatabaseAsync("db",CancellationToken.None);
        await using var session = await db.CreateSessionAsync(CancellationToken.None);
        await SqlWindowRangeGroupsTests.ExecuteAsync(session,"CREATE TABLE t(id INT)");
        await SqlWindowRangeGroupsTests.ExecuteAsync(session,"INSERT INTO t VALUES(1),(2),(3)");

        // A registered zero-argument volatile function exercises the random() call shape.
        await SqlWindowRangeGroupsTests.ExecuteAsync(session,"CREATE FUNCTION random() RETURNS DOUBLE PRECISION VOLATILE RETURN 0.5");

        // Act
        var analysis = await SqlWindowRangeGroupsTests.RowsAsync(session,"EXPLAIN ANALYZE SELECT SUM(random()) OVER(ORDER BY id ROWS 1 PRECEDING) FROM t");

        // Assert: work counters, independent of the random values.
        analysis.Single(r=>Equals(r[2],"WindowAgg"))[5]!.ToString()!.ShouldContain(
            "frame_visits=4; transitions=0; inverse_transitions=0;",Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Window review: volatile restart work retains the statement visit cap")]
    public async Task Execute_VolatileShrinkingFrame_ShouldRefuseAtVisitCapAsync()
    {
        // Arrange
        int calls = 0;
        var builder = SqlDatabaseEngine.CreateBuilder("window-volatile-cap-review");
        builder.Functions.Add(SqlScalarFunction.Create<int,int>("observe",value=>{ calls++; return value; }));
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var db = await engine.CreateDatabaseAsync("db",CancellationToken.None);
        await using var session = await db.CreateSessionAsync(CancellationToken.None);
        await SqlWindowRangeGroupsTests.ExecuteAsync(session,"CREATE TABLE t(id INT)");
        await SqlWindowRangeGroupsTests.ExecuteAsync(session,"INSERT INTO t VALUES "+string.Join(",",Enumerable.Range(0,2000).Select(i=>$"({i})")));

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(()=>SqlWindowRangeGroupsTests.RowsAsync(session,
            "SELECT SUM(observe(id)) OVER(ORDER BY id ROWS BETWEEN CURRENT ROW AND UNBOUNDED FOLLOWING) FROM t LIMIT 1"));

        // Assert
        failure.Diagnostics.ShouldContain(d=>d.Code=="CS05084");
        calls.ShouldBe(1002000);
        session.LastStatementMetrics!.HashMemoryBytes.ShouldBe(0);
        SqlWindowMemory.Current.ShouldBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Window review: volatile calls and subplans use restart visits instead of inverse transitions")]
    [InlineData("SUM(observe(id))",4, new long[]{1,3,7})]
    [InlineData("SUM((SELECT observe(t.id)))",4,new long[]{1,3,7})]
    [InlineData("SUM(id) FILTER(WHERE observe(id)>0)",4,new long[]{1,3,5})]
    [InlineData("SUM(CASE WHEN id>0 THEN COALESCE(observe(id),0) ELSE 0 END)",4,new long[]{1,3,7})]
    [InlineData("SUM((SELECT t.id))",0,new long[]{1,3,5})]
    public async Task Execute_RestartSensitiveFrame_ShouldUseRecomputationAsync(string aggregate,int expectedCalls,long[] expected)
    {
        // Arrange
        int calls = 0;
        var builder = SqlDatabaseEngine.CreateBuilder("window-volatile-review");
        builder.Functions.Add(SqlScalarFunction.Create<int,int>("observe",value=>++calls));
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var db = await engine.CreateDatabaseAsync("db",CancellationToken.None);
        await using var session = await db.CreateSessionAsync(CancellationToken.None);
        await SqlWindowRangeGroupsTests.ExecuteAsync(session,"CREATE TABLE t(id INT)");
        await SqlWindowRangeGroupsTests.ExecuteAsync(session,"INSERT INTO t VALUES(1),(2),(3)");
        string sql = $"SELECT {aggregate} OVER(ORDER BY id ROWS 1 PRECEDING) FROM t ORDER BY id";

        // Act
        var rows = await SqlWindowRangeGroupsTests.RowsAsync(session,sql);
        int actualCalls = calls;
        var analysis = await SqlWindowRangeGroupsTests.RowsAsync(session,"EXPLAIN ANALYZE "+sql);

        // Assert
        _output.WriteLine($"{aggregate}: calls={actualCalls}; values={string.Join(",",rows.Select(r=>r[0]))}.");
        rows.Select(r=>Convert.ToDecimal(r[0],CultureInfo.InvariantCulture)).ShouldBe(expected.Select(v=>(decimal)v));
        actualCalls.ShouldBe(expectedCalls);
        string detail = analysis.Single(r=>Equals(r[2],"WindowAgg"))[5]!.ToString()!;
        detail.ShouldContain("frame_visits=4; transitions=0; inverse_transitions=0;",Case.Sensitive);
        session.LastStatementMetrics!.HashMemoryBytes.ShouldBe(0);
        SqlWindowMemory.Current.ShouldBeNull();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long CollectLiveBytes(List<WeakReference<string>> payloads)
    {
        GC.Collect(GC.MaxGeneration,GCCollectionMode.Forced,blocking:true,compacting:true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration,GCCollectionMode.Forced,blocking:true,compacting:true);
        long bytes = 0;
        foreach(var payload in payloads)
        { if(payload.TryGetTarget(out var text)) { bytes += (24L+2L*text.Length+7)&~7L; } }
        return bytes;
    }

    private static async Task SeedAsync(SqlDatabaseSession session)
    {
        await SqlWindowRangeGroupsTests.ExecuteAsync(session,"CREATE TABLE t(id INT)");
        await SqlWindowRangeGroupsTests.ExecuteAsync(session,"INSERT INTO t VALUES(0)");
        await SqlWindowRangeGroupsTests.ExecuteAsync(session,"CREATE TABLE aux(id INT)");
        await SqlWindowRangeGroupsTests.ExecuteAsync(session,"INSERT INTO aux VALUES "+string.Join(",",Enumerable.Range(0,500).Select(i=>$"({i.ToString(CultureInfo.InvariantCulture)})")));
    }
}
