using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Documents.Language;
using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Documents.Tests;

public sealed partial class DocumentRegisteredFunctionTests
{
    [Fact(DisplayName = "Cohesion Test [Documents] - Functions: builders freeze engine-wide functions without leaking to another engine")]
    public async Task Build_RegisteredScalar_ShouldFreezeAndIsolateCatalogAsync()
    {
        var function = DocumentScalarFunction.Create<string, string>("slug", value => value.ToLowerInvariant());
        var builder = DocumentDatabaseEngine.CreateBuilder("functions");
        builder.Functions.Add(function);
        builder.Functions.Contains("SLUG").ShouldBeTrue();
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        engine.Functions.GetOverloads("sLuG").ShouldHaveSingleItem().ShouldBeSameAs(function);
        Should.Throw<InvalidOperationException>(() => builder.Functions.Add(DocumentScalarFunction.Create<string, string>("late", value => value)));
        await using var other = DocumentDatabaseEngine.Create("other-functions", new());
        other.Functions.GetOverloads("slug").ShouldBeEmpty();
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);
        var request = DocumentQueryRequest.FromOql("SELECT slug(name) AS name FROM items WHERE amount = 2");
        (await RowsAsync(session, request)).ShouldHaveSingleItem().GetString(0).ShouldBe("alpha");
        var otherDatabase = await other.CreateDatabaseAsync("test", CancellationToken.None);
        await using var otherSession = await otherDatabase.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(otherSession);
        var error = await Should.ThrowAsync<DatabaseException>(async () => await otherSession.ExecuteAsync(request, CancellationToken.None));
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD00008");
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - Functions: typed scalar arities retain declared result metadata")]
    public async Task Execute_TypedScalarArities_ShouldKeepValuesAndTypesAsync()
    {
        await using var engine = await BuildAsync(
            DocumentScalarFunction.Create<long, long>("twice", value => value * 2),
            DocumentScalarFunction.Create<string, string, string>("join_two", (left, right) => left + "_" + right),
            DocumentScalarFunction.Create<string, string, string, string>("join_three", (a, b, c) => a + b + c),
            DocumentScalarFunction.Create<decimal, decimal, decimal, decimal, decimal>("add_four", (a, b, c, d) => a + b + c + d));
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);
        var query = DocumentQueryRequest.FromOql("SELECT twice(amount) AS n, join_two('x','y') AS s, join_three('a','b','c') AS t, add_four(1,2,3,4) AS d FROM items WHERE amount = 2");
        await using var result = (DatabaseResultSet)await session.ExecuteAsync(query, CancellationToken.None);
        result.Columns.Select(column => column.Type).ShouldBe([DatabaseType.Int64, DatabaseType.String, DatabaseType.String, DatabaseType.Decimal]);
        await using var rows = result.GetRowsAsync(CancellationToken.None).GetAsyncEnumerator();
        (await rows.MoveNextAsync()).ShouldBeTrue();
        rows.Current.GetValue(0).ShouldBe(4L);
        rows.Current.GetString(1).ShouldBe("x_y");
        rows.Current.GetString(2).ShouldBe("abc");
        rows.Current.GetValue(3).ShouldBe(10m);
        (await rows.MoveNextAsync()).ShouldBeFalse();
        await using var empty = (DatabaseResultSet)await session.ExecuteAsync("SELECT twice(amount) AS n FROM items WHERE amount < 0", cancellationToken: CancellationToken.None);
        empty.Columns.ShouldHaveSingleItem().Type.ShouldBe(DatabaseType.Int64);
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - Functions: custom scalar residuals preserve indexed candidates")]
    public async Task Execute_CustomPredicate_ShouldRetainIndexSeekAsync()
    {
        await using var engine = await BuildAsync(Slug());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);
        await session.ExecuteAsync("CREATE INDEX by_amount ON items (amount)", cancellationToken: CancellationToken.None);
        var rows = await RowsAsync(session, DocumentQueryRequest.FromOql("SELECT slug(name) AS name FROM items WHERE amount = 4 AND slug(name) = 'beta'"));
        rows.ShouldHaveSingleItem().GetString(0).ShouldBe("beta");
        session.LastQueryDocumentsRead.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - Functions: custom aggregates bind grouping HAVING ordering and empty groups")]
    public async Task Execute_CustomAggregate_ShouldGroupAndRetainEmptyMetadataAsync()
    {
        int seeds = 0, steps = 0;
        var total = DocumentAggregateFunction.Create<decimal, decimal, decimal>("total_value",
            () => { seeds++; return 0m; }, (state, value) => { steps++; return state + value; }, state => state);
        await using var engine = await BuildAsync(Slug(), total);
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);
        var rows = await RowsAsync(session, DocumentQueryRequest.FromOql("SELECT slug(category) AS category, total_value(amount) AS total FROM items GROUP BY slug(category) HAVING total_value(amount) >= 6 ORDER BY slug(category)"));
        rows.Count.ShouldBe(2);
        rows.Select(row => row.GetString(0)).ShouldBe(["a", "b"]);
        rows.Select(row => row.GetValue(1)).ShouldBe(new object?[] { 6m, 9m });
        // HAVING and projection are separate aggregate calls with independent accumulators.
        seeds.ShouldBe(4);
        steps.ShouldBe(6);
        var empty = (await RowsAsync(session, DocumentQueryRequest.FromOql("SELECT total_value(amount) AS total FROM items WHERE amount < 0"))).ShouldHaveSingleItem();
        empty.IsNull(0).ShouldBeTrue();
        seeds.ShouldBe(5);
        steps.ShouldBe(6);
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - Functions: typed aggregate incompatible stored kinds do not enter delegates")]
    public async Task Execute_CustomAggregateWrongStoredKind_ShouldSkipIncompatibleInputsAsync()
    {
        int steps = 0;
        await using var engine = await BuildAsync(DocumentAggregateFunction.Create<decimal, decimal, decimal>("numeric_total",
            () => 0m, (state, value) => { steps++; return state + value; }, state => state));
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);
        (await RowsAsync(session, DocumentQueryRequest.FromOql("SELECT numeric_total(name) FROM items"))).ShouldHaveSingleItem().IsNull(0).ShouldBeTrue();
        steps.ShouldBe(0);
        var native = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("SELECT SUM(name) FROM items", cancellationToken: CancellationToken.None));
        native.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD02008");
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - Functions: NULL and MISSING propagate distinctly or reach an opted-in core")]
    public async Task Execute_UnknownInputs_ShouldPreserveMissingAndCorePolicyAsync()
    {
        int strictCalls = 0;
        await using var engine = await BuildAsync(
            DocumentScalarFunction.Create<string, string, string>("strict_pair", (a, b) => { strictCalls++; return a + b; }),
            DocumentScalarFunction.Create<DocumentValue, string>("inspect_value", value => value.IsMissing ? "missing" : value.IsNull ? "null" : "valued",
                DocumentNullBehavior.CalledOnNullOrMissingInput));
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);
        var row = (await RowsAsync(session, DocumentQueryRequest.FromOql("SELECT strict_pair(NULL, absent) AS missing, strict_pair(NULL, name) AS nil, inspect_value(absent) AS m, inspect_value(NULL) AS n FROM items WHERE amount = 2"))).ShouldHaveSingleItem();
        row.IsMissing(0).ShouldBeTrue();
        row.IsNull(0).ShouldBeFalse();
        row.IsNull(1).ShouldBeTrue();
        row.GetString(2).ShouldBe("missing");
        row.GetString(3).ShouldBe("null");
        strictCalls.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - Functions: lazy grammar forms skip custom faults and EXPLAIN never invokes them")]
    public async Task Execute_LazyFormsAndExplain_ShouldAvoidSkippedCustomCallsAsync()
    {
        int calls = 0;
        await using var engine = await BuildAsync(DocumentScalarFunction.Create<decimal, decimal>("fail_value", _ => { calls++; throw new InvalidOperationException("must stay skipped"); }));
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await SeedAsync(session);
        var row = (await RowsAsync(session, DocumentQueryRequest.FromOql("SELECT COALESCE(name, fail_value(1)) AS a, IFNULL(name, fail_value(1)) AS b, IFMISSING(NULL, fail_value(1)) AS c, CASE WHEN TRUE THEN name ELSE fail_value(1) END AS d FROM items WHERE amount = 2"))).ShouldHaveSingleItem();
        row.GetString(0).ShouldBe("Alpha");
        row.GetString(1).ShouldBe("Alpha");
        row.IsNull(2).ShouldBeTrue();
        row.GetString(3).ShouldBe("Alpha");
        await using var plan = (DatabaseResultSet)await session.ExecuteAsync("EXPLAIN SELECT fail_value(amount) FROM items", cancellationToken: CancellationToken.None);
        calls.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - Functions: native functions expose the same public invocation path")]
    public async Task Catalog_NativeFunctions_ShouldUseScalarAndAggregateContractsAsync()
    {
        await using var engine = DocumentDatabaseEngine.Create("native-functions", new());
        var expected = OqlFunctions.All.Where(function => function.Name is not ("META" or "COALESCE" or "IFMISSING" or "IFNULL" or "IFMISSINGORNULL")).Select(function => function.Name).Order().ToArray();
        engine.Functions.Select(function => function.Name.ToString()).Distinct(StringComparer.OrdinalIgnoreCase).Order().ShouldBe(expected);
        var upper = engine.Functions.GetOverloads("UPPER").ShouldHaveSingleItem().ShouldBeAssignableTo<DocumentScalarFunction>();
        var values = new[] { DocumentValue.FromString("native") };
        upper.Invoke(new DocumentArguments(values)).AsString().ShouldBe("NATIVE");
        var count = engine.Functions.GetOverloads("COUNT").OfType<DocumentAggregateFunction>().Single(function => function.MinimumArguments == 0);
        var context = new DocumentFunctionContext(default, CancellationToken.None);
        var accumulator = count.CreateAccumulator(in context);
        var arguments = new DocumentArguments(ReadOnlySpan<DocumentValue>.Empty);
        accumulator.Add(in arguments);
        accumulator.Add(in arguments);
        accumulator.Finish().AsInt64().ShouldBe(2);
        Should.Throw<InvalidOperationException>(() => accumulator.Finish());
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Functions: registration rejects reserved names")]
    [InlineData("UPPER")]
    [InlineData("COUNT")]
    [InlineData("COALESCE")]
    [InlineData("META")]
    [InlineData("SELECT")]
    [InlineData("ARRAY_AGG")]
    [InlineData("REGEXP_LIKE")]
    public void Add_ReservedFunctionName_ShouldRefuse(string name)
    {
        var builder = DocumentDatabaseEngine.CreateBuilder("reserved-functions");
        Should.Throw<ArgumentException>(() => builder.Functions.Add(DocumentScalarFunction.Create<string, string>(name, value => value)));
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - Functions: application overload arities must be disjoint and kinds consistent")]
    public async Task Add_Overloads_ShouldRejectOverlapAndMixedKindsAsync()
    {
        var builder = DocumentDatabaseEngine.CreateBuilder("overload-functions");
        builder.Functions.Add(DocumentScalarFunction.Create<string, string>("custom", value => value));
        builder.Functions.Add(DocumentScalarFunction.Create<string, string, string>("custom", (a, b) => a + b));
        Should.Throw<InvalidOperationException>(() => builder.Functions.Add(DocumentScalarFunction.Create<decimal, decimal>("CUSTOM", value => value)));
        Should.Throw<InvalidOperationException>(() => builder.Functions.Add(DocumentAggregateFunction.Create<decimal, decimal, decimal>("custom", () => 0m, (a, b) => a + b, value => value)));
        await using var engine = await builder.BuildAsync(CancellationToken.None);
        engine.Functions.GetOverloads("custom").Count.ShouldBe(2);
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Functions: generated name grammar rejects invalid identifiers")]
    [InlineData("")]
    [InlineData("9name")]
    [InlineData("a.b")]
    [InlineData("bad name")]
    public void Create_InvalidName_ShouldRefuse(string name)
        => Should.Throw<ArgumentException>(() => DocumentScalarFunction.Create<string, string>(name, value => value));

    [Fact(DisplayName = "Cohesion Test [Documents] - Functions: default names and unsupported CLR types fail at registration construction")]
    public void Create_DefaultNameAndUnsupportedTypes_ShouldRefuse()
    {
        Should.Throw<ArgumentException>(() => DocumentScalarFunction.Create<string, string>(default(DocumentFunctionName), value => value));
        Should.Throw<NotSupportedException>(() => DocumentScalarFunction.Create<Guid, string>("guid_value", value => value.ToString()));
        Should.Throw<ArgumentException>(() => DocumentScalarFunction.Create<decimal, decimal>("nonnullable", value => value, DocumentNullBehavior.CalledOnNullOrMissingInput));
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - Functions: JSON values own snapshots and numeric-domain failures retain existing codes")]
    public void FromJson_OwnedValue_ShouldSurviveSourceDisposalAndCodeOverflow()
    {
        DocumentValue value;
        using (var json = JsonDocument.Parse("{\"array\":[1,2]}")) { value = DocumentValue.FromJson(json.RootElement); }
        value.AsJson().GetProperty("array")[1].GetInt32().ShouldBe(2);
        using var tooLarge = JsonDocument.Parse("1e100");
        var error = Should.Throw<DatabaseException>(() => DocumentValue.FromJson(tooLarge.RootElement));
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD02007");
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - Functions: standalone parsing retains unknown call diagnostics")]
    public void Parse_StandaloneUnknownCall_ShouldRetainCompatibility()
        => new OqlQueryParser().Parse("SELECT not_registered(name) FROM items").Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD00008");

    private static DocumentScalarFunction Slug() => DocumentScalarFunction.Create<string, string>("slug", value => value.ToLowerInvariant());

    private static ValueTask<DocumentDatabaseEngine> BuildAsync(params DocumentFunction[] functions)
    {
        var builder = DocumentDatabaseEngine.CreateBuilder("registered-functions");
        foreach (var function in functions) { builder.Functions.Add(function); }
        return builder.BuildAsync(CancellationToken.None);
    }

    private static async ValueTask SeedAsync(DocumentDatabaseSession session)
    {
        var collection = await session.CreateCollectionAsync("items", CancellationToken.None);
        await collection.PutAsync(session, "a", "{\"name\":\"Alpha\",\"amount\":2,\"category\":\"A\"}"u8.ToArray(), cancellationToken: CancellationToken.None);
        await collection.PutAsync(session, "b", "{\"name\":\"Beta\",\"amount\":4,\"category\":\"A\"}"u8.ToArray(), cancellationToken: CancellationToken.None);
        await collection.PutAsync(session, "c", "{\"name\":\"Gamma\",\"amount\":9,\"category\":\"B\"}"u8.ToArray(), cancellationToken: CancellationToken.None);
    }

    private static async Task<List<DatabaseRow>> RowsAsync(DocumentDatabaseSession session, DocumentQueryRequest request)
    {
        await using var result = (DatabaseResultSet)await session.ExecuteAsync(request, CancellationToken.None);
        result.Diagnostics.ShouldBeEmpty();
        var rows = new List<DatabaseRow>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None)) { rows.Add(row); }
        return rows;
    }
}
