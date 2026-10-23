using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using Xunit;
using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Graph.Internal;
using Assimalign.Cohesion.Database.Graph.Language;
using Assimalign.Cohesion.Database.Types;

using Assimalign.Cohesion.Database.Graph.Protocol;

namespace Assimalign.Cohesion.Database.Graph.Tests;

public sealed class GraphAggregateTests
{
    private const string seed = "INSERT (a:Person {name:'Alice', age:42, active:TRUE})-[:KNOWS]->(:Person {name:'Bob', age:17, active:FALSE}), (:Person {name:'Isolated', age:30})";

    [Fact(DisplayName = "Cohesion Test [Graph] - Aggregates: counts are Int64, NULLs are skipped and collected values remain a list")]
    public async Task Execute_Aggregates_ShouldReturnRecordedValuesAndTypesAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("aggregates", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync(seed, cancellationToken: CancellationToken.None);
        // Act
        await using var result = (await session.ExecuteAsync("MATCH (a:Person) RETURN count(*) AS rows, count(a.name) AS names, count(DISTINCT a.active) AS flags, count(a.absent) AS absent, sum(a.age) AS total, avg(a.age) AS mean, min(a.age) AS youngest, max(a.age) AS oldest, collect_list(a.active) AS active", cancellationToken: CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();
        var row = (await Read(result)).ShouldHaveSingleItem();
        // Assert
        row.Take(4).ShouldBe(new object?[] { 3L, 3L, 2L, 0L });
        row.Take(4).ShouldAllBe(value => value is long);
        result.Columns.Take(4).ShouldAllBe(column => column.Type == DatabaseType.Int64);
        row[4].ShouldBeOfType<long>().ShouldBe(89L);
        row[5].ShouldBeOfType<double>().ShouldBe(89d / 3, 1e-12);
        row[6].ShouldBe(17L); row[7].ShouldBe(42L);
        row[8].ShouldBeOfType<object?[]>().ShouldBe(new object?[] { true, false }, ignoreOrder: true);
        var distinct = (await Rows(session, "MATCH (a:Person), (b:Person) RETURN sum(DISTINCT a.age), avg(DISTINCT a.age), min(DISTINCT a.age), max(DISTINCT a.age), collect_list(DISTINCT a.active)"))
            .ShouldHaveSingleItem();
        distinct.Take(4).ShouldBe(new object?[] { 89L, 89d / 3, 17L, 42L });
        distinct[4].ShouldBeOfType<object?[]>().ShouldBe(new object?[] { true, false }, ignoreOrder: true);
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Aggregates: empty input has one ungrouped row and no keyed groups")]
    [InlineData("count(*)", 0L)]
    [InlineData("count(a.name)", 0L)]
    [InlineData("count(DISTINCT a.active)", 0L)]
    [InlineData("sum(a.age)", 0L)]
    [InlineData("avg(a.age)", null)]
    [InlineData("min(a.age)", null)]
    [InlineData("max(a.age)", null)]
    public async Task Execute_EmptyInput_ShouldReturnAggregateIdentityAsync(string aggregate, object? expected)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("empty-aggregates", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync(seed, cancellationToken: CancellationToken.None);
        // Act / Assert
        var rows = await Rows(session, "MATCH (a:Person {name:'Nobody'}) RETURN " + aggregate);
        rows.ShouldHaveSingleItem().ShouldHaveSingleItem().ShouldBe(expected);
        if (expected is long) { rows[0][0].ShouldBeOfType<long>(); }
        (await Rows(session, "MATCH (a:Person {name:'Nobody'}) RETURN a.active, " + aggregate)).ShouldBeEmpty();
        (await Rows(session, "MATCH (a:Person {name:'Nobody'}) RETURN a.active, " + aggregate + " GROUP BY a.active")).ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Aggregates: optional rows distinguish COUNT(*) from COUNT(binding) and collect skips NULL")]
    public async Task Execute_OptionalMatch_ShouldCountNullExtendedRowsAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("optional-aggregates", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync(seed, cancellationToken: CancellationToken.None);
        // Act / Assert
        (await Rows(session, "MATCH (a:Person) OPTIONAL MATCH (a)-[:KNOWS]->(b) RETURN count(*), count(b), count(b.name), sum(b.age)"))
            .ShouldHaveSingleItem().ShouldBe(new object?[] { 3L, 1L, 1L, 17L });
        var empty = (await Rows(session, "MATCH (a:Person {name:'Nobody'}) RETURN collect_list(a.name), collect(a.age)"))
            .ShouldHaveSingleItem();
        empty[0].ShouldBeOfType<object?[]>().ShouldBeEmpty(); empty[1].ShouldBeOfType<object?[]>().ShouldBeEmpty();
        (await Rows(session, "RETURN count(NULL), sum(NULL), avg(NULL), min(NULL), max(NULL), collect(NULL)"))
            .ShouldHaveSingleItem().Take(5).ShouldBe(new object?[] { 0L, 0L, null, null, null });
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Aggregates: implicit and ISO keys agree and ORDER BY aliases runs after grouping")]
    public async Task Execute_GroupingAndOrder_ShouldUseGroupedScopeAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("grouped-order", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync(seed, cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("MATCH (a:Person {name:'Alice'}), (b:Person {name:'Isolated'}) INSERT (a)-[:KNOWS]->(b)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("MATCH (a:Person {name:'Bob'}), (b:Person {name:'Isolated'}) INSERT (a)-[:KNOWS]->(b)", cancellationToken: CancellationToken.None);
        const string prefix = "MATCH (a:Person)-[:KNOWS]->(b) RETURN a.name, count(*) AS n";
        // Act
        var implicitRows = await Rows(session, prefix + " ORDER BY n DESC LIMIT 1");
        var explicitRows = await Rows(session, prefix + " GROUP BY a.name ORDER BY n DESC LIMIT 1");
        // Assert
        implicitRows.ShouldHaveSingleItem().ShouldBe(new object?[] { "Alice", 2L });
        explicitRows.ShouldHaveSingleItem().ShouldBe(implicitRows[0]);
        (await Rows(session, prefix + " ORDER BY n LIMIT 1")).ShouldHaveSingleItem().ShouldBe(new object?[] { "Bob", 1L });
        var composed = await Rows(session, "MATCH (a:Person) RETURN a.active AS flag, count(*) + sum(a.age) AS n GROUP BY a.active ORDER BY n + 1 DESC");
        composed.Select(row => row[1]).ShouldBe(new object?[] { 43L, 31L, 18L });
        (await Rows(session, "MATCH (a:Person) RETURN a, count(*) AS n ORDER BY a.age"))
            .Select(row => row[0].ShouldBeOfType<GraphNode>().Properties["name"]).ShouldBe(new object?[] { "Bob", "Isolated", "Alice" });
        (await Rows(session, "MATCH (a:Person) RETURN a AS person, count(*) AS a ORDER BY a + 1")).Select(row => row[1]).ShouldBe(new object?[] { 1L, 1L, 1L });
        (await Rows(session, "MATCH (a:Person) RETURN CASE WHEN a.active IS NULL THEN 0 ELSE 1 END AS k, count(*) GROUP BY CASE WHEN a.active IS NULL THEN 0 ELSE 1 END ORDER BY k"))
            .Select(row => row[1]).ShouldBe(new object?[] { 1L, 2L });
        (await Rows(session, "MATCH (a:Person) RETURN count(*) AS n ORDER BY count(*) + 1")).ShouldHaveSingleItem()[0].ShouldBe(3L);
        (await Rows(session, "MATCH (a:Person) RETURN 1 AS k GROUP BY 1")).ShouldHaveSingleItem()[0].ShouldBe(1L);
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Aggregates: implicit, ISO alias and expression grouping retain every NULL and Boolean group")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Execute_GroupingSpellings_ShouldReturnAllGroupsAsync(bool ordered)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("grouping-spellings", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync(seed, cancellationToken: CancellationToken.None);
        const string source = "MATCH (a:Person) RETURN a.active AS active, count(*) AS n";
        string order = ordered ? " ORDER BY active DESC NULLS LAST" : "";
        object?[][] expected = [[true, 1L], [false, 1L], [null, 1L]];
        // Act / Assert
        foreach (string grouping in new[] { "", " GROUP BY active", " GROUP BY a.active" })
        {
            var rows = await Rows(session, source + grouping + order);
            if (ordered) { rows.Select(row => (row[0], row[1])).ShouldBe(expected.Select(row => (row[0], row[1]))); }
            else { rows.Select(row => (row[0], row[1])).ShouldBe(expected.Select(row => (row[0], row[1])), ignoreOrder: true); }
        }
        (await Rows(session, "MATCH (a:Person) RETURN count(*) GROUP BY ()")).ShouldHaveSingleItem().ShouldBe(new object?[] { 3L });
        (await Rows(session, "MATCH (a:Nobody) RETURN count(*) GROUP BY ()")).ShouldHaveSingleItem().ShouldBe(new object?[] { 0L });
        // A grouping name takes precedence over a same-named input variable.
        (await Rows(session, "MATCH (a:Person) RETURN a.active AS a, count(*) AS n GROUP BY a ORDER BY a DESC NULLS LAST"))
            .Select(row => (row[0], row[1])).ShouldBe(expected.Select(row => (row[0], row[1])));
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Aggregates: ORDER BY recomputes expressions against shadowing aliases")]
    [InlineData("a.v", "a.v + 1", 0)]
    [InlineData("a.v", "a.v", 0)]
    [InlineData("a.v + 1", "a.v + 1", 1)]
    public async Task Execute_ShadowingAlias_ShouldOrderProjectedScopeAsync(string projection, string order, long increment)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("aggregate-shadowing", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await Insert(session, [3L, 2L, 1L]);
        // Act / Assert: RETURN uses input a; ORDER BY uses the projected map a.
        foreach (string aggregate in new[] { "", ", count(*) AS n" })
        {
            var rows = await Rows(session, $"MATCH (a) RETURN {projection} AS old, {{v: -a.v}} AS a{aggregate} ORDER BY {order}");
            rows.Select(row => row[0]).ShouldBe(new object?[] { 3L + increment, 2L + increment, 1L + increment });
        }
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Aggregates: numeric keys and DISTINCT compare exact values across runtime types")]
    public async Task Execute_ExactNumericValues_ShouldRetainAdjacentAndFractionalDifferencesAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("exact-aggregates", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await Insert(session, [1L, 1d, 1m, 9007199254740992L, 9007199254740993L, 0.1m, 0.1d]);
        // Act
        var rows = await Rows(session, "MATCH (a) RETURN a.v, count(*) AS n ORDER BY a.v");
        var distinct = (await Rows(session, "MATCH (a) RETURN count(DISTINCT a.v), min(a.v), max(a.v), collect(DISTINCT a.v)"))
            .ShouldHaveSingleItem();
        // Assert
        rows.Count.ShouldBe(5); rows[2].ShouldBe(new object?[] { 1L, 3L });
        rows[3][0].ShouldBe(9007199254740992L); rows[4][0].ShouldBe(9007199254740993L);
        distinct[0].ShouldBe(5L); distinct[1].ShouldBeOfType<decimal>().ShouldBe(0.1m); distinct[2].ShouldBe(9007199254740993L);
        distinct[3].ShouldBeOfType<object?[]>().Length.ShouldBe(5);
        (await Rows(session, "MATCH (a) RETURN [a.v,NULL] AS k, count(*) AS n GROUP BY [a.v,NULL] ORDER BY k")).Count.ShouldBe(5);
        (await Rows(session, "MATCH (a) RETURN count(DISTINCT {v:a.v, n:NULL})")).ShouldHaveSingleItem()[0].ShouldBe(5L);
    }

    public static TheoryData<object?[], object, object> NumericCases => new()
    {
        { [(sbyte)1, (short)2, 3, 4L, (byte)5, (ushort)6, 7U], 28L, 4d },
        { [1L, 2f, 3d], 6d, 2d },
        { [13L, 0d], 13d, 6.5d },
        { [40L, 60L, 0.5d], 100.5d, 33.5d },
        { [1m, 2L, 3m], 6m, 2m },
        { [1m, 2d, 3L], 6m, 2m },
        { [ulong.MaxValue, 1L], (decimal)ulong.MaxValue + 1m, ((double)ulong.MaxValue + 1d) / 2 },
        { [long.MaxValue, 1L, 0d], (double)long.MaxValue, ((double)long.MaxValue + 1d) / 3 },
        { [long.MaxValue, 1L, 0m], (decimal)long.MaxValue + 1m, ((decimal)long.MaxValue + 1m) / 3 },
        { [decimal.MaxValue, -decimal.MaxValue, 3m], 3m, 1m },
    };

    [Theory(DisplayName = "Cohesion Test [Graph] - Aggregates: sum and avg promote across every retained numeric input")]
    [MemberData(nameof(NumericCases))]
    public async Task Execute_MixedNumericInputs_ShouldReturnPromotedTypesAsync(object?[] values, object sum, object average)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("numeric-aggregates", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await Insert(session, values);
        // Act
        var row = (await Rows(session, "MATCH (a) RETURN sum(a.v), avg(a.v)")).ShouldHaveSingleItem();
        // Assert
        row[0]?.GetType().ShouldBe(sum.GetType()); row[0].ShouldBe(sum);
        row[1]?.GetType().ShouldBe(average.GetType());
        if (average is double expected) { row[1].ShouldBeOfType<double>().ShouldBe(expected, Math.Max(1e-12, Math.Abs(expected) * 1e-15)); }
        else { row[1].ShouldBe(average); }
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Aggregates: avg stays finite when its intermediate sum would overflow")]
    public async Task Execute_LargeAverage_ShouldAvoidSumOverflowAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("large-average", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await Insert(session, [1L, 2L]);
        // Act / Assert
        (await Rows(session, "MATCH (a) RETURN avg($v)", new Dictionary<string, object?> { ["v"] = decimal.MaxValue }))
            .ShouldHaveSingleItem()[0].ShouldBe(decimal.MaxValue);
        (await Rows(session, "MATCH (a) RETURN avg($v)", new Dictionary<string, object?> { ["v"] = double.MaxValue }))
            .ShouldHaveSingleItem()[0].ShouldBe(double.MaxValue);
        (await Rows(session, "MATCH (a) RETURN avg($v)", new Dictionary<string, object?> { ["v"] = 1.0e30 }))
            .ShouldHaveSingleItem()[0].ShouldBe(1.0e30);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Aggregates: large floating inputs do not throw Decimal overflow per row")]
    public void Accumulate_LargeFloatingAverage_ShouldAvoidConversionExceptions()
    {
        // Arrange
        var accumulator = new GraphAggregateAccumulator(new GqlAggregateExpression(GqlAggregateFunction.Avg, new GqlLiteralExpression(1.0e30)));
        int threadId = Environment.CurrentManagedThreadId;
        int overflows = 0;
        EventHandler<System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs> observe = (_, args) =>
        {
            if (Environment.CurrentManagedThreadId == threadId && args.Exception is OverflowException) { overflows++; }
        };
        // Act
        AppDomain.CurrentDomain.FirstChanceException += observe;
        try { for (int i = 0; i < 256; i++) { accumulator.Add(1.0e30, CancellationToken.None); } }
        finally { AppDomain.CurrentDomain.FirstChanceException -= observe; }
        // Assert
        overflows.ShouldBe(0);
        accumulator.Finish().ShouldBeOfType<double>().ShouldBe(1.0e30);
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Aggregates: invalid placement and grouping fail with structured planner diagnostics")]
    [InlineData("MATCH (a) WHERE count(*) > 0 RETURN 1")]
    [InlineData("MATCH (a) RETURN count(count(*))")]
    [InlineData("MATCH (a) RETURN sum(CASE WHEN FALSE THEN count(*) ELSE 1 END)")]
    [InlineData("MATCH (a) RETURN a.v, count(*) GROUP BY a.other")]
    [InlineData("MATCH (a) RETURN a.v, count(*) GROUP BY a.v, a.v")]
    [InlineData("MATCH (a) RETURN count(*) GROUP BY count(*)")]
    [InlineData("MATCH (a) RETURN count(*) AS n ORDER BY a.v")]
    [InlineData("MATCH (a) RETURN a.v + count(*)")]
    [InlineData("MATCH (a) RETURN a.v ORDER BY count(*)")]
    [InlineData("MATCH (a) RETURN count(*) ORDER BY sum(a.v)")]
    [InlineData("MATCH (a) RETURN a.v, count(*) GROUP BY ()")]
    [InlineData("MATCH (a) RETURN a.v AS v, count(*) AS n GROUP BY v, n")]
    [InlineData("MATCH (a) RETURN a.v AS v, count(*) AS a GROUP BY a")]
    [InlineData("MATCH (a), (b) RETURN a.v + b.v AS old, {v: -a.v} AS a, count(*) AS n ORDER BY a.v + b.v")]
    public async Task Execute_InvalidAggregateScope_ShouldRejectBeforeExecutionAsync(string source)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("invalid-aggregates", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        // Act / Assert: includes EXPLAIN and an empty MATCH, so no data-dependent validation.
        foreach (string prefix in new[] { "", "EXPLAIN " })
        {
            var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(prefix + source, cancellationToken: CancellationToken.None));
            error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG05001");
            error.Diagnostics[0].Start.ShouldNotBeNull();
        }
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Aggregates: invalid operands are validated before reading even empty input")]
    [InlineData("MATCH (a:Nobody) RETURN sum($missing)", "CG05001")]
    [InlineData("MATCH (a) RETURN count(b)", "CG05001")]
    [InlineData("MATCH p = (a)->(b) RETURN count(p.x)", "CG03003")]
    [InlineData("MATCH p = (a)-->(b) RETURN count(p.x)", "CG00008")]
    public async Task Execute_InvalidAggregateOperand_ShouldReportPlanningDiagnosticAsync(string source, string code)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("aggregate-operands", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync(seed, cancellationToken: CancellationToken.None);
        // Act / Assert
        foreach (string prefix in new[] { "", "EXPLAIN " })
        {
            var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(prefix + source, cancellationToken: CancellationToken.None));
            error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(code);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Aggregates: exact SUM retains input scale for Decimal and UInt64")]
    public async Task Execute_ExactSum_ShouldPreservePresentedScaleAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("sum-scale", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await Insert(session, [0.1m, 0.1m, 0.1m]);
        // Act / Assert
        decimal fractional = (await Rows(session, "MATCH (a) RETURN sum(a.v)")).ShouldHaveSingleItem()[0].ShouldBeOfType<decimal>();
        fractional.ShouldBe(0.3m);
        ((decimal.GetBits(fractional)[3] >> 16) & 0xff).ShouldBe(1);
        await Insert(session, [1.20m]);
        decimal mixedScale = (await Rows(session, "MATCH (a) RETURN sum(a.v)")).ShouldHaveSingleItem()[0].ShouldBeOfType<decimal>();
        mixedScale.ShouldBe(1.50m);
        ((decimal.GetBits(mixedScale)[3] >> 16) & 0xff).ShouldBe(2);
        decimal scaledZero = (await Rows(session, "MATCH (a) RETURN sum($d)",
            new Dictionary<string, object?> { ["d"] = 0.00m })).ShouldHaveSingleItem()[0].ShouldBeOfType<decimal>();
        ((decimal.GetBits(scaledZero)[3] >> 16) & 0xff).ShouldBe(2);
        await session.ExecuteAsync("MATCH (a) DELETE a", cancellationToken: CancellationToken.None);
        await Insert(session, [1UL, 2UL]);
        decimal integral = (await Rows(session, "MATCH (a) RETURN sum(a.v)")).ShouldHaveSingleItem()[0].ShouldBeOfType<decimal>();
        integral.ShouldBe(3m);
        ((decimal.GetBits(integral)[3] >> 16) & 0xff).ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Aggregates: invalid numeric operands and overflow use the value-error code")]
    [InlineData("MATCH (a) RETURN sum(a.v)", false)]
    [InlineData("MATCH (a) RETURN avg(a.v)", false)]
    [InlineData("MATCH (a) RETURN sum(9223372036854775807)", true)]
    [InlineData("MATCH (a) RETURN sum(1.7976931348623157e308)", true)]
    public async Task Execute_InvalidNumericAggregate_ShouldReportValueDiagnosticAsync(string source, bool numeric)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("aggregate-errors", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await Insert(session, numeric ? [1L, 2L] : ["text"]);
        // Act / Assert
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(source, cancellationToken: CancellationToken.None));
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG02013");
        if (!numeric)
        {
            error.Diagnostics[0].Message.ShouldBe(source.Contains("sum(", StringComparison.Ordinal)
                ? "SUM requires numeric operands." : "AVG requires numeric operands.");
        }
        (await Rows(session, "RETURN count(*)")).ShouldHaveSingleItem()[0].ShouldBe(1L);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Aggregates: path requests retain the existing path projection diagnostic")]
    public async Task Execute_AggregatePathRequest_ShouldRejectWithPathMessageAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("aggregate-paths", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        // Act / Assert
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(
            GraphPathsQueryRequest.FromGql("MATCH (a) RETURN count(a)"), CancellationToken.None));
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG05001");
        error.Diagnostics[0].Message.ShouldNotBeNull().ShouldContain("Path execution requires", Case.Sensitive);
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Aggregates: collect enforces composite count, depth and byte budgets during accumulation")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Accumulate_OversizedList_ShouldReportValueDiagnostic(int bound)
    {
        // Arrange
        var accumulator = new GraphAggregateAccumulator(new GqlAggregateExpression(GqlAggregateFunction.CollectList, new GqlLiteralExpression(1L)));
        object value = bound switch
        {
            0 => new object?[GraphValueCodec.MaxValueCount - 1],
            1 => Enumerable.Range(0, GraphValueCodec.MaxDepth).Aggregate((object)1L, (inner, _) => new object?[] { inner }),
            _ => new string('x', GraphValueCodec.MaxPayloadLength / 2),
        };
        // Act / Assert
        var error = Should.Throw<DatabaseException>(() =>
        {
            accumulator.Add(value, CancellationToken.None);
            accumulator.Add(value, CancellationToken.None);
        });
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG02013");
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Aggregates: composite and entity extrema follow the existing total order and identity")]
    public async Task Execute_CompositeAndEntityInputs_ShouldReuseValueSemanticsAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("composite-aggregates", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await Insert(session, [1L, 2L, 3L]);
        // Act / Assert
        var row = (await Rows(session, "MATCH (a) RETURN min(CASE a.v WHEN 1 THEN {z:1} WHEN 2 THEN [NULL,1] ELSE 'text' END), max(CASE a.v WHEN 1 THEN {z:1} WHEN 2 THEN [NULL,1] ELSE 'text' END)"))
            .ShouldHaveSingleItem();
        row[0].ShouldBeAssignableTo<IReadOnlyDictionary<string, object?>>()!["z"].ShouldBe(1L);
        row[1].ShouldBe("text");
        var entities = (await Rows(session, "MATCH (a), (b) RETURN count(DISTINCT a), collect(DISTINCT a), min(a), max(a)"))
            .ShouldHaveSingleItem();
        entities[0].ShouldBe(3L); entities[1].ShouldBeOfType<object?[]>().Length.ShouldBe(3);
        entities[2].ShouldBeOfType<GraphNode>().Properties["v"].ShouldBe(1L);
        entities[3].ShouldBeOfType<GraphNode>().Properties["v"].ShouldBe(3L);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Aggregates: EXPLAIN maps grouping without evaluating arguments and mutations roll back on failure")]
    public async Task Execute_ExplainAndFailure_ShouldRespectStatementBoundaryAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("aggregate-planning", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        // Act / Assert
        var description = await Rows(session, "EXPLAIN RETURN sum(1/0) AS n ORDER BY n");
        description.ShouldContain(row => Equals(row[2], "GroupAggregate") && ((string)row[5]!).Contains("keys=0; aggregates=sum(", StringComparison.Ordinal));
        var error = await Should.ThrowAsync<DatabaseException>(async () =>
            await session.ExecuteAsync("INSERT (:Unwritten) RETURN sum('text')", cancellationToken: CancellationToken.None));
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG02013");
        (await Rows(session, "MATCH (a) RETURN count(*)")).ShouldHaveSingleItem()[0].ShouldBe(0L);
        await session.ExecuteAsync(seed, cancellationToken: CancellationToken.None);
        (await Rows(session, "MATCH (count:Person) WHERE count.age=42 RETURN count.name, count(count)"))
            .ShouldHaveSingleItem().ShouldBe(new object?[] { "Alice", 1L });
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Aggregates: source-free invalid aggregate nodes are planner errors")]
    public async Task Execute_MalformedAggregateAst_ShouldReportPlanningDiagnosticAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("aggregate-ast", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        // Act / Assert
        foreach (var aggregate in new[]
        {
            new GqlAggregateExpression((GqlAggregateFunction)100, new GqlLiteralExpression(1L)),
            new GqlAggregateExpression(GqlAggregateFunction.Sum),
            new GqlAggregateExpression(GqlAggregateFunction.Count, distinct: true),
        })
        {
            var query = new GqlQueryExpression([], null, [], [], false, [new GqlProjection(aggregate, "n")]);
            var error = await Should.ThrowAsync<DatabaseException>(async () =>
                await session.ExecuteAsync(new GraphQueryRequest(new GqlQueryStatement(query)), CancellationToken.None));
            error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG05001");
        }
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Aggregates: wide grouped projections do not allocate a key index per column")]
    public void Plan_WideGrouping_ShouldBoundScopeAllocation()
    {
        // Arrange
        var items = Enumerable.Range(0, 1000).Select(i => new GqlProjection(new GqlLiteralExpression((long)i), "k" + i)).ToList();
        items.Add(new GqlProjection(new GqlAggregateExpression(GqlAggregateFunction.Count), "n"));
        var query = new GqlQueryExpression([], null, [], [], false, items);
        var projections = items.Select(item => new GraphProjectionPlan(item.Expression, item.Alias!, GraphProjectionKind.Scalar)).ToArray();
        GraphGrouping.Plan(query, projections, CancellationToken.None);
        long before = GC.GetAllocatedBytesForCurrentThread();
        // Act
        var plan = GraphGrouping.Plan(query, projections, CancellationToken.None);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        // Assert: rebuilding the 1,000-key scope for every column exceeds 100 MiB.
        plan.ShouldNotBeNull().Keys.Count.ShouldBe(1000);
        plan.Aggregates.Count.ShouldBe(1);
        allocated.ShouldBeLessThan(10L * 1024 * 1024);
    }

    private static async Task Insert(GraphDatabaseSession session, object?[] values)
    {
        foreach (var value in values)
        { await session.ExecuteAsync("INSERT (:Value {v:$v})", new Dictionary<string, object?> { ["v"] = value }, CancellationToken.None); }
    }

    private static async Task<List<object?[]>> Rows(GraphDatabaseSession session, string source, IReadOnlyDictionary<string, object?>? parameters = null)
    {
        await using var result = (await session.ExecuteAsync(source, parameters, CancellationToken.None)).ShouldBeAssignableTo<DatabaseResultSet>();
        return await Read(result);
    }

    private static async Task<List<object?[]>> Read(DatabaseResultSet result)
    {
        var rows = new List<object?[]>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None)) { rows.Add(Enumerable.Range(0, row.FieldCount).Select(row.GetValue).ToArray()); }
        return rows;
    }
}
