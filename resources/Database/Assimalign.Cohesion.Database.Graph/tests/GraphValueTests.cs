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

namespace Assimalign.Cohesion.Database.Graph.Tests;

public sealed class GraphValueTests
{
    [Fact(DisplayName = "Cohesion Test [Graph] - Values: invalid leaves, dimensions and cycles retain the parameter diagnostic")]
    public void Snapshot_InvalidCompositeParameters_ShouldRejectBeforeEvaluation()
    {
        // Arrange
        var cyclicList = new List<object?>(); cyclicList.Add(cyclicList);
        var cyclicMap = new Dictionary<string, object?>(); cyclicMap["self"] = cyclicMap;
        // Act / Assert
        foreach (object value in new object[] { new object?[] { new object() }, new object?[] { double.NaN }, new int[1, 1], cyclicList, cyclicMap })
        {
            Should.Throw<GraphPlanningException>(() => GraphCompositeValues.Snapshot(value))
                .Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG03003");
        }
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Values: source-free conditional and composite ASTs render in EXPLAIN")]
    public void Describe_PublicValueNodes_ShouldRenderEquivalentSyntax()
    {
        // Arrange
        var one = new GqlLiteralExpression(1L);
        var map = new GqlMapExpression([new KeyValuePair<string, GqlExpression>("k", new GqlListExpression([one]))]);
        var conditional = new GqlCaseExpression(one, [new GqlCaseAlternative(one, map)],
            new GqlCoalesceExpression([new GqlLiteralExpression(null), new GqlNullIfExpression(one, one)]));
        // Act / Assert
        GraphProjectionText.Describe(conditional).ShouldBe("CASE 1 WHEN 1 THEN {k: [1]} ELSE COALESCE(NULL, NULLIF(1, 1)) END");
        GraphProjectionText.Describe(new GqlMemberExpression(new GqlSubscriptExpression(new GqlListExpression([map]), one), "k"))
            .ShouldBe("[{k: [1]}][1].k");
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Values: CASE and composite semantics retain exact values")]
    [InlineData("CASE WHEN NULL THEN 1 ELSE 2 END", 2L)]
    [InlineData("CASE WHEN TRUE THEN 1 WHEN 1/0 = 0 THEN 2 END", 1L)]
    [InlineData("CASE 42 WHEN 17,42 THEN 1 ELSE 2 END", 1L)]
    [InlineData("CASE 42 WHEN > 30 THEN 1 END", 1L)]
    [InlineData("CASE -2 WHEN <-1 THEN 1 END", 1L)]
    [InlineData("CASE 42 WHEN < 30, >= 42 THEN 1 END", 1L)]
    [InlineData("CASE 42 WHEN <= 42 THEN 1 END", 1L)]
    [InlineData("CASE 42 WHEN <> 42 THEN 1 ELSE 2 END", 2L)]
    [InlineData("CASE 42 WHEN != 17 THEN 1 END", 1L)]
    [InlineData("CASE 1 WHEN 1, 1/0 THEN 1 END", 1L)]
    [InlineData("CASE 9007199254740993 WHEN > 9007199254740992.0 THEN 1 END", 1L)]
    [InlineData("CASE NULL WHEN IS NULL THEN 1 END", 1L)]
    [InlineData("CASE NULL WHEN IS NOT NULL THEN 1 ELSE 2 END", 2L)]
    [InlineData("CASE 42 WHEN IS NOT NULL THEN 1 END", 1L)]
    [InlineData("[1][NULL]", null)]
    [InlineData("NULL[0]", null)]
    [InlineData("{k:1}.missing", null)]
    [InlineData("CASE WHEN FALSE THEN 1 END", null)]
    [InlineData("CASE WHEN TRUE THEN 2 WHEN TRUE THEN 3 END", 2L)]
    [InlineData("CASE NULL WHEN NULL THEN 1 ELSE 2 END", 2L)]
    [InlineData("CASE 1 WHEN 1.0 THEN 2 ELSE 3 END", 2L)]
    [InlineData("CASE [NULL] WHEN [NULL] THEN 1 ELSE 2 END", 2L)]
    [InlineData("CASE WHEN TRUE THEN 2 ELSE 'text' END", 2L)]
    [InlineData("CASE WHEN TRUE THEN 2 ELSE 1/0 END", 2L)]
    [InlineData("COALESCE(NULL, 2, 1/0)", 2L)]
    [InlineData("NULLIF(1, 1.0)", null)]
    [InlineData("NULLIF(1, NULL)", 1L)]
    [InlineData("[10,20,30][-1]", 30L)]
    [InlineData("[1][5]", null)]
    [InlineData("[1][-2]", null)]
    [InlineData("{k: {j: 1}}.k.j", 1L)]
    [InlineData("{k: 1}['missing']", null)]
    [InlineData("[1,2] = [1.0,2]", true)]
    [InlineData("[NULL] = [NULL]", null)]
    [InlineData("[NULL,1] = [NULL,2]", false)]
    [InlineData("{k: NULL} = {k: NULL}", null)]
    [InlineData("{k: NULL} <> {j: NULL}", true)]
    [InlineData("[1] IN [[1]]", true)]
    [InlineData("1 IN ([1,2])", true)]
    [InlineData("[NULL] IN [[NULL]]", null)]
    [InlineData("[1,2] < [1,3]", true)]
    [InlineData("[NULL] < [1]", null)]
    [InlineData("{a: 1} < {b: 1}", true)]
    public async Task Execute_ValueExpression_ShouldReturnExpected(string expression, object? expected)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("values", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        // Act
        var rows = await Rows(session, "RETURN " + expression);
        // Assert
        rows.ShouldHaveSingleItem().ShouldHaveSingleItem().ShouldBe(expected);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Values: parameters, optional matches and exact composite DISTINCT work together")]
    public async Task Execute_CompositeParameters_ShouldPreserveSemantics()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("values", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("INSERT (:Person {name:'Alice',age:42}), (:Person {name:'Bob',age:17})", cancellationToken: CancellationToken.None);
        // Act / Assert
        var invalidParameter = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("RETURN $p",
            new Dictionary<string, object?> { ["p"] = new object?[] { Guid.Empty } }, CancellationToken.None));
        var invalidDiagnostic = invalidParameter.Diagnostics.ShouldHaveSingleItem();
        invalidDiagnostic.Code.ShouldBe("CG03003");
        invalidDiagnostic.Start.ShouldBe(7); invalidDiagnostic.End.ShouldBe(9);
        var firstMissing = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(
            "RETURN CASE WHEN $first THEN 1 ELSE $last END", cancellationToken: CancellationToken.None));
        firstMissing.Message.ShouldContain("first", Case.Sensitive);
        foreach (object ages in new object[] { new object?[] { 17L, 42L }, new List<long> { 17, 42 } })
        {
            var parameters = new Dictionary<string, object?> { ["ages"] = ages };
            (await Rows(session, "MATCH (a:Person) WHERE a.age IN $ages RETURN a.name", parameters)).Select(row => row[0]).ShouldBe(["Alice", "Bob"], ignoreOrder: true);
        }
        var values = new Dictionary<string, object?> { ["l"] = new List<long> { 9 }, ["m"] = new Dictionary<string, object?> { ["k"] = 8 }, ["d"] = 0.1m, ["f"] = 0.1d };
        (await Rows(session, "RETURN $l[0], $m.k, [$d] = [$f], CASE $d WHEN $f THEN 1 ELSE 2 END, NULLIF($d,$f)", values))
            .ShouldHaveSingleItem().ShouldBe(new object?[] { 9L, 8, false, 2L, 0.1m });
        (await Rows(session, "MATCH (a:Person) OPTIONAL MATCH (a)->(b) RETURN COALESCE(b,a).name"))
            .Select(row => row[0]).ShouldBe(["Alice", "Bob"], ignoreOrder: true);
        (await Rows(session, "MATCH (a:Person {name:'Alice'}) RETURN COALESCE(a.absent,a.name), NULLIF(a.age,42), (a).name, a['name']"))
            .ShouldHaveSingleItem().ShouldBe(new object?[] { "Alice", null, "Alice", "Alice" });
        var memberError = await Should.ThrowAsync<DatabaseException>(async () =>
            await session.ExecuteAsync("MATCH (a:Person) RETURN (a.name).x", cancellationToken: CancellationToken.None));
        memberError.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG02013");
        var distinct = await Rows(session, "MATCH (a:Person) RETURN DISTINCT [NULL,1], {k:NULL}");
        distinct.Count.ShouldBe(1);
        (await Rows(session, "MATCH (a:Person) RETURN DISTINCT CASE a.age WHEN 42 THEN [$d] ELSE [$f] END", values)).Count.ShouldBe(2);
        values["d"] = 1m; values["f"] = 1.0d;
        (await Rows(session, "MATCH (a:Person) RETURN DISTINCT CASE a.age WHEN 42 THEN [$d,NULL] ELSE [$f,NULL] END", values)).Count.ShouldBe(1);
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("INSERT (:Unwritten {tags: $l})", values, CancellationToken.None));
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG03003");
        (await Rows(session, "MATCH (a:Unwritten) RETURN a.name")).ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Values: invalid container operations use the value-error diagnostic")]
    [InlineData("(1).x")]
    [InlineData("CASE WHEN 1 THEN 1 END")]
    [InlineData("[1][1.0]")]
    [InlineData("[1]['x']")]
    [InlineData("{k:1}[0]")]
    [InlineData("1 IN 2")]
    public async Task Execute_InvalidValueOperation_ShouldReportValueError(string expression)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("values", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("RETURN " + expression, cancellationToken: CancellationToken.None));
        // Assert
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG02013");
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Values: constructed bounds roll back the statement in process")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Execute_ConstructedBound_ShouldRollBack(int form)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("bounds", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        string value = form == 0 ? new string('[', 65) + "1" + new string(']', 65) : "[$p,$p]";
        var parameters = new Dictionary<string, object?>();
        if (form != 0) { parameters["p"] = form == 1 ? new object?[40_000] : new string('a', 9 * 1024 * 1024); }
        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(
            "INSERT (:Unwritten) RETURN " + value, parameters, CancellationToken.None));
        // Assert
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG02013");
        (await Rows(session, "MATCH (n:Unwritten) RETURN 1")).ShouldBeEmpty();
        (await Rows(session, "RETURN 42")).ShouldHaveSingleItem().ShouldBe(new object?[] { 42L });
    }

    private static async Task<List<object?[]>> Rows(GraphDatabaseSession session, string statement, IReadOnlyDictionary<string, object?>? parameters = null)
    {
        await using var result = (DatabaseResultSet)await session.ExecuteAsync(statement, parameters, CancellationToken.None);
        var rows = new List<object?[]>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None))
        { rows.Add(Enumerable.Range(0, row.FieldCount).Select(row.GetValue).ToArray()); }
        return rows;
    }
}
