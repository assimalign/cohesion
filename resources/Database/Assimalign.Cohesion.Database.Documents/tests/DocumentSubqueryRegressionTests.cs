using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;

namespace Assimalign.Cohesion.Database.Documents.Tests;

public sealed class DocumentSubqueryRegressionTests
{
    [Theory(DisplayName = "Cohesion Test [Documents] - Subqueries: valid complete projections retain the 256-level boundary")]
    [InlineData("SELECT (SELECT RAW @deep FROM items LIMIT 1) AS v FROM items LIMIT 1", 254)]
    [InlineData("SELECT RAW (SELECT RAW @deep FROM items LIMIT 1) FROM items LIMIT 1", 255)]
    [InlineData("SELECT * FROM items d UNNEST (SELECT RAW @deep FROM items LIMIT 1) AS v LIMIT 1", 255)]
    public async Task ExecuteAsync_AtDepthLimit_ShouldPreserveRawNamedAndStarResultsAsync(string query, int depth)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("depth-boundary", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await DocumentSubqueryTests.SeedAsync(session);
        // Act
        await using var result = (DatabaseResultSet)await session.ExecuteAsync(query, DeepParameters(depth), CancellationToken.None);
        // Assert
        int count = 0;
        await foreach (var row in result.GetRowsAsync(CancellationToken.None))
        { row.GetValue(0).ShouldBeOfType<JsonElement>(); count++; }
        count.ShouldBe(1);
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Subqueries: complete projections enforce the wire depth before formatting")]
    [InlineData("(SELECT RAW @deep FROM items LIMIT 1)", false)]
    [InlineData("COALESCE((SELECT RAW @deep FROM items LIMIT 1), [])", false)]
    [InlineData("FIRST x FOR x IN [(SELECT RAW @deep FROM items LIMIT 1)] END", false)]
    [InlineData("(SELECT RAW (SELECT RAW @deep FROM items LIMIT 1) FROM items LIMIT 1)", true)]
    [InlineData("(SELECT @deep AS p FROM items LIMIT 1)", true)]
    [InlineData("ARRAY_AGG((SELECT RAW @deep FROM items LIMIT 1))", true)]
    public async Task ExecuteAsync_OverdeepProjection_ShouldReturnLocatedDepthFailureAsync(string expression, bool raw)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("depth-regression", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.CreateCollectionAsync("items", CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO items (KEY, VALUE) VALUES ('a', {n:1})", cancellationToken: CancellationToken.None);
        var parameters = DeepParameters();
        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () =>
            await session.ExecuteAsync("SELECT " + (raw ? "RAW " : "") + expression + " FROM items", parameters, CancellationToken.None));
        // Assert
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD05015");
        error.Diagnostics.Single().Start.ShouldNotBeNull();
        (await DocumentSubqueryTests.RowsAsync(session, "SELECT n FROM items")).Single().GetInt32(0).ShouldBe(1);
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Subqueries: constructors serialize before reaching later errors or callbacks")]
    [InlineData("[@deep, {tail}]", "1/0")]
    [InlineData("[@deep, {tail}]", "tick(1)")]
    [InlineData("{a:[@deep, {tail}], b:tick(2)}", "1/0")]
    [InlineData("{a:[@deep, {tail}], b:tick(2)}", "tick(1)")]
    [InlineData("ARRAY CASE WHEN x = 1 THEN @deep ELSE {tail} END FOR x IN [1,2] END", "1/0")]
    [InlineData("ARRAY CASE WHEN x = 1 THEN @deep ELSE {tail} END FOR x IN [1,2] END", "tick(1)")]
    [InlineData("FIRST [@deep, {tail}] FOR x IN [1,2] END", "1/0")]
    [InlineData("FIRST [@deep, {tail}] FOR x IN [1,2] END", "tick(1)")]
    public async Task ExecuteAsync_ConstructorFailure_ShouldStopBeforeLaterOperandAsync(string template, string tail)
    {
        // Arrange
        int calls = 0;
        var builder = DocumentDatabaseEngine.CreateBuilder("constructor-regression");
        builder.Functions.Add(DocumentScalarFunction.Create<decimal, decimal>("tick", value => { calls++; return value; }));
        await using var engine = builder.Build();
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await DocumentSubqueryTests.SeedAsync(session);
        string expression = template.Replace("{tail}", tail, StringComparison.Ordinal);
        // Act / Assert: run both dispatch paths, including an unrelated subquery in the latter.
        foreach (string extra in new[] { "", ", (SELECT RAW n FROM items) AS v" })
        {
            calls = 0;
            var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(
                "SELECT " + expression + extra + " FROM items LIMIT 1", DeepParameters(), CancellationToken.None));
            error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD05015");
            calls.ShouldBe(0);
        }
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Subqueries: outer aliases are named correlation refusals")]
    [InlineData("SELECT n AS p FROM items ORDER BY (SELECT RAW p FROM other s)")]
    [InlineData("SELECT (SELECT RAW d.* FROM other s) FROM items d")]
    [InlineData("SELECT (SELECT RAW n FROM d) FROM items d")]
    public async Task ExecuteAsync_OuterAlias_ShouldRefuseCorrelationDuringPlanningAsync(string query)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("correlation-regression", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await DocumentSubqueryTests.SeedAsync(session);
        await session.CreateCollectionAsync("other", CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO other (KEY, VALUE) VALUES ('a', {x:9})", cancellationToken: CancellationToken.None);
        // Act / Assert
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(query, cancellationToken: CancellationToken.None));
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CK00001");
        error.Diagnostics.Single().Message!.ShouldContain("Correlated", Case.Sensitive);
        session.LastQuerySubqueriesExecuted.ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Subqueries: local names shadow outer aliases and unqualified fields bind inside")]
    [InlineData("SELECT n AS p FROM items ORDER BY (SELECT RAW p FROM items p LIMIT 1)")]
    [InlineData("SELECT n AS p FROM items ORDER BY (SELECT n AS p FROM items s ORDER BY p LIMIT 1)")]
    [InlineData("SELECT (SELECT RAW n FROM items s LIMIT 1) FROM items d")]
    public async Task ExecuteAsync_InnerBinding_ShouldPreserveLocalScopeAsync(string query)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("shadow-regression", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await DocumentSubqueryTests.SeedAsync(session);
        // Act / Assert
        (await DocumentSubqueryTests.RowsAsync(session, query)).Count.ShouldBe(3);
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Subqueries: function kind validation treats subqueries as literal arrays")]
    [InlineData("LENGTH({v})")]
    [InlineData("ABS({v})")]
    [InlineData("CONCAT({v}, 'a')")]
    public async Task ExecuteAsync_ArrayArgument_ShouldRefuseBeforeExecutionAsync(string template)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("kind-regression", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await DocumentSubqueryTests.SeedAsync(session);
        // Act / Assert
        foreach (string value in new[] { "[1]", "(SELECT RAW 1 FROM items LIMIT 1)" })
        {
            var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(
                "SELECT " + template.Replace("{v}", value, StringComparison.Ordinal) + " FROM items LIMIT 1", cancellationToken: CancellationToken.None));
            error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD05001");
            session.LastQuerySubqueriesExecuted.ShouldBe(0);
        }
    }

    internal static Dictionary<string, object?> DeepParameters(int depth = 255)
    {
        using var json = JsonDocument.Parse(new string('[', depth) + "0" + new string(']', depth), new JsonDocumentOptions { MaxDepth = 256 });
        return new() { ["deep"] = json.RootElement.Clone() };
    }
}
