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
