using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Documents.Internal;
using Assimalign.Cohesion.Database.Documents.Language;

namespace Assimalign.Cohesion.Database.Documents.Tests;

public sealed partial class DocumentQueryTests
{
    [Theory(DisplayName = "Cohesion Test [Documents] - UNNEST: mixed shapes preserve only LEFT rows")]
    [InlineData("{}")]
    [InlineData("{\"a\":null}")]
    [InlineData("{\"a\":2}")]
    [InlineData("{\"a\":{}}")]
    [InlineData("{\"a\":[]}")]
    [InlineData("{\"a\":{\"b\":null}}")]
    public async Task Execute_UnnestMixedShapes_ShouldPreserveLeftMissingAsync(string json)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("arrays", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var collection = await session.CreateCollectionAsync("items", CancellationToken.None);
        await PutAsync(collection, session, "one", json);
        // Act / Assert: navigation through NULL and reading NULL both fail expansion.
        foreach (string path in new[] { "d.a", "d.a.b" })
        {
            (await RowsAsync(session, $"SELECT t FROM items d UNNEST {path} t")).ShouldBeEmpty();
            var row = (await RowsAsync(session, $"SELECT t FROM items d LEFT OUTER UNNEST {path} t")).ShouldHaveSingleItem();
            row.IsMissing(0).ShouldBeTrue();
            row.IsNull(0).ShouldBeFalse();
            row.GetValue(0).ShouldBeNull();
        }
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - UNNEST: chained rows retain identity and array order and metadata")]
    public async Task Execute_ChainedUnnest_ShouldPreserveTupleOrderAsync()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("arrays", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var collection = await session.CreateCollectionAsync("items", CancellationToken.None);
        await PutAsync(collection, session, "b", "{\"orders\":[{\"lines\":[3]}]}");
        await PutAsync(collection, session, "a", "{\"orders\":[{\"lines\":[2,1]},{\"lines\":[null]}]}");
        // Act
        var rows = await RowsAsync(session, "SELECT META(d).id AS id, l FROM items d UNNEST d.orders o UNNEST o.lines l");
        // Assert
        rows.Select(row => row.GetValue(0)).ShouldBe(new object?[] { "a", "a", "a", "b" });
        rows.Select(row => row.GetValue(1)).ShouldBe(new object?[] { 2m, 1m, null, 3m });
        rows[2].IsNull(1).ShouldBeTrue();
        (await RowsAsync(session, "SELECT l FROM items d UNNEST d.orders o UNNEST o.lines l WHERE META().id = 'a' AND l > 1"))
            .ShouldHaveSingleItem().GetValue(0).ShouldBe(2m);
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - UNNEST: star builds owned synthetic objects and plain star preserves bytes")]
    public async Task Execute_UnnestStar_ShouldReturnSyntheticDocumentsAsync()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("arrays", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var collection = await session.CreateCollectionAsync("items", CancellationToken.None);
        const string json = " {\"tags\":[\"blue\",null]} ";
        await PutAsync(collection, session, "a", json);
        await PutAsync(collection, session, "b", "{}");
        // Act / Assert
        foreach (var (source, key, path) in new[] { ("items d", "d", "d.tags"), ("items", "items", "tags") })
        {
            var rows = await RowsAsync(session, $"SELECT * FROM {source} LEFT UNNEST {path} t");
            rows.Count.ShouldBe(3);
            var first = rows[0].GetValue(0).ShouldBeOfType<JsonElement>();
            first.GetProperty(key).GetProperty("tags")[0].GetString().ShouldBe("blue");
            first.GetProperty("t").GetString().ShouldBe("blue");
            rows[1].GetValue(0).ShouldBeOfType<JsonElement>().GetProperty("t").ValueKind.ShouldBe(JsonValueKind.Null);
            rows[2].GetValue(0).ShouldBeOfType<JsonElement>().TryGetProperty("t", out _).ShouldBeFalse();
            Encoding.UTF8.GetString(rows[0].GetBytes(0).Span).ShouldContain("\"t\":\"blue\"", Case.Sensitive);
        }
        Encoding.UTF8.GetString((await RowsAsync(session, "SELECT * FROM items WHERE META().id = 'a'")).Single().GetBytes(0).Span).ShouldBe(json);
        // An unaliased source still reads an actual field named items, not an implicit variable.
        (await RowsAsync(session, "SELECT items.tags FROM items UNNEST tags t")).ShouldAllBe(row => row.IsMissing(0));
        var collision = await RowsAsync(session, "SELECT * FROM items LEFT UNNEST tags items");
        collision[0].GetValue(0).ShouldBeOfType<JsonElement>().GetProperty("items").GetString().ShouldBe("blue");
        collision[2].GetValue(0).ShouldBeOfType<JsonElement>().GetProperty("items").ValueKind.ShouldBe(JsonValueKind.Object);
        (await RowsAsync(session, "SELECT \" \" FROM items d UNNEST d.tags AS \" \""))
            .Select(row => row.GetValue(0)).ShouldBe(new object?[] { "blue", null });
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - UNNEST: array roots expand and scalar roots preserve LEFT MISSING")]
    public async Task Execute_UnnestRoot_ShouldHandleScalarDocumentsAsync()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("arrays", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var collection = await session.CreateCollectionAsync("items", CancellationToken.None);
        await PutAsync(collection, session, "a", "[1,{\"a\":true}]");
        await PutAsync(collection, session, "b", "42");
        await PutAsync(collection, session, "c", "\"text\"");
        // Act
        var inner = await RowsAsync(session, "SELECT e FROM items d UNNEST d e");
        var left = await RowsAsync(session, "SELECT e FROM items d LEFT UNNEST d e");
        // Assert
        inner.Count.ShouldBe(2);
        inner[0].GetValue(0).ShouldBe(1m);
        inner[1].GetValue(0).ShouldBeOfType<JsonElement>().GetProperty("a").GetBoolean().ShouldBeTrue();
        left.Count.ShouldBe(4);
        left.Skip(2).ShouldAllBe(row => row.IsMissing(0));
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - UNNEST: grouping counts expanded rows and separates NULL from MISSING")]
    public async Task Execute_UnnestGrouping_ShouldAggregateRowsAsync()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("arrays", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var collection = await session.CreateCollectionAsync("items", CancellationToken.None);
        await PutAsync(collection, session, "a", "{\"tags\":[null,2,2,3]}");
        await PutAsync(collection, session, "b", "{}");
        // Act
        var rows = await RowsAsync(session, "SELECT t, COUNT(*) AS n, SUM(t) AS s FROM items d LEFT UNNEST d.tags t GROUP BY t HAVING COUNT(*) > 0 ORDER BY t");
        // Assert
        rows.Count.ShouldBe(4);
        rows[0].IsMissing(0).ShouldBeTrue();
        rows[1].IsNull(0).ShouldBeTrue();
        rows[2].GetValue(0).ShouldBe(2m);
        rows[2].GetValue(1).ShouldBe(2L);
        rows[2].GetValue(2).ShouldBe(4m);
        (await RowsAsync(session, "SELECT ANY x IN d.tags SATISFIES x > 1 END AS yes, COUNT(*) AS n FROM items d GROUP BY d.tags"))
            .Count.ShouldBe(2);
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - Arrays: inner variables shadow fields and enclosing quantifiers")]
    public async Task Execute_QuantifierScope_ShouldResolveInnerBindingsAsync()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("arrays", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var collection = await session.CreateCollectionAsync("items", CancellationToken.None);
        await PutAsync(collection, session, "a", "{\"x\":{\"items\":[1]},\"tags\":[1,2],\"t\":99}");
        // Act / Assert
        foreach (string predicate in new[]
        {
            "ANY x IN x.items SATISFIES x = 1 END",
            "ANY d IN d.tags SATISFIES d = 2 END",
            "ANY x IN [1] SATISFIES EVERY x IN [2] SATISFIES x = 2 END AND x = 1 END",
            "ANY x IN d.tags SATISFIES x = t END",
        })
        {
            (await RowsAsync(session, $"SELECT 1 FROM items d UNNEST [1] t WHERE {predicate}")).Count.ShouldBe(1);
        }
        (await RowsAsync(session, "SELECT 1 FROM items d UNNEST d.tags t WHERE ANY t IN [3] SATISFIES t = 3 END AND t = 2"))
            .Count.ShouldBe(1);
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Arrays: metadata aggregates and missing inputs fail before empty execution")]
    [InlineData("SELECT ANY d IN [1] SATISFIES META(d).id = 'a' END FROM items d", "COHDOC001")]
    [InlineData("SELECT ANY x IN [1] SATISFIES META(x).id = 'a' END FROM items d", "COHDOC001")]
    [InlineData("SELECT META(t).id FROM items d UNNEST d.tags t", "COHDOC001")]
    [InlineData("SELECT ANY x IN [1] SATISFIES COUNT(*) > 0 END FROM items", "COHDOC001")]
    [InlineData("SELECT * FROM items d UNNEST [COUNT(*)] t", "COHDOC001")]
    [InlineData("SELECT ANY x IN [1] SATISFIES @absent END FROM items", "COHDOC005")]
    [InlineData("SELECT 1 FROM items UNNEST @absent t", "COHDOC005")]
    public async Task Execute_InvalidArrayContext_ShouldRejectBeforeReadingAsync(string query, string code)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("arrays", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.CreateCollectionAsync("items", CancellationToken.None);
        // Act
        var exception = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(query, cancellationToken: CancellationToken.None));
        // Assert
        exception.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(code);
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - Arrays: variable paths never seek document indexes")]
    public async Task Plan_UnnestVariable_ShouldKeepDocumentIndexSafetyAsync()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("arrays", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var collection = await session.CreateCollectionAsync("items", CancellationToken.None);
        await PutAsync(collection, session, "a", "{\"name\":\"alpha\",\"t\":\"red\",\"tags\":[\"blue\",\"green\"]}");
        await PutAsync(collection, session, "b", "{\"name\":\"beta\",\"t\":\"blue\",\"tags\":[]}");
        string[] predicates = ["t = 'blue'", "t BETWEEN 'blue' AND 'blue'", "ANY t IN d.tags SATISFIES d.name = 'alpha' END", "EVERY t IN d.tags SATISFIES d.name = 'alpha' END"];
        var expected = new List<object?[]>();
        foreach (string predicate in predicates)
        { expected.Add((await RowsAsync(session, $"SELECT d.name FROM items d LEFT UNNEST d.tags t WHERE {predicate}")).Select(row => row.GetValue(0)).ToArray()); }
        await session.ExecuteAsync("CREATE INDEX by_t ON items (t)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("CREATE INDEX by_name ON items (name)", cancellationToken: CancellationToken.None);
        // Act / Assert
        for (int i = 0; i < predicates.Length; i++)
        {
            string query = $"SELECT d.name FROM items d LEFT UNNEST d.tags t WHERE {predicates[i]}";
            (await PlanAsync(database, query)).Access.ShouldBeOfType<DocumentScanPath>();
            (await RowsAsync(session, query)).Select(row => row.GetValue(0)).ShouldBe(expected[i]);
        }
        (await PlanAsync(database, "SELECT t FROM items d UNNEST d.tags t WHERE d.name = 'alpha' AND t = 'blue'"))
            .Access.ShouldBeOfType<DocumentIndexPath>().Index.Name.ShouldBe("by_name");
        var explain = await RowsAsync(session, "EXPLAIN SELECT t FROM items d UNNEST d.tags t LEFT UNNEST [1] u");
        explain.Select(row => row.GetValue(2)).ShouldBe(new object?[] { "Select", "Project", "LeftUnnest", "Unnest", "CollectionScan" });
        explain[2].GetValue(5).ShouldBe("u");
        explain[3].GetValue(5).ShouldBe("t");
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - UNNEST: projection aliases outrank variables and VALUE ordering uses scope")]
    public async Task Execute_UnnestOrdering_ShouldRespectBindingsAsync()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("arrays", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var collection = await session.CreateCollectionAsync("items", CancellationToken.None);
        await PutAsync(collection, session, "a", "{\"t\":2,\"tags\":[1,3]}");
        await PutAsync(collection, session, "b", "{\"t\":1,\"tags\":[2]}");
        // Act / Assert
        (await RowsAsync(session, "SELECT VALUE t FROM items d UNNEST d.tags t ORDER BY d.t"))
            .Select(row => row.GetValue(0)).ShouldBe(new object?[] { 2m, 1m, 3m });
        (await RowsAsync(session, "SELECT -t AS t FROM items d UNNEST d.tags t ORDER BY t"))
            .Select(row => row.GetValue(0)).ShouldBe(new object?[] { -3m, -2m, -1m });
        await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("SELECT DISTINCT t FROM items d UNNEST d.tags t ORDER BY d.t", cancellationToken: CancellationToken.None));
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - UNNEST: paging counts expanded matches and stops between elements")]
    public async Task Execute_UnnestPaging_ShouldCountOutputRowsAsync()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("arrays", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var collection = await session.CreateCollectionAsync("items", CancellationToken.None);
        await PutAsync(collection, session, "a", "{\"tags\":[]}");
        await PutAsync(collection, session, "b", "{\"tags\":[2,1,0]}");
        // Act / Assert
        (await RowsAsync(session, "SELECT ABS(t) FROM items d UNNEST d.tags t WHERE 1 / t > 0 LIMIT 1 OFFSET 1"))
            .ShouldHaveSingleItem().GetValue(0).ShouldBe(1m);
        (await RowsAsync(session, "SELECT META().id FROM items d UNNEST d.tags t WHERE 1 / t > 0 LIMIT 1"))
            .ShouldHaveSingleItem().GetValue(0).ShouldBe("b");
        // Unknown column types retain #1129's full-input type inference.
        await Should.ThrowAsync<DatabaseException>(async () => await RowsAsync(session,
            "SELECT t FROM items d UNNEST d.tags t WHERE 1 / t > 0 LIMIT 1"));
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - Arrays: virtual sources and write queries share tuple evaluation")]
    public async Task Execute_ArrayQueries_ShouldSupportVirtualAndWriteSourcesAsync()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("arrays", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var collection = await session.CreateCollectionAsync("items", CancellationToken.None);
        await session.CreateCollectionAsync("copies", CancellationToken.None);
        await PutAsync(collection, session, "a", "{\"tags\":[\"blue\",\"green\"]}");
        await PutAsync(collection, session, "b", "{\"tags\":[]}");
        await session.ExecuteAsync("CREATE INDEX by_tags ON items (tags)", cancellationToken: CancellationToken.None);
        // Act / Assert
        (await RowsAsync(session, "SELECT t FROM COHESION_SCHEMA.INDEXES s UNNEST s.PATHS t WHERE t = 'tags'"))
            .ShouldHaveSingleItem().GetValue(0).ShouldBe("tags");
        var systemStar = (await RowsAsync(session, "SELECT * FROM COHESION_SCHEMA.INDEXES UNNEST PATHS t")).ShouldHaveSingleItem();
        systemStar.GetValue(0).ShouldBeOfType<JsonElement>().GetProperty("COHESION_SCHEMA.INDEXES").GetProperty("INDEX_NAME").GetString().ShouldBe("by_tags");
        var explained = await RowsAsync(session, "EXPLAIN SELECT t FROM COHESION_SCHEMA.INDEXES s UNNEST s.PATHS t");
        explained.Select(row => row.GetValue(2)).ShouldBe(new object?[] { "SystemCollection", "Project", "Unnest", "SystemCollectionScan" });
        await session.ExecuteAsync("INSERT INTO copies (KEY k, VALUE v) SELECT t AS k, {tag:t} AS v FROM items d UNNEST d.tags t", cancellationToken: CancellationToken.None);
        (await RowsAsync(session, "SELECT tag FROM copies")).Select(row => row.GetValue(0)).ShouldBe(new object?[] { "blue", "green" });
        await session.ExecuteAsync("UPDATE items SET found = TRUE WHERE SOME t IN tags SATISFIES t = 'blue' END", cancellationToken: CancellationToken.None);
        (await RowsAsync(session, "SELECT found FROM items WHERE 'blue' IN tags")).ShouldHaveSingleItem().GetValue(0).ShouldBe(true);
        await session.ExecuteAsync("DELETE FROM items WHERE EVERY t IN tags SATISFIES FALSE END", cancellationToken: CancellationToken.None);
        (await RowsAsync(session, "SELECT META().id FROM items")).ShouldHaveSingleItem().GetValue(0).ShouldBe("a");
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - Arrays: a direct AST cannot repeat a binding")]
    public void Plan_DirectDuplicateBinding_ShouldReportDocumentCode()
    {
        // Arrange
        var query = new OqlSelectExpression("items", "d", [new OqlProjection(new OqlStarExpression(), null)],
            null, [], null, [], false, false, unnest: [new OqlUnnestExpression(new OqlArrayExpression([]), "d")]);
        // Act
        var exception = Should.Throw<DocumentPlanningException>(() => DocumentPlanner.CreateLogicalPlan(query));
        // Assert
        exception.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("COHDOC023");
        exception.Message.ShouldBe("An OQL binding list cannot repeat a variable name.");
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - Arrays: large arrays support early paging and cancellation")]
    public async Task Execute_LargeArray_ShouldObserveCancellationAsync()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("arrays", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var collection = await session.CreateCollectionAsync("items", CancellationToken.None);
        await PutAsync(collection, session, "a", "{\"tags\":[" + string.Join(',', Enumerable.Repeat("1", 200000)) + "]}");
        // Act / Assert
        (await RowsAsync(session, "SELECT 1 FROM items d UNNEST d.tags t LIMIT 2 OFFSET 1")).Count.ShouldBe(2);
        using var cancellation = new CancellationTokenSource();
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(10));
        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await session.ExecuteAsync("SELECT COUNT(*) FROM items d UNNEST d.tags t UNNEST d.tags u", cancellationToken: cancellation.Token));
    }
}
