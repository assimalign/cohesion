using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Documents.Internal;
using Assimalign.Cohesion.Database.Execution;

namespace Assimalign.Cohesion.Database.Documents.Tests;

public sealed class DocumentSubqueryTests
{
    [Theory(DisplayName = "Cohesion Test [Documents] - Subqueries: arrays and lazy operators execute on the nested context")]
    [InlineData("SELECT (SELECT RAW n FROM items WHERE n > 10) FROM items LIMIT 1", "[]")]
    [InlineData("SELECT (SELECT RAW n FROM items WHERE n = 1) FROM items LIMIT 1", "[1]")]
    [InlineData("SELECT (SELECT n AS x FROM items WHERE n > 10) FROM items LIMIT 1", "[]")]
    [InlineData("SELECT (SELECT n AS x FROM items WHERE n = 1) FROM items LIMIT 1", "[{\"x\":1}]")]
    [InlineData("SELECT (SELECT RAW n FROM items) FROM items LIMIT 1", "[1,2,3]")]
    [InlineData("SELECT (SELECT n AS d FROM items ORDER BY d LIMIT 1) FROM items d LIMIT 1", "[{\"d\":1}]")]
    [InlineData("SELECT (SELECT RAW d FROM items s UNNEST [1] d LIMIT 1) FROM items d LIMIT 1", "[1]")]
    [InlineData("SELECT (SELECT n AS x FROM items) FROM items LIMIT 1", "[{\"x\":1},{\"x\":2},{\"x\":3}]")]
    [InlineData("SELECT (SELECT RAW (SELECT RAW n FROM items WHERE n = 1) FROM items LIMIT 1) FROM items LIMIT 1", "[[1]]")]
    [InlineData("SELECT ARRAY_LENGTH((SELECT RAW n FROM items)) FROM items LIMIT 1", "3")]
    [InlineData("SELECT SUM(ARRAY_LENGTH((SELECT RAW n FROM items))) FROM items", "9")]
    [InlineData("SELECT ARRAY_LENGTH(ARRAY_AGG((SELECT RAW n FROM items))) FROM items", "3")]
    [InlineData("SELECT ARRAY (SELECT RAW n FROM items WHERE n = 1) FOR x IN [1,2] END FROM items LIMIT 1", "[[1],[1]]")]
    [InlineData("SELECT FIRST (SELECT RAW n FROM items WHERE n = 1) FOR x IN [1,2] END FROM items LIMIT 1", "[1]")]
    [InlineData("SELECT EVERY x IN [1,2] SATISFIES EXISTS (SELECT RAW n FROM items) END FROM items LIMIT 1", "True")]
    [InlineData("SELECT {x:(SELECT RAW n FROM items WHERE n = 1)} FROM items LIMIT 1", "{\"x\":[1]}")]
    [InlineData("SELECT [(SELECT RAW n FROM items WHERE n = 1)] FROM items LIMIT 1", "[[1]]")]
    [InlineData("SELECT (SELECT RAW n FROM items WHERE n = 1) BETWEEN [] AND [9] FROM items LIMIT 1", "False")]
    [InlineData("SELECT DISTINCT (SELECT RAW n FROM items) AS a FROM items ORDER BY a", "[1,2,3]")]
    [InlineData("SELECT DISTINCT (SELECT RAW n FROM items) FROM items ORDER BY (SELECT RAW n FROM items)", "[1,2,3]")]
    [InlineData("SELECT (SELECT RAW INDEX_NAME FROM COHESION_SCHEMA.INDEXES) FROM items LIMIT 1", "[]")]
    [InlineData("SELECT (SELECT RAW TYPE(1) FROM items) LIKE 'x' FROM items LIMIT 1", null)]
    [InlineData("SELECT CASE WHEN TRUE THEN 7 ELSE (SELECT RAW 1/0 FROM items) END FROM items LIMIT 1", "7")]
    [InlineData("SELECT FALSE AND EXISTS (SELECT RAW 1/0 FROM items) FROM items LIMIT 1", "False")]
    [InlineData("SELECT TRUE OR EXISTS (SELECT RAW 1/0 FROM items) FROM items LIMIT 1", "True")]
    [InlineData("SELECT COALESCE(7, (SELECT RAW 1/0 FROM items)) FROM items LIMIT 1", "7")]
    [InlineData("SELECT IFMISSING(7, (SELECT RAW 1/0 FROM items)) FROM items LIMIT 1", "7")]
    public async Task ExecuteAsync_ExpressionSubqueries_ShouldPreserveArraysAndLazinessAsync(string query, string? expected)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("subqueries", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);
        // Act
        var rows = await RowsAsync(session, query);
        // Assert
        rows.ShouldHaveSingleItem().GetString(0).ShouldBe(expected);
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - EXISTS: collection truth table and ordinary NOT")]
    [InlineData("EXISTS [1]", true, false)]
    [InlineData("EXISTS []", false, false)]
    [InlineData("EXISTS MISSING", null, true)]
    [InlineData("EXISTS NULL", null, false)]
    [InlineData("EXISTS 1", null, false)]
    [InlineData("EXISTS 'x'", null, false)]
    [InlineData("EXISTS {}", null, false)]
    [InlineData("NOT EXISTS []", true, false)]
    [InlineData("NOT EXISTS [1]", false, false)]
    [InlineData("NOT EXISTS NULL", null, false)]
    [InlineData("NOT EXISTS MISSING", null, true)]
    [InlineData("EXISTS {n:1}", null, false)]
    [InlineData("EXISTS TRUE", null, false)]
    public async Task ExecuteAsync_Exists_ShouldApplyCollectionTruthTableAsync(string expression, bool? expected, bool missing)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("exists", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);
        // Act
        var row = (await RowsAsync(session, "SELECT " + expression + " FROM items LIMIT 1")).Single();
        // Assert
        row.IsMissing(0).ShouldBe(missing);
        row.GetValue(0).ShouldBe(expected);
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Subqueries: all SELECT expression positions and IN unknowns")]
    [InlineData("SELECT n FROM items WHERE EXISTS (SELECT RAW n FROM items WHERE n = 1)", 3)]
    [InlineData("SELECT COUNT(*) FROM items GROUP BY (SELECT RAW n FROM items) HAVING EXISTS (SELECT RAW n FROM items) ORDER BY (SELECT RAW n FROM items)", 1)]
    [InlineData("SELECT n FROM items WHERE n NOT IN (SELECT RAW n FROM items WHERE n = 1)", 2)]
    [InlineData("SELECT v FROM items UNNEST (SELECT RAW n FROM items) AS v", 9)]
    [InlineData("SELECT (SELECT RAW n FROM items WHERE n = 1) FROM COHESION_SCHEMA.OBJECT_OWNERSHIP", 1)]
    [InlineData("SELECT n FROM items WHERE NULL IN (SELECT RAW n FROM items)", 0)]
    [InlineData("SELECT n FROM items WHERE MISSING NOT IN (SELECT RAW n FROM items)", 0)]
    [InlineData("SELECT n FROM items WHERE n NOT IN (SELECT RAW NULL FROM items)", 0)]
    [InlineData("SELECT n FROM items WHERE n NOT IN (SELECT RAW MISSING FROM items)", 0)]
    public async Task ExecuteAsync_SelectPositions_ShouldEvaluateNestedArraysAsync(string query, int count)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("positions", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);
        // Act / Assert
        (await RowsAsync(session, query)).Count.ShouldBe(count);
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - Subqueries: deterministic plans cache and developer callbacks execute per evaluation")]
    public async Task ExecuteAsync_Caching_ShouldRespectCallbackEffectsAsync()
    {
        // Arrange
        int calls = 0;
        var builder = DocumentDatabaseEngine.CreateBuilder("cache");
        builder.Functions.Add(DocumentScalarFunction.Create<decimal, decimal>("tick", value => { calls++; return value; }));
        await using var engine = builder.Build();
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);
        // Act / Assert
        (await RowsAsync(session, "SELECT (SELECT RAW n FROM items LIMIT 1) FROM items")).Count.ShouldBe(3);
        session.LastQuerySubqueriesExecuted.ShouldBe(1);
        (await RowsAsync(session, "SELECT (SELECT RAW tick(n) FROM items LIMIT 1) FROM items")).Count.ShouldBe(3);
        session.LastQuerySubqueriesExecuted.ShouldBe(3);
        calls.ShouldBe(3);
        (await RowsAsync(session, "SELECT (SELECT RAW NOW_MILLIS() FROM items LIMIT 1) FROM items")).Count.ShouldBe(3);
        session.LastQuerySubqueriesExecuted.ShouldBe(3);
        await RowsAsync(session, "SELECT CASE WHEN TRUE THEN 7 ELSE (SELECT RAW tick(n) FROM items) END FROM items");
        calls.ShouldBe(3);
        session.LastQuerySubqueriesExecuted.ShouldBe(0);
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Subquery IN: result cells preserve NULL and MISSING")]
    [InlineData("NULL IN (SELECT RAW n FROM items WHERE n > 10)", null, false)]
    [InlineData("MISSING IN (SELECT RAW n FROM items)", null, true)]
    [InlineData("1 IN (SELECT RAW NULL FROM items)", null, false)]
    [InlineData("1 NOT IN (SELECT RAW MISSING FROM items)", null, false)]
    [InlineData("1 IN (SELECT RAW n FROM items WHERE n < 3)", true, false)]
    [InlineData("9 NOT IN (SELECT RAW n FROM items WHERE n > 10)", true, false)]
    public async Task ExecuteAsync_SubqueryMembership_ShouldPreserveUnknownKindsAsync(string expression, bool? expected, bool missing)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("membership-subqueries", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);
        // Act
        var row = (await RowsAsync(session, "SELECT " + expression + " FROM items LIMIT 1")).Single();
        // Assert
        row.IsMissing(0).ShouldBe(missing);
        row.GetValue(0).ShouldBe(expected);
    }

    internal static async Task SeedAsync(DocumentDatabaseSession session)
    {
        await session.CreateCollectionAsync("items", CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO items (KEY, VALUE) VALUES ('a', {n:1}), ('b', {n:2}), ('c', {n:3})", cancellationToken: CancellationToken.None);
    }

    internal static async Task<List<DatabaseRow>> RowsAsync(DocumentDatabaseSession session, string query)
    {
        await using var result = (DatabaseResultSet)await session.ExecuteAsync(query, cancellationToken: CancellationToken.None);
        var rows = new List<DatabaseRow>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None)) { rows.Add(row); }
        return rows;
    }
}
