using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;

namespace Assimalign.Cohesion.Database.Graph.Tests;

public sealed class GraphFunctionReviewTests
{
    [Theory(DisplayName = "Cohesion Test [Graph] - Functions: application timeout and I/O receive callback diagnostics")]
    [InlineData("timeout")]
    [InlineData("io")]
    [InlineData("file")]
    public async Task ExecuteAsync_CallbackFailure_ShouldKeepCauseAndMessageAsync(string kind)
    {
        // Arrange
        Exception cause = kind switch { "timeout" => new TimeoutException("callback timeout"), "io" => new IOException("callback IO"), _ => new FileNotFoundException("callback file") };
        var builder = GraphDatabaseEngine.CreateBuilder("errors");
        builder.Functions.Add(GraphScalarFunction.Create<long>("fail", () => throw cause));
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(() => Rows(session, "RETURN fail()"));

        // Assert
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG05021");
        failure.InnerException.ShouldBeSameAs(cause);
        failure.Message.ShouldContain(cause.Message, Case.Sensitive);
        (await Rows(session, "RETURN 7")).ShouldBe(new object?[] { 7L });
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Functions: nonfinite scalar aggregate and nested results violate the result contract")]
    [InlineData("nan()")]
    [InlineData("infinite(1)")]
    [InlineData("nested_nan()")]
    public async Task ExecuteAsync_NonfiniteResult_ShouldFailContractAsync(string expression)
    {
        // Arrange
        var builder = GraphDatabaseEngine.CreateBuilder("finite");
        builder.Functions.Add(GraphScalarFunction.Create<double>("nan", () => double.NaN));
        builder.Functions.Add(GraphScalarFunction.Create<object>("nested_nan", () => new object?[] { new object?[] { double.NaN } }));
        builder.Functions.Add(GraphAggregateFunction.Create<long, long, double>("infinite", () => 0L, (state, value) => state + value, _ => double.PositiveInfinity, _ => 8));
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(() => Rows(session, "RETURN " + expression));

        // Assert
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG05021");
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Functions: volatile aggregate inputs execute once per row and appearance")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_VolatileAggregateArgument_ShouldEvaluateEachAppearanceAsync(bool native)
    {
        // Arrange
        int calls = 0;
        var builder = GraphDatabaseEngine.CreateBuilder("volatile");
        builder.Functions.Add(GraphScalarFunction.Create<long, long>("tick", _ => ++calls));
        builder.Functions.Add(GraphAggregateFunction.Create<long, long, long>("total", () => 0L, (state, value) => state + value, state => state, _ => 8));
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);

        // Act
        string aggregate = native ? "sum" : "total";
        var values = await Rows(session, $"FOR v IN [1,2] RETURN {aggregate}(tick(v)) AS first, {aggregate}(tick(v)) AS second");

        // Assert
        calls.ShouldBe(4);
        values.ShouldBe(new object?[] { 4L, 6L });
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Functions: Any accepts nested query lists")]
    public async Task ExecuteAsync_AnyNestedList_ShouldRetainValueAsync()
    {
        // Arrange
        var builder = GraphDatabaseEngine.CreateBuilder("any");
        builder.Functions.Add(GraphScalarFunction.Create<object, object>("echo_any", value => value));
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);

        // Act
        var values = await Rows(session, "RETURN echo_any([[1]])[0]");

        // Assert
        values.ShouldHaveSingleItem().ShouldBeOfType<object[]>().ShouldBe(new object?[] { 1L });
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Functions: unsupported numeric CLR adapters are refused at creation")]
    public void Create_NoncanonicalNumericTypes_ShouldRefuseBeforeRegistration()
    {
        // Arrange / Act / Assert
        Should.Throw<ArgumentException>(() => GraphScalarFunction.Create<int, int>("f", value => value));
        Should.Throw<ArgumentException>(() => GraphScalarFunction.Create<short, short>("f", value => value));
        Should.Throw<ArgumentException>(() => GraphScalarFunction.Create<sbyte, sbyte>("f", value => value));
        Should.Throw<ArgumentException>(() => GraphScalarFunction.Create<byte, byte>("f", value => value));
        Should.Throw<ArgumentException>(() => GraphScalarFunction.Create<ushort, ushort>("f", value => value));
        Should.Throw<ArgumentException>(() => GraphScalarFunction.Create<uint, uint>("f", value => value));
        Should.Throw<ArgumentException>(() => GraphScalarFunction.Create<ulong, ulong>("f", value => value));
        Should.Throw<ArgumentException>(() => GraphScalarFunction.Create<float, float>("f", value => value));
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Functions: called-on-NULL typed inputs must accept NULL at creation")]
    public void Create_NonnullableCalledOnNull_ShouldRefuse()
    {
        // Arrange / Act / Assert
        Should.Throw<ArgumentException>(() => GraphScalarFunction.Create<long, long>("f", value => value, GraphNullBehavior.CalledOnNullInput));
        Should.Throw<ArgumentException>(() => GraphScalarFunction.Create<long?, long, long>("f", (a, b) => b, GraphNullBehavior.CalledOnNullInput));
        Should.Throw<ArgumentException>(() => GraphAggregateFunction.Create<long, long, long>("f", () => 0L, (a, b) => a + b, a => a, _ => 8, GraphNullBehavior.CalledOnNullInput));
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Functions: dynamic argument mismatch uses a value diagnostic before callback")]
    public async Task ExecuteAsync_ArgumentMismatch_ShouldUseValueCodeAsync()
    {
        // Arrange
        int calls = 0;
        var builder = GraphDatabaseEngine.CreateBuilder("argument");
        builder.Functions.Add(GraphScalarFunction.Create<long, long>("integer_only", value => { calls++; return value; }));
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);

        await session.ExecuteAsync("INSERT (:Input {value:1.5})", cancellationToken: CancellationToken.None);

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(() => Rows(session, "MATCH (n:Input) RETURN integer_only(n.value)"));

        // Assert
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG02013");
        calls.ShouldBe(0);
    }

    private static async Task<object?[]> Rows(GraphDatabaseSession session, string text)
    {
        await using var result = (DatabaseResultSet)await session.ExecuteAsync(text, cancellationToken: CancellationToken.None);
        var values = new List<object?>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None))
        { values.AddRange(Enumerable.Range(0, result.Columns.Count).Select(row.GetValue)); }
        return values.ToArray();
    }
}
