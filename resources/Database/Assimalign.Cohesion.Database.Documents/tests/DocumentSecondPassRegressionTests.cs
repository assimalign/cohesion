using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Documents.Tests;

public sealed class DocumentSecondPassRegressionTests
{
    [Theory(DisplayName = "Cohesion Test [Documents] - Grouped captures: implicit collection names identify the local source")]
    [InlineData("SELECT RAW (SELECT RAW items.g FROM [1] AS s) FROM items GROUP BY g", "[7]")]
    [InlineData("SELECT RAW (SELECT RAW META(items).id FROM [1] AS s) FROM items GROUP BY META().id", "[\"a\"]")]
    public async Task ExecuteAsync_ImplicitGroupedSource_ShouldMatchKeyAsync(string query, string expected)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("implicit-group", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.CreateCollectionAsync("items", CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO items (KEY, VALUE) VALUES ('a', {g:7})", cancellationToken: CancellationToken.None);
        // Act
        var rows = await DocumentSubqueryTests.RowsAsync(session, query);
        // Assert
        ((JsonElement)rows.Single().GetValue(0)!).GetRawText().ShouldBe(expected);
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - Implicit META: identity paging is retained and explained")]
    public async Task ExecuteAsync_ImplicitMetadataOrder_ShouldPageByIdentityAsync()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("implicit-paging", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await DocumentSubqueryTests.SeedAsync(session);
        const string query = "SELECT RAW META(items).id FROM items ORDER BY META(items).id LIMIT 2";
        // Act
        var plan = await DocumentSubqueryTests.RowsAsync(session, "EXPLAIN " + query);
        var rows = await DocumentSubqueryTests.RowsAsync(session, query);
        // Assert
        plan.Single(row => row.GetString(2) == "Select").GetString(5)!.ShouldContain("identity_paging=True", Case.Sensitive);
        rows.Select(row => row.GetString(0)).ShouldBe(new[] { "a", "b" });
        session.LastQueryDocumentsRead.ShouldBe(2);
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - ORDER BY: projection aliases do not mask direct outer paths or index proofs")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_OrderPathAliasCollision_ShouldKeepOuterIdentityTiesAsync(bool indexed)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("order-collision", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.CreateCollectionAsync("items", CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO items (KEY, VALUE) VALUES ('a', {n:1,d:{x:2}}), ('b', {n:2,d:{x:1}})", cancellationToken: CancellationToken.None);
        if (indexed) { await session.ExecuteAsync("CREATE INDEX ix ON items (d.x INCLUDE MISSING)", cancellationToken: CancellationToken.None); }
        const string query = "SELECT RAW (SELECT s.n AS d FROM items s ORDER BY d.x) FROM items d WHERE d.n = 1";
        // Act
        var rows = await DocumentSubqueryTests.RowsAsync(session, query);
        var plan = await DocumentSubqueryTests.RowsAsync(session, "EXPLAIN " + query);
        // Assert
        ((JsonElement)rows.Single().GetValue(0)!).GetRawText().ShouldBe("[{\"d\":1},{\"d\":2}]");
        plan.Single(row => row.GetString(2) == "SubqueryExpression").GetString(5)!.ShouldContain("correlation=d", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - ORDER BY: computed paths colliding with projection aliases remain correlated")]
    public async Task ExecuteAsync_ComputedOrderAliasCollision_ShouldEvaluateForEveryOuterRowAsync()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("order-cache", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await DocumentSubqueryTests.SeedAsync(session);
        // Act
        var rows = await DocumentSubqueryTests.RowsAsync(session,
            "SELECT RAW (SELECT s.n AS d FROM items s ORDER BY ABS(d.n - s.n), s.n) FROM items d ORDER BY d.n");
        // Assert
        rows.Select(row => ((JsonElement)row.GetValue(0)!).GetRawText()).ShouldBe(new[]
        { "[{\"d\":1},{\"d\":2},{\"d\":3}]", "[{\"d\":2},{\"d\":1},{\"d\":3}]", "[{\"d\":3},{\"d\":2},{\"d\":1}]" });
        session.LastQuerySubqueriesExecuted.ShouldBe(3);
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Grouping: hidden ORDER BY captures and reused source names retain local restrictions")]
    [InlineData("SELECT RAW (SELECT s.n AS d FROM items s ORDER BY ABS(d.n - s.n)) FROM items d GROUP BY d.g")]
    [InlineData("SELECT RAW ARRAY (SELECT d.n AS x FROM [1] AS s ORDER BY x.n) FOR x IN [0] END FROM [{n:10}] AS d UNNEST [{n:1}] AS x GROUP BY (SELECT d.n AS x FROM [1] AS s ORDER BY x.n)")]
    [InlineData("SELECT RAW (SELECT (SELECT RAW d.n FROM [1] AS t) AS x FROM d.arr AS d GROUP BY d.g) FROM [{arr:[{g:1,n:10},{g:1,n:20}]}] AS d")]
    [InlineData("SELECT RAW (SELECT d.n FROM d.arr AS d GROUP BY d.g) FROM [{arr:[{g:1,n:10},{g:1,n:20}]}] AS d")]
    [InlineData("SELECT RAW (SELECT d.n FROM [d.arr] AS s UNNEST s AS d GROUP BY d.g) FROM [{arr:[{g:1,n:10},{g:1,n:20}]}] AS d")]
    [InlineData("SELECT RAW (SELECT (SELECT RAW d.n FROM [1] AS t) AS x FROM [d.arr] AS s UNNEST s AS d GROUP BY d.g) FROM [{arr:[{g:1,n:10},{g:1,n:20}]}] AS d")]
    public async Task ExecuteAsync_HiddenLocalField_ShouldRefuseGroupingAsync(string query)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("hidden-group", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await DocumentSubqueryTests.SeedAsync(session);
        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(query, cancellationToken: CancellationToken.None));
        // Assert
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD05001");
        session.LastQuerySubqueriesExecuted.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - Grouping: complete subquery keys match before descending another level")]
    public async Task ExecuteAsync_DeeperSubqueryKey_ShouldMatchBeforeRecursingAsync()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("deeper-key", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        // Act
        var rows = await DocumentSubqueryTests.RowsAsync(session,
            "SELECT RAW (SELECT RAW (SELECT RAW d.n FROM [1] AS s) FROM [1] AS t) FROM [{n:10}] AS d GROUP BY (SELECT RAW d.n FROM [1] AS s)");
        // Assert
        ((JsonElement)rows.Single().GetValue(0)!).GetRawText().ShouldBe("[[10]]");
    }
}
