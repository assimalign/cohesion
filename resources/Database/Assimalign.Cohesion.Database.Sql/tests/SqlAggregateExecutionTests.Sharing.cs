using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed partial class SqlAggregateExecutionTests
{
    public static IEnumerable<object[]> VolatileAggregateInputs()
    {
        string[] aggregates =
        [
            "SUM(observe(id))",
            "SUM(COALESCE(NULL, observe(id)) + 0)",
            "SUM(random())",
            "SUM(id) FILTER (WHERE observe(id) > 0)",
            "STRING_AGG(CAST(id AS TEXT), ',' ORDER BY observe(id))",
            "LISTAGG(CAST(id AS TEXT), ',') WITHIN GROUP (ORDER BY observe(id))",
            "SUM((SELECT observe(t.id)))",
            "SUM(id) FILTER (WHERE EXISTS (SELECT 1 WHERE observe(t.id) > 0))",
            "SUM(id) FILTER (WHERE id IN (SELECT observe(t.id)))",
            "SUM(id) FILTER (WHERE id = ANY (SELECT observe(t.id)))",
        ];
        foreach (string grouping in new[] { "", " GROUP BY g", " GROUP BY GROUPING SETS ((g))", " GROUP BY ROLLUP(g)", " GROUP BY CUBE(g)" })
        {
            foreach (string aggregate in aggregates) { yield return [aggregate, grouping]; }
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Aggregate sharing: volatile arguments, FILTER, ordering and subqueries retain independent calls (#1763)")]
    [MemberData(nameof(VolatileAggregateInputs))]
    public async Task ExecuteAsync_VolatileAggregateInputs_ShouldEvaluateEachOccurrence(string aggregate, string grouping)
    {
        // Arrange
        int calls = 0;
        var builder = SqlDatabaseEngine.CreateBuilder("aggregate-sharing");
        builder.Functions.Add(SqlScalarFunction.Create<int, int>("observe", value => { calls++; return value; }));
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var database = await engine.CreateDatabaseAsync("sharing");
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        // Deterministic implementation of the volatile zero-argument random() call shape.
        await ExecuteAsync(session, "CREATE FUNCTION random() RETURNS INT VOLATILE RETURN observe(1);");
        await ExecuteAsync(session, "CREATE TABLE t(id INT, g INT);");
        await ExecuteAsync(session, "INSERT INTO t VALUES(1,1),(2,1),(3,1);");

        // Act
        var rows = await RowsAsync(session, $"SELECT {aggregate}, {aggregate} FROM t{grouping};");

        // Assert: grouping sets feed each evaluated logical input to every set.
        calls.ShouldBe(6);
        rows.Count.ShouldBe(grouping.Contains("ROLLUP") || grouping.Contains("CUBE") ? 2 : 1);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Aggregate sharing: repeated HAVING and ORDER BY calls follow scalar volatility (#1763)")]
    [InlineData(SqlFunctionVolatility.Volatile, "", 12)]
    [InlineData(SqlFunctionVolatility.Volatile, " GROUP BY g", 12)]
    [InlineData(SqlFunctionVolatility.Volatile, " GROUP BY ROLLUP(g)", 12)]
    [InlineData(SqlFunctionVolatility.Stable, "", 3)]
    [InlineData(SqlFunctionVolatility.Stable, " GROUP BY g", 3)]
    [InlineData(SqlFunctionVolatility.Stable, " GROUP BY ROLLUP(g)", 3)]
    [InlineData(SqlFunctionVolatility.Immutable, "", 3)]
    [InlineData(SqlFunctionVolatility.Immutable, " GROUP BY g", 3)]
    [InlineData(SqlFunctionVolatility.Immutable, " GROUP BY ROLLUP(g)", 3)]
    public async Task ExecuteAsync_RepeatedAggregateScopes_ShouldRespectVolatility(SqlFunctionVolatility volatility, string grouping, int expectedCalls)
    {
        // Arrange
        int calls = 0;
        var builder = SqlDatabaseEngine.CreateBuilder("aggregate-scope-sharing");
        builder.Functions.Add(SqlScalarFunction.Create<int, int>("observe", value => { calls++; return value; }, volatility));
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var database = await engine.CreateDatabaseAsync("sharing");
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE t(id INT, g INT);");
        await ExecuteAsync(session, "INSERT INTO t VALUES(1,1),(2,1),(3,1);");

        // Act
        var rows = await RowsAsync(session, "SELECT SUM(observe(id)), SUM(observe(id)) FROM t" + grouping +
            " HAVING SUM(observe(id)) > 0 ORDER BY SUM(observe(id));");

        // Assert
        calls.ShouldBe(expectedCalls);
        rows.ShouldAllBe(row => Equals(row[0], 6m) && Equals(row[1], 6m));
        rows.Count.ShouldBe(grouping.Contains("ROLLUP") ? 2 : 1);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Aggregate sharing: ORDER BY aliases and ordinals read the completed volatile output (#1763)")]
    [InlineData("result")]
    [InlineData("1")]
    public async Task ExecuteAsync_OutputOrdering_ShouldReadCompletedAggregate(string ordering)
    {
        // Arrange
        int calls = 0;
        var builder = SqlDatabaseEngine.CreateBuilder("aggregate-output-sharing");
        builder.Functions.Add(SqlScalarFunction.Create<int, int>("observe", value => { calls++; return value; }));
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var database = await engine.CreateDatabaseAsync("sharing");
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE t(id INT);");
        await ExecuteAsync(session, "INSERT INTO t VALUES(1),(2),(3);");

        // Act
        var rows = await RowsAsync(session, $"SELECT SUM(observe(id)) AS result FROM t ORDER BY {ordering};");

        // Assert
        calls.ShouldBe(3);
        rows.ShouldHaveSingleItem().ShouldBe(new object?[] { 6m });
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Aggregate sharing: setval alone prevents sharing of application aggregate states (#1763)")]
    [InlineData(" GROUP BY g", 2)]
    [InlineData(" GROUP BY ROLLUP(g)", 4)]
    public async Task ExecuteAsync_SetvalInput_ShouldRetainSeparateTransitions(string grouping, int expectedStates)
    {
        // Arrange
        int states = 0, transitions = 0;
        var builder = SqlDatabaseEngine.CreateBuilder("aggregate-setval-sharing");
        builder.Functions.Add(SqlAggregateFunction.Create<long, long, long>("tracked_sum", () => { states++; return 0; },
            (state, value) => { transitions++; return state + value; }, state => state));
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var database = await engine.CreateDatabaseAsync("sharing");
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE SEQUENCE s;");
        await ExecuteAsync(session, "CREATE TABLE t(id INT, g INT);");
        await ExecuteAsync(session, "INSERT INTO t VALUES(1,1),(2,1),(3,1);");

        // Act
        var rows = await RowsAsync(session, "SELECT tracked_sum(setval('s', id)), tracked_sum(setval('s', id)) FROM t" + grouping + ";");

        // Assert
        states.ShouldBe(expectedStates);
        transitions.ShouldBe(3 * expectedStates);
        rows.ShouldAllBe(row => Equals(row[0], 6L) && Equals(row[1], 6L));
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Aggregate sharing: window calls retain independent inputs in shared stages (#1763)")]
    [InlineData("SUM(observe(id))", "")]
    [InlineData("SUM(id) FILTER (WHERE observe(id) > 0)", "")]
    [InlineData("STRING_AGG(CAST(id AS TEXT), ',' ORDER BY observe(id))", "")]
    [InlineData("LISTAGG(CAST(id AS TEXT), ',') WITHIN GROUP (ORDER BY observe(id))", "")]
    [InlineData("SUM((SELECT observe(t.id)))", "")]
    [InlineData("SUM(SUM(observe(id)))", " GROUP BY g")]
    [InlineData("SUM(SUM(observe(id)))", " GROUP BY ROLLUP(g)")]
    public async Task ExecuteAsync_WindowAggregateInputs_ShouldRetainSeparateCalls(string aggregate, string grouping)
    {
        // Arrange
        int calls = 0;
        var builder = SqlDatabaseEngine.CreateBuilder("window-aggregate-sharing");
        builder.Functions.Add(SqlScalarFunction.Create<int, int>("observe", value => { calls++; return value; }));
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var database = await engine.CreateDatabaseAsync("sharing");
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE t(id INT, g INT);");
        await ExecuteAsync(session, "INSERT INTO t VALUES(1,1),(2,1),(3,1);");

        // Act
        var rows = await RowsAsync(session, $"SELECT {aggregate} OVER w, {aggregate} OVER w FROM t{grouping} WINDOW w AS ();");

        // Assert
        calls.ShouldBe(6);
        rows.ShouldNotBeEmpty();

        // Act: a separately written ORDER BY window call has its own aggregate as well.
        calls = 0;
        var ordered = await RowsAsync(session, $"SELECT {aggregate} OVER w, {aggregate} OVER w FROM t{grouping} " +
            $"WINDOW w AS () ORDER BY {aggregate} OVER w;");

        // Assert
        calls.ShouldBe(9);
        ordered.Count.ShouldBe(rows.Count);
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Aggregate sharing: one named-window aggregate expression binds once (#1763)")]
    [InlineData(" GROUP BY g")]
    [InlineData(" GROUP BY ROLLUP(g)")]
    public async Task ExecuteAsync_NamedWindowAggregate_ShouldEvaluateOneWrittenOccurrence(string grouping)
    {
        // Arrange
        int calls = 0;
        var builder = SqlDatabaseEngine.CreateBuilder("named-window-aggregate-sharing");
        builder.Functions.Add(SqlScalarFunction.Create<int, int>("observe", value => { calls++; return value; }));
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var database = await engine.CreateDatabaseAsync("sharing");
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE t(id INT, g INT);");
        await ExecuteAsync(session, "INSERT INTO t VALUES(1,1),(2,1),(3,1);");

        // Act
        var rows = await RowsAsync(session, "SELECT ROW_NUMBER() OVER w, RANK() OVER w FROM t" + grouping +
            " WINDOW w AS (ORDER BY SUM(observe(id)));");

        // Assert
        calls.ShouldBe(3);
        rows.ShouldNotBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Aggregate sharing: window partitions and shared-sort prefixes respect independent aggregate slots (#1763)")]
    [InlineData(" GROUP BY g", "partition", false)]
    [InlineData(" GROUP BY g", "partition", true)]
    [InlineData(" GROUP BY g", "order", false)]
    [InlineData(" GROUP BY g", "order", true)]
    [InlineData(" GROUP BY g", "prefix", false)]
    [InlineData(" GROUP BY g", "prefix", true)]
    [InlineData(" GROUP BY GROUPING SETS ((g))", "partition", false)]
    [InlineData(" GROUP BY GROUPING SETS ((g))", "partition", true)]
    [InlineData(" GROUP BY GROUPING SETS ((g))", "order", false)]
    [InlineData(" GROUP BY GROUPING SETS ((g))", "order", true)]
    [InlineData(" GROUP BY GROUPING SETS ((g))", "prefix", false)]
    [InlineData(" GROUP BY GROUPING SETS ((g))", "prefix", true)]
    public async Task ExecuteAsync_WindowAggregateKeys_ShouldUseEachBoundResult(string grouping, string kind, bool wrapped)
    {
        // Arrange: the two written aggregates produce different partitions or opposite orderings.
        int calls = 0;
        bool partition = kind == "partition";
        var builder = SqlDatabaseEngine.CreateBuilder("window-aggregate-key-sharing");
        builder.Functions.Add(SqlScalarFunction.Create<int, int>("observe", value =>
            ++calls % 2 == 1 ? partition ? 0 : value : partition ? value : -value));
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var database = await engine.CreateDatabaseAsync("sharing");
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE t(id INT, g INT);");
        await ExecuteAsync(session, "INSERT INTO t VALUES(1,1),(2,2),(3,3);");
        string aggregate = wrapped ? "COALESCE(SUM(observe(id)), 0) + 0" : "SUM(observe(id))";
        string function = partition ? "COUNT(*)" : "ROW_NUMBER()";
        string clause = partition ? "PARTITION BY " : "ORDER BY ";
        string suffix = kind == "prefix" ? ", g" : "";

        // Act
        var rows = await RowsAsync(session, $"SELECT g, {function} OVER ({clause}{aggregate}{suffix}), " +
            $"{function} OVER ({clause}{aggregate}) FROM t{grouping} ORDER BY g;");

        // Assert
        calls.ShouldBe(6);
        CheckRows(rows, partition ? [[1, 3L, 1L], [2, 3L, 1L], [3, 3L, 1L]] : [[1, 1L, 3L], [2, 2L, 2L], [3, 3L, 1L]]);
    }
}
