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

public sealed class DocumentSourceTests
{
    [Theory(DisplayName = "Cohesion Test [Documents] - FROM: synthetic stars include the source wrapper in projection depth")]
    [InlineData(254, false)]
    [InlineData(255, true)]
    public async Task ExecuteAsync_SourceStarDepth_ShouldCountEveryWrapperAsync(int depth, bool refuses)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("source-depth", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        using var json = JsonDocument.Parse(new string('[', depth) + "0" + new string(']', depth), new JsonDocumentOptions { MaxDepth = 256 });
        var request = DocumentQueryRequest.FromOql("SELECT * FROM {p:@deep} AS v", new Dictionary<string, object?> { ["deep"] = json.RootElement });
        // Act / Assert
        if (refuses)
        {
            var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(request, CancellationToken.None));
            error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD05015");
            error.Diagnostics.Single().Start.ShouldBe(7);
        }
        else
        {
            await using var result = (DatabaseResultSet)await session.ExecuteAsync(request, CancellationToken.None);
            int count = 0;
            await foreach (var row in result.GetRowsAsync(CancellationToken.None)) { row.GetValue(0).ShouldBeOfType<JsonElement>(); count++; }
            count.ShouldBe(1);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - FROM: uncorrelated callbacks evaluate once and LIMIT zero remains lazy")]
    public async Task ExecuteAsync_ComputedSource_ShouldEvaluateOnceWhenReachedAsync()
    {
        // Arrange
        int calls = 0;
        using var array = JsonDocument.Parse("[1,2,3]");
        var builder = DocumentDatabaseEngine.CreateBuilder("computed-sources");
        builder.Functions.Add(DocumentScalarFunction.Create<decimal, JsonElement>("source_array", value => { calls++; return array.RootElement; }));
        await using var engine = builder.Build();
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        // Act
        var plan = await DocumentSubqueryTests.RowsAsync(session, "EXPLAIN SELECT RAW v FROM source_array(1) AS v");
        var empty = await DocumentSubqueryTests.RowsAsync(session, "SELECT RAW v FROM source_array(1) AS v LIMIT 0");
        // Assert
        plan.ShouldNotBeEmpty();
        empty.ShouldBeEmpty();
        calls.ShouldBe(0);
        var rows = await DocumentSubqueryTests.RowsAsync(session, "SELECT RAW v FROM source_array(1) AS v LIMIT 2");
        rows.Select(row => row.GetValue(0)).ShouldBe(new object?[] { 1m, 2m });
        calls.ShouldBe(1);
        (await DocumentSubqueryTests.RowsAsync(session, "SELECT RAW v FROM [1/0] AS v LIMIT 0")).ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - FROM: expression values follow SQL++ row semantics")]
    [InlineData("[1,2]", "1|2")]
    [InlineData("[]", "")]
    [InlineData("{a:1}", "{\"a\":1}")]
    [InlineData("1", "1")]
    [InlineData("'text'", "\"text\"")]
    [InlineData("TRUE", "true")]
    [InlineData("NULL", "null")]
    [InlineData("MISSING", "")]
    [InlineData("[NULL,{},[],1]", "null|{}|[]|1")]
    public async Task ExecuteAsync_ExpressionKinds_ShouldProduceExpectedRowsAsync(string expression, string expected)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("source-values", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        // Act
        var rows = await DocumentSubqueryTests.RowsAsync(session, "SELECT RAW v FROM " + expression + " AS v");
        // Assert
        string.Join("|", rows.Select(row => Json(row.GetValue(0)))).ShouldBe(expected);
        var derived = await DocumentSubqueryTests.RowsAsync(session,
            "SELECT RAW d FROM (SELECT RAW v FROM " + expression + " AS v) d");
        string.Join("|", derived.Select(row => Json(row.GetValue(0)))).ShouldBe(expected);
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - FROM: parameter values preserve every JSON source kind")]
    [InlineData("[1,2]", "1|2")]
    [InlineData("[]", "")]
    [InlineData("{\"a\":1}", "{\"a\":1}")]
    [InlineData("1", "1")]
    [InlineData("\"text\"", "\"text\"")]
    [InlineData("true", "true")]
    [InlineData("null", "null")]
    [InlineData("MISSING", "")]
    public async Task ExecuteAsync_ParameterKinds_ShouldPreserveSourceRowsAsync(string json, string expected)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("parameter-sources", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        using var parameter = JsonDocument.Parse(json == "MISSING" ? "null" : json);
        var request = DocumentQueryRequest.FromOql("SELECT RAW v FROM @p AS v", new Dictionary<string, object?>
        { ["p"] = json == "MISSING" ? default(JsonElement) : parameter.RootElement });
        // Act
        await using var result = (DatabaseResultSet)await session.ExecuteAsync(request, CancellationToken.None);
        var values = new List<string>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None)) { values.Add(Json(row.GetValue(0))); }
        // Assert
        string.Join("|", values).ShouldBe(expected);
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - FROM: derived RAW and named queries compose with every outer clause")]
    [InlineData("SELECT d.n FROM (SELECT n FROM items) d WHERE d.n > 1 ORDER BY d.n DESC LIMIT 1 OFFSET 1", "2")]
    [InlineData("SELECT SUM(d.n) AS n FROM (SELECT n FROM items) AS d HAVING SUM(d.n) > 5", "6")]
    [InlineData("SELECT COUNT(*) AS n FROM (SELECT n FROM items) d GROUP BY n > 1 HAVING COUNT(*) = 2", "2")]
    [InlineData("SELECT RAW e.n FROM (SELECT d.n FROM (SELECT n FROM items) d) e ORDER BY e.n DESC", "3|2|1")]
    [InlineData("SELECT RAW letting.n FROM (SELECT n FROM items GROUP BY n) letting ORDER BY letting.n", "1|2|3")]
    [InlineData("SELECT RAW x FROM (SELECT RAW [n,n+1] FROM items) d UNNEST d AS x WHERE x > 2 ORDER BY x", "3|3|4")]
    [InlineData("SELECT RAW x FROM [{tags:[1,2]},{tags:[3]}] AS d UNNEST d.tags AS x ORDER BY x DESC LIMIT 2", "3|2")]
    [InlineData("SELECT RAW x FROM [1,2] AS d LEFT UNNEST [] AS absent UNNEST [d,d+1] AS x ORDER BY x", "1|2|2|3")]
    [InlineData("SELECT SUM(v) AS n FROM [1,2,3] AS v HAVING SUM(v) > 0", "6")]
    [InlineData("SELECT COUNT(*) AS n FROM MISSING AS v", "0")]
    [InlineData("SELECT COUNT(*) AS n FROM (SELECT RAW n FROM items WHERE FALSE) d", "0")]
    [InlineData("SELECT DISTINCT RAW v FROM [2,1,2] AS v ORDER BY v", "1|2")]
    [InlineData("SELECT RAW v FROM (SELECT RAW n FROM items WHERE FALSE) v", "")]
    [InlineData("SELECT RAW v FROM (SELECT RAW NULL FROM items LIMIT 1) v", "null")]
    [InlineData("SELECT RAW v FROM (SELECT RAW MISSING FROM items LIMIT 1) v", "null")]
    [InlineData("SELECT RAW v FROM (SELECT RAW {} FROM items LIMIT 1) v", "{}")]
    [InlineData("SELECT RAW v FROM (SELECT RAW [] FROM items LIMIT 1) v", "[]")]
    [InlineData("SELECT RAW (SELECT RAW v FROM [7,8] AS v) FROM [1] AS d", "[7,8]")]
    [InlineData("SELECT RAW v FROM COALESCE((SELECT RAW n FROM items), []) AS v ORDER BY v", "1|2|3")]
    [InlineData("SELECT RAW v FROM ARRAY x+1 FOR x IN [1,2] END AS v", "2|3")]
    [InlineData("SELECT RAW \" \" FROM [1,2] AS \" \"", "1|2")]
    [InlineData("SELECT COUNT(*) FROM (SELECT * FROM COHESION_SCHEMA.OBJECT_OWNERSHIP) d", "1")]
    public async Task ExecuteAsync_SourceComposition_ShouldPreserveValuesAsync(string query, string expected)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("source-clauses", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await DocumentSubqueryTests.SeedAsync(session);
        // Act
        var rows = await DocumentSubqueryTests.RowsAsync(session, query);
        // Assert
        string.Join("|", rows.Select(row => Json(row.GetValue(0)))).ShouldBe(expected);
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - FROM: aliased stars wrap source values and metadata has no stored identity")]
    [InlineData("SELECT * FROM [1,NULL,{n:2}] AS v", "{\"v\":1}|{\"v\":null}|{\"v\":{\"n\":2}}")]
    [InlineData("SELECT * FROM (SELECT n FROM items LIMIT 1) v", "{\"v\":{\"n\":1}}")]
    [InlineData("SELECT META().id, META(v).version FROM [1] AS v", "null")]
    [InlineData("SELECT RAW META(v).id FROM (SELECT n FROM items LIMIT 1) v", "null")]
    [InlineData("SELECT * FROM [{tags:[1]}] AS d UNNEST d.tags AS v", "{\"d\":{\"tags\":[1]},\"v\":1}")]
    [InlineData("SELECT * FROM [1] AS d LEFT UNNEST NULL AS v", "{\"d\":1}")]
    public async Task ExecuteAsync_SourceStarAndMetadata_ShouldFollowExpressionRowsAsync(string query, string expected)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("source-star", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await DocumentSubqueryTests.SeedAsync(session);
        // Act
        var rows = await DocumentSubqueryTests.RowsAsync(session, query);
        // Assert
        string.Join("|", rows.Select(row => Json(row.GetValue(0)))).ShouldBe(expected);
        if (query.Contains("version", StringComparison.Ordinal)) { rows.Single().GetValue(1).ShouldBeNull(); }
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - FROM: LIMIT pulls only the required inner rows")]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(7, 7)]
    public async Task ExecuteAsync_DerivedLimit_ShouldBoundInnerReadsAndCallbacksAsync(int limit, int expected)
    {
        // Arrange
        int calls = 0;
        var builder = DocumentDatabaseEngine.CreateBuilder("stream-sources");
        builder.Functions.Add(DocumentScalarFunction.Create<decimal, decimal>("tick", value => { calls++; return value; }));
        await using var engine = builder.Build();
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.CreateCollectionAsync("items", CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO items (KEY, VALUE) VALUES " + string.Join(",", Enumerable.Range(0, 100).Select(n => $"('k{n:D3}', {{n:{n}}})")), cancellationToken: CancellationToken.None);
        // Act
        var rows = await DocumentSubqueryTests.RowsAsync(session, $"SELECT d.n FROM (SELECT tick(n) AS n FROM items) d LIMIT {limit}");
        // Assert
        rows.Count.ShouldBe(expected);
        calls.ShouldBe(expected);
        session.LastQueryDocumentsRead.ShouldBe(expected);
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - FROM: parameter arrays and derived blocking inputs support large inputs")]
    public async Task ExecuteAsync_LargeParameterAndBlockingInput_ShouldRetainOnlyRequiredRowsAsync()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("large-sources", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        using var values = JsonDocument.Parse("[" + string.Join(",", Enumerable.Range(1, 10_000)) + "]");
        var parameters = new Dictionary<string, object?> { ["p"] = values.RootElement };
        // Act
        var request = DocumentQueryRequest.FromOql("SELECT SUM(v) AS n FROM @p AS v", parameters);
        await using var result = (DatabaseResultSet)await session.ExecuteAsync(request, CancellationToken.None);
        var rows = new List<DatabaseRow>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None)) { rows.Add(row); }
        // Assert
        rows.ShouldHaveSingleItem().GetValue(0).ShouldBe(50_005_000m);
        var nested = await DocumentSubqueryTests.RowsAsync(session, "SELECT RAW x FROM (SELECT RAW v FROM [3,2,1] AS v ORDER BY v LIMIT 2) d UNNEST [d] x ORDER BY x");
        nested.Select(row => row.GetValue(0)).ShouldBe(new object?[] { 1m, 2m });
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - FROM: correlation executes and unbound expression aliases retain diagnostics")]
    [InlineData("SELECT (SELECT RAW v FROM d.tags AS v) FROM items d", "CK00001")]
    [InlineData("SELECT (SELECT RAW v FROM d AS v) FROM items d", "CK00001")]
    [InlineData("SELECT (SELECT RAW v FROM [d.n] AS v) FROM items d", "CK00001")]
    [InlineData("SELECT * FROM [1]", "CD01033")]
    [InlineData("SELECT * FROM @absent AS v LIMIT 0", "CD01005")]
    [InlineData("SELECT RAW v FROM [unbound] AS v", "CD01034")]
    [InlineData("SELECT RAW v FROM (unbound) AS v LIMIT 0", "CD01034")]
    [InlineData("INSERT INTO items (KEY k) SELECT 'new' AS k FROM [1] AS v", "CK00001")]
    [InlineData("UPSERT INTO items (KEY k) SELECT 'new' AS k FROM [1] AS v", "CK00001")]
    public async Task ExecuteAsync_InvalidSource_ShouldReportExpectedCodeAsync(string query, string code)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("invalid-sources", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await DocumentSubqueryTests.SeedAsync(session);
        // Act / Assert
        if (code == "CK00001")
        {
            var result = await session.ExecuteAsync(query, cancellationToken: CancellationToken.None);
            result.Diagnostics.ShouldBeEmpty();
            if (query.StartsWith("SELECT", StringComparison.Ordinal))
            {
                var rows = new List<DatabaseRow>();
                await foreach (var row in ((DatabaseResultSet)result).GetRowsAsync(CancellationToken.None)) { rows.Add(row); }
                rows.Count.ShouldBe(3);
                session.LastQuerySubqueriesExecuted.ShouldBe(3);
                await ((DatabaseResultSet)result).DisposeAsync();
            }
            else { (await DocumentSubqueryTests.RowsAsync(session, "SELECT * FROM items")).Count.ShouldBe(4); }
            return;
        }
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(query, cancellationToken: CancellationToken.None));
        error.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == code);
    }

    private static string Json(object? value) => value switch
    {
        null => "null", JsonElement json => json.GetRawText(), string text => "\"" + text + "\"",
        bool boolean => boolean ? "true" : "false", _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)!,
    };
}
