using System;
using System.Collections.Generic;
using System.Linq;
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
            "COUNT(DISTINCT observe(id))",
            "SUM(CASE WHEN id > 0 THEN observe(id) ELSE 0 END)",
            "SUM(CAST(observe(id) AS BIGINT))",
            "SUM(-observe(id))",
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
    [InlineData(SqlFunctionVolatility.Volatile, "", 9)]
    [InlineData(SqlFunctionVolatility.Volatile, " GROUP BY g", 9)]
    [InlineData(SqlFunctionVolatility.Volatile, " GROUP BY ROLLUP(g)", 9)]
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

        // Act: the whole ORDER BY window expression reuses its grouped aggregate inputs.
        // The window functions themselves still have separate aggregate states.
        calls = 0;
        var ordered = await RowsAsync(session, $"SELECT {aggregate} OVER w, {aggregate} OVER w FROM t{grouping} " +
            $"WINDOW w AS () ORDER BY {aggregate} OVER w;");

        // Assert
        calls.ShouldBe(grouping.Length == 0 ? 9 : 6);
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

    [Theory(DisplayName = "Cohesion Test [Sql] - Aggregate sharing: equal whole window keys reuse aggregate slots (#1763)")]
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
        // Arrange: independent aggregates would produce different partitions or orderings.
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
        calls.ShouldBe(3);
        CheckRows(rows, partition ? [[1, 2L, 2L], [2, 1L, 1L], [3, 2L, 2L]] : [[1, 2L, 2L], [2, 1L, 1L], [3, 3L, 3L]]);
    }

    public static IEnumerable<object[]> WholeAggregateKeys()
    {
        foreach (string grouping in new[] { " GROUP BY g", " GROUP BY GROUPING SETS ((g))" })
        {
            yield return ["SUM(nextval('s'))", " ORDER BY SUM(nextval('s'))", grouping, 6m, 3L];
            yield return ["SUM(nextval('s'))", " HAVING SUM(nextval('s')) = 12 ORDER BY SUM(nextval('s'))", grouping, 9m, 6L];
            yield return ["SUM(nextval('s'))", " ORDER BY SUM(nextval('s')) + 1", grouping, 9m, 6L];
            yield return ["SUM(nextval('s')) AS result", " ORDER BY result + SUM(nextval('s'))", grouping, 9m, 6L];
            yield return ["COUNT(*)", " ORDER BY SUM(nextval('s')) + 1, SUM(nextval('s')) + 1", grouping, 3L, 3L];
            yield return ["SUM(nextval('s')), COUNT(*) OVER w", " WINDOW w AS (ORDER BY SUM(nextval('s')))", grouping, 6m, 3L];
            yield return ["COUNT(*) OVER (PARTITION BY SUM(nextval('s'))), SUM(nextval('s'))", "", grouping, 1L, 3L];
            yield return ["SUM(nextval('s')) + MAX(nextval('s'))", " ORDER BY SUM(nextval('s')) + MAX(nextval('s'))", grouping, 15m, 6L];
            yield return ["SUM((SELECT nextval('s') + t.id * 0))", " ORDER BY SUM((SELECT nextval('s') + t.id * 0))", grouping, 6m, 3L];
            yield return ["COUNT(*) OVER (ORDER BY SUM(nextval('s'))), COUNT(*) OVER (ORDER BY SUM(nextval('s')))", "", grouping, 1L, 3L];
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Aggregate sharing: whole targets and earlier keys map slots while nested keys remain independent (#1763)")]
    [MemberData(nameof(WholeAggregateKeys))]
    public async Task ExecuteAsync_WholeAggregateKeys_ShouldReadMatchedSlots(string projection, string suffix, string grouping, object expected, long allocations)
    {
        // Arrange: slots run left to right per row. One SUM gives 1+2+3=6;
        // independent SUM/HAVING slots give 1+3+5=9 and 2+4+6=12.
        await using var engine = CreateEngine();
        var database = await engine.CreateDatabaseAsync("keys");
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE SEQUENCE s;");
        await ExecuteAsync(session, "CREATE TABLE t(id INT, g INT);");
        await ExecuteAsync(session, "INSERT INTO t VALUES(1,1),(2,1),(3,1);");

        // Act
        var rows = await RowsAsync(session, $"SELECT {projection} FROM t{grouping}{suffix};");
        var sequence = await RowsAsync(session, "SELECT currval('s');");

        // Assert
        rows.ShouldHaveSingleItem()[0].ShouldBe(expected);
        if (projection.StartsWith("COUNT(*) OVER (PARTITION", StringComparison.Ordinal)) { rows[0][1].ShouldBe(6m); }
        if (projection.EndsWith(", COUNT(*) OVER w", StringComparison.Ordinal)) { rows[0][1].ShouldBe(1L); }
        sequence.ShouldHaveSingleItem()[0].ShouldBe(allocations);
        if (projection.StartsWith("COUNT(*) OVER (ORDER", StringComparison.Ordinal))
        {
            // Repeat from a fresh sequence position to pin executed sort work as well.
            await RowsAsync(session, "SELECT setval('s', 1, FALSE);");
            var plan = await RowsAsync(session, $"EXPLAIN ANALYZE SELECT {projection} FROM t{grouping}{suffix};");
            plan.Where(row => Equals(row[2], "WindowAgg")).ShouldHaveSingleItem()[5]!.ToString()!
                .ShouldContain("sort_count=1", Case.Sensitive);
            (await RowsAsync(session, "SELECT currval('s');")).ShouldHaveSingleItem()[0].ShouldBe(3L);
            rows[0][1].ShouldBe(1L);
        }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Aggregate sharing: grouped window keys merge stages and share sort prefixes for nonvolatile inputs (#1763)")]
    [InlineData("SUM(id)", false)]
    [InlineData("SUM(observe(id))", false)]
    [InlineData("SUM(id)", true)]
    [InlineData("SUM(observe(id))", true)]
    public async Task ExecuteAsync_NonvolatileWindowKeys_ShouldShareSort(string key, bool prefix)
    {
        // Arrange
        var builder = SqlDatabaseEngine.CreateBuilder("window-keys");
        builder.Functions.Add(SqlScalarFunction.Create<int, int>("observe", value => value, SqlFunctionVolatility.Stable));
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var database = await engine.CreateDatabaseAsync("keys");
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE TABLE t(id INT, g INT);");
        await ExecuteAsync(session, "INSERT INTO t VALUES(1,1),(2,2),(3,3);");
        string extra = prefix ? ", g" : "";

        // Act
        var plan = await RowsAsync(session, $"EXPLAIN SELECT ROW_NUMBER() OVER (ORDER BY {key}{extra}), RANK() OVER (ORDER BY {key}) FROM t GROUP BY g;");

        // Assert
        var windows = plan.Where(row => Equals(row[2], "WindowAgg")).ToArray();
        windows.Length.ShouldBe(prefix ? 2 : 1);
        if (prefix) { windows.ShouldContain(row => row[5]!.ToString()!.Contains("sort_shared=true", StringComparison.Ordinal)); }
    }

    [Theory(DisplayName = "Cohesion Test [Sql] - Aggregate sharing: volatile builtins and nonvolatile child queries retain separate states (#1763)")]
    [InlineData("currval('s')", " GROUP BY g", false)]
    [InlineData("lastval()", " GROUP BY g", false)]
    [InlineData("gen_random_uuid()", " GROUP BY g", true)]
    [InlineData("(SELECT 1)", " GROUP BY g", false)]
    [InlineData("currval('s')", " GROUP BY GROUPING SETS ((g))", false)]
    [InlineData("lastval()", " GROUP BY GROUPING SETS ((g))", false)]
    [InlineData("gen_random_uuid()", " GROUP BY GROUPING SETS ((g))", true)]
    [InlineData("(SELECT 1)", " GROUP BY GROUPING SETS ((g))", false)]
    public async Task ExecuteAsync_NonshareableInputs_ShouldCreateSeparateStates(string input, string grouping, bool uuid)
    {
        // Arrange
        int states = 0, transitions = 0;
        var builder = SqlDatabaseEngine.CreateBuilder("builtin-sharing");
        builder.Functions.Add(SqlAggregateFunction.Create<long, long, long>("tracked_sum", () => { states++; return 0; },
            (state, value) => { transitions++; return state + value; }, state => state));
        builder.Functions.Add(SqlAggregateFunction.Create<long, Guid, long>("tracked_count", () => { states++; return 0; },
            (state, value) => { transitions++; return state + 1; }, state => state));
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var database = await engine.CreateDatabaseAsync("sharing");
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await ExecuteAsync(session, "CREATE SEQUENCE s;");
        await RowsAsync(session, "SELECT nextval('s');");
        await ExecuteAsync(session, "CREATE TABLE t(id INT, g INT);");
        await ExecuteAsync(session, "INSERT INTO t VALUES(1,1),(2,1),(3,1);");
        string aggregate = uuid ? "tracked_count" : "tracked_sum";

        // Act
        var rows = await RowsAsync(session, $"SELECT {aggregate}({input}), {aggregate}({input}) FROM t{grouping};");

        // Assert
        states.ShouldBe(2);
        transitions.ShouldBe(6);
        rows.ShouldHaveSingleItem().ShouldBe(new object?[] { 3L, 3L });
    }
}
