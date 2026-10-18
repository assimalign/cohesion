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
using Assimalign.Cohesion.Database.Documents.Tests.TestObjects;

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
        var collision = await Should.ThrowAsync<DatabaseException>(async () =>
            await session.ExecuteAsync("SELECT * FROM items LEFT UNNEST tags items", cancellationToken: CancellationToken.None));
        collision.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("OQL0010");
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

    [Theory(DisplayName = "Cohesion Test [Documents] - Grouping: quantifier bindings cannot match outer grouping names")]
    [InlineData(false, "t", "")]
    [InlineData(true, "t", "")]
    [InlineData(false, "t", "UNNEST [-1] t")]
    [InlineData(false, "d", "")]
    public async Task Execute_GroupKeyShadowing_ShouldRejectUngroupedFieldAsync(bool reverse, string variable, string unnest)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("group-scope", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var collection = await session.CreateCollectionAsync("items", CancellationToken.None);
        await PutAsync(collection, session, "a", reverse ? "{\"x\":-1,\"t\":1,\"d\":1}" : "{\"x\":1,\"t\":-1,\"d\":-1}");
        await PutAsync(collection, session, "b", reverse ? "{\"x\":1,\"t\":-1,\"d\":-1}" : "{\"x\":-1,\"t\":1,\"d\":1}");
        string expression = $"ANY {variable} IN [0] SATISFIES s.x + {variable} > 0 END";
        string source = $"FROM items s {unnest} GROUP BY s.x + {variable}";

        // Act / Assert: SELECT, HAVING and ORDER BY use the same lexical match.
        foreach (string query in new[]
        {
            $"SELECT {expression} AS yes, COUNT(*) AS n {source}",
            $"SELECT COUNT(*) AS n {source} HAVING {expression}",
            $"SELECT COUNT(*) AS n {source} ORDER BY {expression}",
        })
        {
            var exception = await Should.ThrowAsync<DatabaseException>(async () =>
                await session.ExecuteAsync(query, cancellationToken: CancellationToken.None));
            var diagnostic = exception.Diagnostics.ShouldHaveSingleItem();
            diagnostic.Code.ShouldBe("COHDOC001");
            query[diagnostic.Start!.Value..diagnostic.End!.Value].ShouldBe("s.x");
        }
        // Corresponding local quantifiers still match, including repeated-name nesting.
        (await RowsAsync(session, $"SELECT {expression} AS yes, COUNT(*) AS n FROM items s GROUP BY {expression}"))
            .Count.ShouldBe(2);
        (await RowsAsync(session, "SELECT ANY t IN [1] SATISFIES ANY t IN [2] SATISFIES t = 2 END END AS yes, COUNT(*) AS n FROM items s " +
            "GROUP BY ANY t IN [1] SATISFIES ANY t IN [2] SATISFIES t = 2 END END"))
            .ShouldHaveSingleItem().GetValue(1).ShouldBe(2L);
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - UNNEST: composite and UNIQUE document keys cannot bind array variables")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Plan_UnnestCompositeKey_ShouldScanDocumentRowsAsync(bool unique)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("array-indexes", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var collection = await session.CreateCollectionAsync("items", CancellationToken.None);
        await PutAsync(collection, session, "a", "{\"t\":\"other\",\"name\":\"y\",\"tags\":[\"x\",\"x\"]}");
        await PutAsync(collection, session, "b", "{\"t\":\"x\",\"name\":\"y\",\"tags\":[\"other\"]}");
        await PutAsync(collection, session, "c", "{\"t\":\"third\",\"name\":\"z\",\"tags\":[\"x\"]}");
        await session.ExecuteAsync($"CREATE {(unique ? "UNIQUE " : "")}INDEX by_t_name ON items (t, name)", cancellationToken: CancellationToken.None);
        const string query = "SELECT META(d).id AS id FROM items d UNNEST d.tags t WHERE t = 'x' AND d.name = 'y'";

        // Act / Assert
        (await PlanAsync(database, query)).Access.ShouldBeOfType<DocumentScanPath>();
        (await RowsAsync(session, query)).Select(row => row.GetValue(0)).ShouldBe(new object?[] { "a", "a" });
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - DML: contextual unnest fields remain expressions in SATISFIES")]
    public async Task Execute_UnnestField_ShouldAllowQuantifiedMutationsAsync()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("array-fields", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var collection = await session.CreateCollectionAsync("items", CancellationToken.None);
        await PutAsync(collection, session, "a", "{\"tags\":[1],\"unnest\":true}");
        await PutAsync(collection, session, "b", "{\"tags\":[1],\"unnest\":true}");

        // Act / Assert
        var updated = await session.ExecuteAsync("UPDATE items SET found = ANY x IN tags SATISFIES unnest END WHERE ANY x IN tags SATISFIES unnest END", cancellationToken: CancellationToken.None);
        updated.AffectedCount.ShouldBe(2);
        (await RowsAsync(session, "SELECT found FROM items")).ShouldAllBe(row => row.GetBoolean(0));
        var deleted = await session.ExecuteAsync("DELETE FROM items WHERE ANY x IN tags SATISFIES unnest END", cancellationToken: CancellationToken.None);
        deleted.AffectedCount.ShouldBe(2);
        (await RowsAsync(session, "SELECT * FROM items")).ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Arrays: predicates observe cancellation after element reads begin")]
    [InlineData("ANY x IN tags SATISFIES FALSE END")]
    [InlineData("EVERY x IN tags SATISFIES TRUE END")]
    [InlineData("2 IN tags")]
    public void Evaluate_ArrayExpansionCancellation_ShouldStopWithinElements(string predicate)
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        using var source = new CountingJsonMemory("{\"tags\":[" + string.Join(',', Enumerable.Repeat("1", 200000)) + "]}", cancellation);
        using var document = JsonDocument.Parse(source.Memory);
        var query = (OqlSelectExpression)((OqlQueryStatement)new OqlQueryParser().Parse($"SELECT {predicate} FROM items")).OqlExpression;
        var evaluator = new DocumentExpressionEvaluator(null, null, cancellation.Token);
        source.Arm(64);

        // Act
        Should.Throw<OperationCanceledException>(() => evaluator.Evaluate(query.Projections[0].Expression, new DocumentEvaluationContext(document.RootElement)));

        // Assert: cancellation originated inside the borrowed JSON array, well before exhaustion.
        cancellation.IsCancellationRequested.ShouldBeTrue();
        source.Reads.ShouldBeInRange(64, 70);
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Constructors: constant arrays and objects are cached per evaluator")]
    [InlineData("[1, @value, {a:[2,3]}]")]
    [InlineData("{a:[1, @value], b:{c:2}}")]
    public void Evaluate_ConstantConstructor_ShouldReuseOwnedJson(string expression)
    {
        // Arrange
        var query = (OqlSelectExpression)((OqlQueryStatement)new OqlQueryParser().Parse($"SELECT {expression} FROM items")).OqlExpression;
        var evaluator = new DocumentExpressionEvaluator(null, new Dictionary<string, object?> { ["value"] = 5 });

        // Act
        var first = (JsonElement)evaluator.Evaluate(query.Projections[0].Expression, default)!;
        var second = (JsonElement)evaluator.Evaluate(query.Projections[0].Expression, default)!;

        // Assert: JsonElement equality identifies the same owned JSON document and offset.
        second.Equals(first).ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - Paths: an unbound row does not box its JSON root")]
    public void Evaluate_UnboundPath_ShouldAllocateOnlyLeafValues()
    {
        // Arrange
        using var document = JsonDocument.Parse("{\"flag\":true}");
        var query = (OqlSelectExpression)((OqlQueryStatement)new OqlQueryParser().Parse("SELECT d.flag FROM items d")).OqlExpression;
        var controlQuery = (OqlSelectExpression)((OqlQueryStatement)new OqlQueryParser().Parse("SELECT NULL FROM items d")).OqlExpression;
        var evaluator = new DocumentExpressionEvaluator("d", null);
        var context = new DocumentEvaluationContext(document.RootElement);
        var path = query.Projections[0].Expression;
        var control = controlQuery.Projections[0].Expression;
        evaluator.Evaluate(path, context).ShouldBe(true);
        evaluator.Evaluate(control, context).ShouldBeNull();

        // Act
        long controlAllocated = Measure(control);
        long allocated = Measure(path);

        // Assert: the control evaluates NULL over the same 10,000 row contexts without boxing.
        // Allow one 24-byte Boolean leaf box per evaluation plus 10% on all 64-bit CI legs.
        // d1183905e also boxed the 16-byte JsonElement root (32 bytes per evaluation), so
        // its 560,000-byte excess cannot fit this 264,000-byte allowance in Debug or Release.
        Console.WriteLine($"10,000 path evaluations allocated {allocated:N0} bytes; NULL control {controlAllocated:N0} bytes.");
        (allocated - controlAllocated).ShouldBeLessThanOrEqualTo(264_000L);

        long Measure(OqlExpression expression)
        {
            long minimum = long.MaxValue;
            for (int sample = 0; sample < 3; sample++)
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 10000; i++) { _ = evaluator.Evaluate(expression, context); }
                minimum = Math.Min(minimum, GC.GetAllocatedBytesForCurrentThread() - before);
            }
            return minimum;
        }
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

    [Theory(DisplayName = "Cohesion Test [Documents] - Arrays: a direct AST cannot repeat a binding")]
    [InlineData("d", "d")]
    [InlineData(null, "items")]
    public void Plan_DirectDuplicateBinding_ShouldReportDocumentCode(string? alias, string variable)
    {
        // Arrange
        var query = new OqlSelectExpression("items", alias, [new OqlProjection(new OqlStarExpression(), null)],
            null, [], null, [], false, false, unnest: [new OqlUnnestExpression(new OqlArrayExpression([]), variable)]);
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
        const string star = "SELECT * FROM items d UNNEST d.tags t LIMIT 1";
        (await RowsAsync(session, star)).ShouldHaveSingleItem();
        long before = GC.GetAllocatedBytesForCurrentThread();
        var execution = session.ExecuteAsync(star, cancellationToken: CancellationToken.None);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        execution.IsCompletedSuccessfully.ShouldBeTrue();
        await using var result = (Assimalign.Cohesion.Database.Execution.DatabaseResultSet)await execution;
        int count = 0;
        await foreach (var row in result.GetRowsAsync(CancellationToken.None))
        { row.GetValue(0).ShouldBeOfType<JsonElement>().GetProperty("t").GetInt32().ShouldBe(1); count++; }
        count.ShouldBe(1);
        // One source read, its owned clone and one synthetic object measured 33.6 MB;
        // bound the complete operation independently of the 200,000 expanded rows.
        Console.WriteLine($"200,000-element UNNEST star LIMIT 1 allocated {allocated:N0} bytes.");
        allocated.ShouldBeLessThan(40_000_000L);
        using var cancellation = new CancellationTokenSource();
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(10));
        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await session.ExecuteAsync("SELECT COUNT(*) FROM items d UNNEST d.tags t UNNEST d.tags u", cancellationToken: cancellation.Token));
    }
}
