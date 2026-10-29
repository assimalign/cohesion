using System;
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
public sealed class SqlWindowAggregateMemoryTests
{
    private readonly ITestOutputHelper _output;

    public SqlWindowAggregateMemoryTests(ITestOutputHelper output) { _output = output; }

    [Theory(DisplayName = "Cohesion Test [Sql] - Window memory: computed aggregate inputs are charged before retention")]
    [InlineData("STRING_AGG('x',',' ORDER BY wide_key(v))")]
    [InlineData("STRING_AGG(wide_key(v),',' ORDER BY v)")]
    public async Task Execute_WideComputedInput_ShouldRefuseAndReleaseGrantAsync(string aggregate)
    {
        // Arrange
        int calls = 0;
        var builder = SqlDatabaseEngine.CreateBuilder("window-computed-memory");
        builder.Options.StatementMemoryBudget = 131072;
        builder.Functions.Add(SqlScalarFunction.Create<int,string>("wide_key", value => { calls++; return new string('x', 65536); }));
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var db = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await db.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t(v INT)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO t VALUES " + string.Join(",", Enumerable.Range(0,64).Select(i => $"({i})")), cancellationToken: CancellationToken.None);

        // Act
        DatabaseException? error = null;
        try { await session.ExecuteAsync($"SELECT {aggregate} OVER() FROM t", cancellationToken: CancellationToken.None); }
        catch (DatabaseException failure) { error = failure; }

        // Assert
        _output.WriteLine($"Computed input calls: {calls}; peak retained bytes: {session.LastStatementMetrics!.PeakHashMemoryBytes}.");
        error.ShouldNotBeNull().Diagnostics.ShouldContain(d => d.Code == "CS05079");
        calls.ShouldBe(1);
        session.LastStatementMetrics.HashMemoryBytes.ShouldBe(0);
        SqlWindowMemory.Current.ShouldBeNull();
        await using var usable = (await session.ExecuteAsync("SELECT COUNT(*) FROM t", cancellationToken: CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();
        await foreach (var row in usable.GetRowsAsync(CancellationToken.None)) { row.GetValue(0).ShouldBe(64L); }
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Window memory: an inherited execution context cannot charge an independent statement")]
    public async Task Execute_IndependentStatement_ShouldKeepItsOwnMemoryOwnerAsync()
    {
        // Arrange: Task.Run inherits the active window's AsyncLocal, but the child
        // statement has its own metrics. Its ordinary DISTINCT/string buffers must
        // not acquire or mutate the enclosing window's grant.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        SqlDatabaseSession? child = null;
        int calls = 0;
        var builder = SqlDatabaseEngine.CreateBuilder("window-independent-owner");
        builder.Options.StatementMemoryBudget = 131072;
        builder.Functions.Add(SqlScalarFunction.Create<int,int>("independent_query", value =>
        {
            calls++;
            var owner = SqlWindowMemory.Current.ShouldNotBeNull();
            Task.Run(async () =>
            {
                SqlWindowMemory.Current.ShouldBeSameAs(owner);
                await using var distinct = (await child!.ExecuteAsync("SELECT DISTINCT id FROM aux",cancellationToken:timeout.Token)).ShouldBeAssignableTo<DatabaseResultSet>();
                await using var text = (await child.ExecuteAsync("SELECT STRING_AGG('xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx','') FROM aux",cancellationToken:timeout.Token)).ShouldBeAssignableTo<DatabaseResultSet>();
                distinct.Columns.Count.ShouldBe(1); text.Columns.Count.ShouldBe(1);
            },timeout.Token).GetAwaiter().GetResult();
            return value;
        }));
        await using var engine = await builder.BuildAsync(timeout.Token);
        var db = await engine.CreateDatabaseAsync("db",timeout.Token);
        await using var session = await db.CreateSessionAsync(timeout.Token);
        await using var independentEngine = SqlDatabaseEngine.Create("window-independent-child",new());
        var independentDb = await independentEngine.CreateDatabaseAsync("db",timeout.Token);
        await using var independent = await independentDb.CreateSessionAsync(timeout.Token);
        child = independent;
        await session.ExecuteAsync("CREATE TABLE outer_rows(id INT)",cancellationToken:timeout.Token);
        await session.ExecuteAsync("INSERT INTO outer_rows VALUES(1)",cancellationToken:timeout.Token);
        await independent.ExecuteAsync("CREATE TABLE aux(id INT)",cancellationToken:timeout.Token);
        for (int first = 0; first < 4096; first += 256)
        { await independent.ExecuteAsync("INSERT INTO aux VALUES " + string.Join(",",Enumerable.Range(first,256).Select(i=>$"({i})")),cancellationToken:timeout.Token); }

        // Act
        var rows = await SqlWindowRangeGroupsTests.RowsAsync(session,"SELECT COUNT(independent_query(id)) OVER() FROM outer_rows");

        // Assert
        rows.ShouldHaveSingleItem()[0].ShouldBe(1L);
        calls.ShouldBe(1);
        session.LastStatementMetrics.ShouldNotBeNull().HashMemoryBytes.ShouldBe(0);
        SqlWindowMemory.Current.ShouldBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Window memory: correlated argument results release with their partition")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Execute_CorrelatedArguments_ShouldReleasePartitionBuffersAsync(bool nestedWindow)
    {
        // Arrange
        int calls = 0;
        var builder = SqlDatabaseEngine.CreateBuilder("window-correlated-memory");
        builder.Options.StatementMemoryBudget = 131072;
        builder.Functions.Add(SqlScalarFunction.Create<int,string>("wide_input", value => { calls++; return new string('x',8192); }));
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var db = await engine.CreateDatabaseAsync("db",CancellationToken.None);
        await using var session = await db.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t(id INT,p INT)",cancellationToken:CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO t VALUES " + string.Join(",",Enumerable.Range(0,32).Select(i=>$"({i},{i/2})")),cancellationToken:CancellationToken.None);

        string value = nestedWindow ? "FIRST_VALUE(wide_input(t.id)) OVER()" : "wide_input(t.id)";
        // Act
        var rows = await SqlWindowRangeGroupsTests.RowsAsync(session,$"SELECT COUNT((SELECT {value})) OVER(PARTITION BY p ORDER BY id ROWS 1 PRECEDING) FROM t ORDER BY id");
        int successfulCalls = calls;
        long peak = session.LastStatementMetrics!.PeakHashMemoryBytes;
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("SELECT COUNT((SELECT wide_input(t.id))) OVER(ORDER BY id ROWS 1 PRECEDING) FROM t",cancellationToken:CancellationToken.None));

        // Assert
        _output.WriteLine($"Correlated arguments: partitioned peak={peak}; calls={calls}.");
        rows.Select(r=>r[0]).ShouldBe(Enumerable.Range(0,32).Select(i=>(object)(i%2==0 ? 1L : 2L)));
        successfulCalls.ShouldBe(32);
        calls.ShouldBeGreaterThan(32);
        failure.Diagnostics.ShouldContain(d=>d.Code=="CS05079");
        session.LastStatementMetrics.ShouldNotBeNull().HashMemoryBytes.ShouldBe(0);
        SqlWindowMemory.Current.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Sql] - Window memory: admitted arguments and ordering keys evaluate once after FILTER")]
    public async Task Execute_AdmittedComputedInputs_ShouldEvaluateOnceAsync()
    {
        // Arrange
        int arguments = 0, keys = 0;
        var builder = SqlDatabaseEngine.CreateBuilder("window-input-once");
        builder.Functions.Add(SqlScalarFunction.Create<int,string>("observed_text", value => { arguments++; return value.ToString(); }));
        builder.Functions.Add(SqlScalarFunction.Create<int,int>("observed_key", value => { keys++; return value; }));
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var db = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await db.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("CREATE TABLE t(v INT)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO t VALUES(1),(2),(3)", cancellationToken: CancellationToken.None);

        // Act
        await using var result = (await session.ExecuteAsync("SELECT STRING_AGG(observed_text(v),',' ORDER BY observed_key(v) DESC) FILTER(WHERE v>1) OVER() FROM t", cancellationToken: CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();

        // Assert
        await foreach (var row in result.GetRowsAsync(CancellationToken.None)) { row.GetValue(0).ShouldBe("3,2"); }
        arguments.ShouldBe(2);
        keys.ShouldBe(2);
        session.LastStatementMetrics.ShouldNotBeNull().HashMemoryBytes.ShouldBe(0);
        SqlWindowMemory.Current.ShouldBeNull();
    }
}
