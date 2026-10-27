using System;
using System.Collections.Generic;
using System.Linq;
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
    public static IEnumerable<object[]> SpeculativeComprehensionCases()
    {
        foreach (string expression in new[]
        {
            "ANY x IN [0] SATISFIES 1 / x > 0 END",
            "EVERY x IN [0] SATISFIES 1 / x > 0 END",
            "ARRAY 1 / x FOR x IN [0] END",
            "FIRST 1 / x FOR x IN [0] END",
        })
        {
            foreach (string predicate in new[]
            {
                "FALSE AND n = ({0})", "FALSE AND ({0}) = n",
                "FALSE AND n BETWEEN ({0}) AND 2", "FALSE AND n BETWEEN 0 AND ({0})",
                "n < 0 AND n = ({0})", "n = ({0}) LIMIT 0",
            })
            {
                yield return [expression, predicate, false];
                yield return [expression, predicate, true];
            }
        }
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Comprehensions: speculative bounds defer errors to visited rows")]
    [MemberData(nameof(SpeculativeComprehensionCases))]
    public async Task Execute_FailingConstantSeekCandidate_ShouldPreserveSkippedEvaluationAsync(string expression, string predicate, bool indexed)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("comprehension-speculation", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var collection = await session.CreateCollectionAsync("items", CancellationToken.None);
        await PutAsync(collection, session, "a", "{\"n\":1}");
        if (indexed) { await session.ExecuteAsync("CREATE INDEX by_n ON items (n)", cancellationToken: CancellationToken.None); }

        // Act
        var rows = await RowsAsync(session, "SELECT 1 FROM items WHERE " + string.Format(System.Globalization.CultureInfo.InvariantCulture, predicate, expression));

        // Assert
        rows.ShouldBeEmpty();
        var failure = await Should.ThrowAsync<DatabaseException>(async () =>
            await session.ExecuteAsync($"SELECT 1 FROM items WHERE n = ({expression})", cancellationToken: CancellationToken.None));
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD02006");
        (await RowsAsync(session, "SELECT n FROM items")).ShouldHaveSingleItem().GetValue(0).ShouldBe(1m);
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Comprehensions: VALUES short circuit closed quantified and mapped expressions")]
    [InlineData("ANY x IN [0] SATISFIES 1 / x > 0 END", false)]
    [InlineData("ANY x IN [0] SATISFIES 1 / x > 0 END", true)]
    [InlineData("EVERY x IN [0] SATISFIES 1 / x > 0 END", false)]
    [InlineData("EVERY x IN [0] SATISFIES 1 / x > 0 END", true)]
    [InlineData("ARRAY 1 / x FOR x IN [0] END", false)]
    [InlineData("ARRAY 1 / x FOR x IN [0] END", true)]
    [InlineData("FIRST 1 / x FOR x IN [0] END", false)]
    [InlineData("FIRST 1 / x FOR x IN [0] END", true)]
    public async Task Execute_SkippedConstantValues_ShouldPreserveShortCircuitingAsync(string expression, bool indexed)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("comprehension-values-skipped", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.CreateCollectionAsync("items", CancellationToken.None);
        if (indexed) { await session.ExecuteAsync("CREATE INDEX by_n ON items (n)", cancellationToken: CancellationToken.None); }

        // Act
        await session.ExecuteAsync($"INSERT INTO items (KEY, VALUE) VALUES ('a', {{n:CASE WHEN TRUE THEN 1 ELSE ({expression}) END, skipped:FALSE AND ({expression})}})", cancellationToken: CancellationToken.None);

        // Assert
        var row = (await RowsAsync(session, "SELECT n, skipped FROM items")).ShouldHaveSingleItem();
        row.GetValue(0).ShouldBe(1m);
        row.GetValue(1).ShouldBe(false);
        var failure = await Should.ThrowAsync<DatabaseException>(async () =>
            await session.ExecuteAsync($"INSERT INTO items (KEY, VALUE) VALUES ('b', {expression})", cancellationToken: CancellationToken.None));
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD02006");
        (await RowsAsync(session, "SELECT n FROM items")).Count.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - Comprehensions: closed VALUES and UNNEST retain owned JSON")]
    public async Task Execute_ComprehensionWriteAndUnnest_ShouldComposeAsync()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("comprehension-values", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.CreateCollectionAsync("items", CancellationToken.None);
        // Act
        await session.ExecuteAsync("INSERT INTO items (KEY, VALUE) VALUES ('a', {tags:ARRAY {n:x} FOR x IN [1,2] END}), " +
            "('b', {tags:ARRAY {n:x} FOR x IN [3] END})", cancellationToken: CancellationToken.None);
        var rows = await RowsAsync(session, "SELECT t.n AS n FROM items d UNNEST ARRAY x FOR x IN d.tags END t ORDER BY t.n");
        // Assert
        rows.Select(row => row.GetValue(0)).ShouldBe(new object?[] { 1m, 2m, 3m });
        (await RowsAsync(session, "SELECT FIRST x FOR x IN tags END AS first FROM items"))[1]
            .GetValue(0).ShouldBeOfType<JsonElement>().GetProperty("n").GetInt32().ShouldBe(3);
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - Comprehensions: direct AST binding validation has a stable identity")]
    public async Task Execute_EmptyComprehensionBinding_ShouldRejectDirectAstAsync()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("comprehension-ast", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.CreateCollectionAsync("items", CancellationToken.None);
        var expression = new OqlComprehensionExpression("", new OqlArrayExpression([]), new OqlMissingExpression());
        var query = new OqlSelectExpression("items", null, [new OqlProjection(expression, "a")], null, [], null, [], false, false);
        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(new DocumentQueryRequest(new OqlQueryStatement(query)), CancellationToken.None));
        // Assert
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD01027");
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Comprehensions: map filter and propagate SQL++ unknowns")]
    [InlineData("ARRAY x FOR x IN [] END", "[]", false, false)]
    [InlineData("ARRAY x FOR x IN [1] WHEN FALSE END", "[]", false, false)]
    [InlineData("ARRAY x.a FOR x IN [{},{a:null},{a:2}] END", "[null,2]", false, false)]
    [InlineData("ARRAY x FOR x IN [0,1,2] WHEN x END", "[1,2]", false, false)]
    [InlineData("ARRAY x FOR x IN [1] WHEN NULL END", "[]", false, false)]
    [InlineData("FIRST x FOR x IN [] END", null, true, false)]
    [InlineData("FIRST x FOR x IN [1] WHEN MISSING END", null, true, false)]
    [InlineData("FIRST x.a FOR x IN [{},{a:2}] END", null, true, false)]
    [InlineData("FIRST x FOR x IN [null,2] END", null, false, true)]
    [InlineData("ARRAY x FOR x IN MISSING END", null, true, false)]
    [InlineData("FIRST x FOR x IN MISSING END", null, true, false)]
    [InlineData("ARRAY x FOR x IN NULL END", null, false, true)]
    [InlineData("FIRST x FOR x IN {} END", null, false, true)]
    [InlineData("ARRAY x FOR x IN 2 END", null, false, true)]
    public async Task Execute_ComprehensionValues_ShouldPreserveSemanticsAsync(string expression, string? json, bool missing, bool nil)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("comprehensions", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var collection = await session.CreateCollectionAsync("items", CancellationToken.None);
        await PutAsync(collection, session, "a", "{}");
        // Act
        var row = (await RowsAsync(session, $"SELECT {expression} AS result FROM items")).ShouldHaveSingleItem();
        // Assert
        row.IsMissing(0).ShouldBe(missing);
        row.IsNull(0).ShouldBe(nil);
        if (json is not null) { row.GetValue(0).ShouldBeOfType<JsonElement>().GetRawText().ShouldBe(json); }
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Comprehensions: nested lexical scopes restore their parents")]
    [InlineData("ARRAY [x, FIRST x FOR x IN [2] END, x] FOR x IN [1] END", "[[1,2,1]]")]
    [InlineData("ARRAY ARRAY x FOR x IN x END FOR x IN [[1,2],[3]] END", "[[1,2],[3]]")]
    [InlineData("ARRAY x FOR x IN x.items END", "[4,5]")]
    [InlineData("ARRAY d FOR d IN d.tags END", "[1,2]")]
    [InlineData("ARRAY x FOR x IN d.tags WHEN ANY x IN [3] SATISFIES x = 3 END END", "[1,2]")]
    [InlineData("ARRAY {n:x} FOR x IN d.tags END", "[{\"n\":1},{\"n\":2}]")]
    [InlineData("ARRAY x FOR x IN d.tags WHEN FALSE END", "[]")]
    public async Task Execute_ComprehensionScope_ShouldResolveLexicalBindingsAsync(string expression, string expected)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("comprehension-scope", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var collection = await session.CreateCollectionAsync("items", CancellationToken.None);
        await PutAsync(collection, session, "a", "{\"x\":{\"items\":[4,5]},\"tags\":[1,2]}");
        // Act
        var row = (await RowsAsync(session, $"SELECT {expression} AS result FROM items d")).ShouldHaveSingleItem();
        // Assert
        row.GetValue(0).ShouldBeOfType<JsonElement>().GetRawText().ShouldBe(expected);
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - Comprehensions: grouped scope matching cannot confuse inner and outer names")]
    public async Task Execute_GroupedComprehension_ShouldRespectBindingIdentityAsync()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("comprehension-groups", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var collection = await session.CreateCollectionAsync("items", CancellationToken.None);
        await PutAsync(collection, session, "a", "{\"n\":1,\"x\":-1,\"tags\":[1,2]}");
        const string expression = "ARRAY d.n + x FOR x IN [0] END";
        // Act / Assert
        foreach (string query in new[]
        {
            $"SELECT {expression} AS a, COUNT(*) AS n FROM items d GROUP BY d.n + x",
            $"SELECT COUNT(*) FROM items d GROUP BY d.n + x HAVING {expression}",
            $"SELECT COUNT(*) FROM items d GROUP BY d.n + x ORDER BY {expression}",
        })
        {
            var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(query, cancellationToken: CancellationToken.None));
            failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD05001");
        }
        (await RowsAsync(session, $"SELECT {expression} AS a, COUNT(*) AS n FROM items d GROUP BY {expression}"))
            .ShouldHaveSingleItem().GetValue(1).ShouldBe(1L);
        (await RowsAsync(session, "SELECT ARRAY x FOR x IN d.tags END AS a, COUNT(*) AS n FROM items d GROUP BY d.tags"))
            .ShouldHaveSingleItem().GetValue(0).ShouldBeOfType<JsonElement>().GetRawText().ShouldBe("[1,2]");
        (await RowsAsync(session, "SELECT SUM(ARRAY_LENGTH(ARRAY x FOR x IN tags END)) AS n FROM items"))
            .ShouldHaveSingleItem().GetValue(0).ShouldBe(2m);
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Comprehensions: validate skipped branches before data access")]
    [InlineData("ARRAY $absent FOR x IN [] END", "CD01005")]
    [InlineData("FIRST 1 FOR x IN [] WHEN $absent END", "CD01005")]
    [InlineData("ARRAY SUM(x) FOR x IN [] END", "CD05001")]
    [InlineData("ARRAY x FOR x IN [1] WHEN META(x).id END", "CD05001")]
    [InlineData("ARRAY META(d).id FOR d IN [] END", "CD05001")]
    public async Task Execute_InvalidComprehension_ShouldValidateEmptyInputsAsync(string expression, string code)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("comprehension-validation", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.CreateCollectionAsync("items", CancellationToken.None);
        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync($"SELECT {expression} FROM items d", cancellationToken: CancellationToken.None));
        // Assert
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(code);
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - Comprehensions: registered arguments and FIRST preserve short circuiting")]
    public async Task Execute_RegisteredComprehension_ShouldKeepOwnedArgumentsAsync()
    {
        // Arrange
        int calls = 0;
        var builder = DocumentDatabaseEngine.CreateBuilder("comprehension-functions");
        builder.Functions.Add(DocumentScalarFunction.Create<decimal, decimal>("visit", value => { calls++; return value; }));
        builder.Functions.Add(DocumentScalarFunction.Create<JsonElement, long>("size_of", value => value.GetArrayLength()));
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var collection = await session.CreateCollectionAsync("items", CancellationToken.None);
        await PutAsync(collection, session, "a", "{\"tags\":[1,2,3]}");
        // Act
        var row = (await RowsAsync(session, "SELECT size_of(ARRAY visit(x) FOR x IN tags WHEN x > 1 END) AS n, FIRST visit(x) FOR x IN tags END AS f FROM items")).ShouldHaveSingleItem();
        // Assert
        row.GetValue(0).ShouldBe(2L);
        row.GetValue(1).ShouldBe(1m);
        calls.ShouldBe(3);
        (await RowsAsync(session, "SELECT FIRST 10 / x FOR x IN [2,0] END FROM items")).ShouldHaveSingleItem().GetValue(0).ShouldBe(5m);
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Comprehensions: observe cancellation during element reads")]
    [InlineData("ARRAY x FOR x IN tags END")]
    [InlineData("FIRST x FOR x IN tags WHEN FALSE END")]
    public void Evaluate_ComprehensionCancellation_ShouldStopWithinElements(string expression)
    {
        // Arrange
        using var cancellation = new CancellationTokenSource();
        using var source = new CountingJsonMemory("{\"tags\":[" + string.Join(',', Enumerable.Repeat("1", 200000)) + "]}", cancellation);
        using var document = JsonDocument.Parse(source.Memory);
        var query = (OqlSelectExpression)((OqlQueryStatement)new OqlQueryParser().Parse($"SELECT {expression} FROM items")).OqlExpression;
        var evaluator = new DocumentExpressionEvaluator(null, null, cancellation.Token);
        source.Arm(64);
        // Act
        Should.Throw<OperationCanceledException>(() => evaluator.Evaluate(query.Projections[0].Expression, new DocumentEvaluationContext(document.RootElement)));
        // Assert
        cancellation.IsCancellationRequested.ShouldBeTrue();
        source.Reads.ShouldBeInRange(64, 70);
    }
}
