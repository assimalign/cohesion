using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Documents.Catalog;
using Assimalign.Cohesion.Database.Documents.Internal;
using Assimalign.Cohesion.Database.Documents.Language;
using Assimalign.Cohesion.Database.Execution;

namespace Assimalign.Cohesion.Database.Documents.Tests;

public sealed partial class DocumentQueryTests
{
    [Fact(DisplayName = "Cohesion Test [Documents] - Direct array AST floating constants and UNIQUE arrays have coded refusals (#1530)")]
    public async Task Plan_DirectArrayIndex_ShouldValidateRestrictedConstantsAndUniqueFlag()
    {
        // Arrange.
        await using var engine = DocumentDatabaseEngine.Create("array-direct-ast", new());
        var database = await engine.CreateDatabaseAsync("test");
        await using var session = await database.CreateSessionAsync();
        await session.CreateCollectionAsync("items");
        var array = new OqlComprehensionExpression("v", new OqlPathExpression([new("tags", null)]),
            new OqlPathExpression([new("v", null)]),
            new OqlBinaryExpression(new OqlPathExpression([new("v", null)]), ">", new OqlLiteralExpression(1.0d)));

        // Act / Assert.
        var error = Should.Throw<DatabaseException>(() => DocumentArrayIndexExpressions.BindKey(array));
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD02027");
        var unique = await Should.ThrowAsync<DatabaseException>(async () =>
            await session.ExecuteAsync("CREATE UNIQUE INDEX ix ON items (DISTINCT ARRAY v FOR v IN tags END)"));
        unique.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD03025");
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - WHEN implication uses normalized conjuncts from one element scope (#1530)")]
    [InlineData("v.stock > 0 AND v.live = TRUE", "0 < x.stock AND TRUE = x.live", true)]
    [InlineData("v.stock > 0 OR v.live = TRUE", "(x.stock > 0 OR x.live = TRUE)", true)]
    [InlineData("NOT (v.stock IS NULL)", "x.stock IS NOT NULL", true)]
    [InlineData("v.stock > 0", "x.stock > 1", false)]
    [InlineData("v.stock > 0", "ANY y IN variants SATISFIES y.stock > 0 END", false)]
    public async Task Execute_ArrayWhenImplication_ShouldUseOnlyProvenMatchingScopes(string when, string condition, bool indexed)
    {
        // Arrange.
        await using var engine = DocumentDatabaseEngine.Create("array-when-implication", new());
        var database = await engine.CreateDatabaseAsync("test");
        await using var session = await database.CreateSessionAsync();
        var collection = await session.CreateCollectionAsync("items");
        await PutAsync(collection, session, "cross", "{\"variants\":[{\"price\":20,\"stock\":0,\"live\":false},{\"price\":5,\"stock\":3,\"live\":true}]}");
        await PutAsync(collection, session, "good", "{\"variants\":[{\"price\":20,\"stock\":3,\"live\":true}]}");
        string query = $"SELECT META().id AS id FROM items WHERE ANY x IN variants SATISFIES x.price > 10 AND {condition} END";
        var expected = (await RowsAsync(session, query)).Select(row => row.GetString(0)).ToArray();

        // Act.
        await session.ExecuteAsync($"CREATE INDEX ix ON items (DISTINCT ARRAY v.price FOR v IN variants WHEN {when} END)");
        var rows = await RowsAsync(session, query);

        // Assert.
        rows.Select(row => row.GetString(0)).ShouldBe(expected);
        ((await PlanAsync(database, query)).Access is DocumentIndexPath).ShouldBe(indexed);
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - Array and scalar chooser inputs intersect before content reads (#1530)")]
    public async Task Execute_ArrayAndScalarIndexes_ShouldExtendExistingIntersectionChooser()
    {
        // Arrange.
        await using var engine = DocumentDatabaseEngine.Create("array-intersection", new());
        var database = await engine.CreateDatabaseAsync("test");
        await using var session = await database.CreateSessionAsync();
        var collection = await session.CreateCollectionAsync("items");
        for (int i = 0; i < 64; i++)
        { await PutAsync(collection, session, $"{i:D3}", $"{{\"a\":{i / 32},\"tags\":[{i % 2},{i % 2}]}}"); }
        await session.ExecuteAsync("CREATE INDEX scalar_ix ON items (a)");
        await session.ExecuteAsync("CREATE INDEX array_ix ON items (DISTINCT ARRAY v FOR v IN tags END)");
        const string query = "SELECT META().id AS id FROM items WHERE a = 0 AND ANY x IN tags SATISFIES x = 0 END";

        // Act.
        var rows = await RowsAsync(session, query);
        var plan = await PlanAsync(database, query);

        // Assert.
        rows.Count.ShouldBe(16);
        session.LastQueryDocumentsRead.ShouldBe(16);
        plan.Access.ShouldBeOfType<DocumentIntersectionPath>().Inputs.Count.ShouldBe(2);
        await using var description = DocumentPlanner.Describe(plan);
        var nodes = new List<DatabaseRow>();
        await foreach (var row in description.GetRowsAsync(CancellationToken.None)) { nodes.Add(row); }
        nodes.Count(n => n.GetString(2) == "IndexSeek").ShouldBe(2);
        nodes.Single(n => n.GetString(4) == "array_ix").GetString(5)!.ShouldContain("array_residual=True", Case.Sensitive);
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Array key metadata renders round-trippable normalized DDL (#1530)")]
    [InlineData("DISTINCT ARRAY t['odd.name'] FOR t IN \"odd.source\" WHEN (t.ok = TRUE OR t.stock IS MISSING) AND t.price IS NOT NULL END")]
    [InlineData("DISTINCT ARRAY t FOR t IN tags WHEN t >= -1.25 AND t <= 1e2 END")]
    [InlineData("DISTINCT ARRAY t FOR t IN \"a'b\" WHEN t != 'it''s' END")]
    public async Task Execute_ArrayMetadata_ShouldRoundTripDescriptorAndExplain(string key)
    {
        // Arrange.
        await using var engine = DocumentDatabaseEngine.Create("array-rendering", new());
        var database = await engine.CreateDatabaseAsync("test");
        await using var session = await database.CreateSessionAsync();
        await session.CreateCollectionAsync("items");
        await session.ExecuteAsync($"CREATE INDEX ix ON items ({key})");

        // Act.
        string rendered = (await RowsAsync(session, "SELECT PATH FROM COHESION_SCHEMA.INDEXES")).Single().GetString(0)!;
        var original = (OqlCreateIndexExpression)((OqlQueryStatement)new OqlQueryParser().Parse($"CREATE INDEX ix ON items ({key})")).OqlExpression;
        var roundTrip = (OqlQueryStatement)new OqlQueryParser().Parse($"CREATE INDEX copy ON items ({rendered})");

        // Assert.
        roundTrip.Diagnostics.ShouldBeEmpty();
        var copy = roundTrip.OqlExpression.ShouldBeOfType<OqlCreateIndexExpression>();
        DocumentArrayIndexExpressions.BindKey(copy.Keys[0]).ShouldBe(DocumentArrayIndexExpressions.BindKey(original.Keys[0]));
        await session.ExecuteAsync($"CREATE INDEX copy ON items ({rendered})");
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Array WHEN predicate limits are coded before typed API guards (#1530)")]
    [InlineData(32, false)]
    [InlineData(33, true)]
    [InlineData(128, false)]
    [InlineData(129, true)]
    public async Task Execute_ArrayWhenBoundary_ShouldValidateBeforeCatalogMutation(int size, bool invalid)
    {
        // Arrange.
        await using var engine = DocumentDatabaseEngine.Create("array-when-limits", new());
        var database = await engine.CreateDatabaseAsync("test");
        await using var session = await database.CreateSessionAsync();
        await session.CreateCollectionAsync("items");
        string predicate;
        if (size < 100) { predicate = string.Concat(Enumerable.Repeat("NOT ", size - 1)) + "v.stock > 0"; }
        else
        {
            var leaves = Enumerable.Repeat("v.stock > 0", 64).ToArray();
            while (leaves.Length > 1) { leaves = leaves.Chunk(2).Select(pair => $"({pair[0]} OR {pair[1]})").ToArray(); }
            predicate = string.Concat(Enumerable.Repeat("NOT ", size - 127)) + leaves[0];
        }
        string text = $"CREATE INDEX ix ON items (DISTINCT ARRAY v.price FOR v IN variants WHEN {predicate} END)";

        // Act / Assert.
        if (invalid)
        {
            var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(text));
            var diagnostic = error.Diagnostics.ShouldHaveSingleItem();
            diagnostic.Code.ShouldBe("CD03028");
            diagnostic.Start.ShouldNotBeNull();
            (await RowsAsync(session, "SELECT * FROM COHESION_SCHEMA.INDEXES")).ShouldBeEmpty();
        }
        else { (await session.ExecuteAsync(text)).Status.ShouldBe(DatabaseResultStatus.Success); }
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - Array access respects replacement, rollback, deletion and old snapshots (#1530)")]
    public async Task Execute_ArrayIndexWrites_ShouldKeepSnapshotVisiblePostings()
    {
        // Arrange.
        await using var engine = DocumentDatabaseEngine.Create("array-snapshots", new());
        var database = await engine.CreateDatabaseAsync("test");
        await using var writer = await database.CreateSessionAsync();
        await using var reader = await database.CreateSessionAsync();
        var collection = await writer.CreateCollectionAsync("items");
        await PutAsync(collection, writer, "a", "{\"tags\":[1,1]}");
        await writer.ExecuteAsync("CREATE INDEX ix ON items (DISTINCT ARRAY v FOR v IN tags END)");
        const string one = "SELECT META().id AS id FROM items WHERE ANY t IN tags SATISFIES t = 1 END";
        const string two = "SELECT META().id AS id FROM items WHERE ANY t IN tags SATISFIES t = 2 END";
        await using var old = await reader.BeginTransactionAsync(CancellationToken.None);
        (await RowsAsync(reader, one)).Count.ShouldBe(1);

        // Act / Assert.
        await PutAsync(collection, writer, "a", "{\"tags\":[2,2]}");
        (await RowsAsync(reader, one)).Count.ShouldBe(1);
        (await RowsAsync(reader, two)).ShouldBeEmpty();
        (await RowsAsync(writer, one)).ShouldBeEmpty();
        (await RowsAsync(writer, two)).Count.ShouldBe(1);
        await using (var transaction = await writer.BeginTransactionAsync(CancellationToken.None))
        {
            await PutAsync(collection, writer, "a", "{\"tags\":[1,2,3]}");
            (await RowsAsync(writer, one)).Count.ShouldBe(1);
            await transaction.RollbackAsync(CancellationToken.None);
        }
        (await RowsAsync(writer, one)).ShouldBeEmpty();
        await collection.DeleteAsync(writer, "a", cancellationToken: CancellationToken.None);
        (await RowsAsync(writer, two)).ShouldBeEmpty();
        (await RowsAsync(reader, one)).Count.ShouldBe(1);
        await old.CommitAsync(CancellationToken.None);
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Array seeks equal scans across independent ANY scopes and mixed shapes (#1530)")]
    [InlineData("DISTINCT ARRAY v.price FOR v IN variants END")]
    [InlineData("category, DISTINCT ARRAY v.price FOR v IN variants END")]
    [InlineData("DISTINCT ARRAY v.price FOR v IN variants END, category")]
    public async Task Execute_ArrayIndex_ShouldPreservePredicateResults(string keys)
    {
        // Arrange.
        await using var engine = DocumentDatabaseEngine.Create("array-parity", new());
        var database = await engine.CreateDatabaseAsync("test");
        await using var session = await database.CreateSessionAsync();
        var collection = await session.CreateCollectionAsync("items");
        string[] shapes = ["[]", "null", "42", "{}", "[{\"price\":5},{\"price\":20}]",
            "[{\"price\":20,\"stock\":0},{\"price\":5,\"stock\":3}]", "[{\"price\":20,\"stock\":3}]",
            "[{\"price\":5},{\"price\":5.0},{\"price\":8}]", "[null,{}, {\"price\":null}, {\"price\":[]}, {\"price\":{}}]",
            "[{\"price\":true},{\"price\":\"x\"},{\"price\":-2.5}]" ];
        for (int i = 0; i < shapes.Length; i++)
        { await PutAsync(collection, session, $"{i:D2}", "{\"category\":\"X\",\"variants\":" + shapes[i] + "}"); }
        await PutAsync(collection, session, "missing", "{\"category\":\"X\"}");
        string[] predicates =
        [
            "category = 'X' AND ANY v IN variants SATISFIES v.price <= @price END",
            "category = 'X' AND ANY v IN variants SATISFIES 10 < v.price AND v.stock > 0 END",
            "category = 'X' AND ANY v IN variants SATISFIES v.price > 10 END AND ANY v IN variants SATISFIES v.price < 10 END",
            "category = 'X' AND ANY v IN variants SATISFIES v.price > 10 AND v.price < 10 END",
            "category = 'X' AND SOME z IN variants SATISFIES z.price BETWEEN 5 AND 9 END",
            "category = 'X' AND ANY d IN d.variants SATISFIES d.price = 5 END",
            "category = 'X' AND ANY z IN variants SATISFIES z.price = true END",
            "category = 'X' AND ANY z IN variants SATISFIES z.price = 'x' END",
            "category = 'X' AND ANY z IN variants SATISFIES z.price > 5 OR z.stock > 0 END",
            "category = 'X' AND EVERY z IN variants SATISFIES z.price > 5 END",
            "category = 'X' AND NOT ANY z IN variants SATISFIES z.price > 5 END",
            "category = 'X' AND (ANY z IN variants SATISFIES z.price = 5 END OR variants IS NULL)",
            "category = 'X'",
        ];
        var parameters = new Dictionary<string, object?> { ["price"] = 8m };
        var queries = predicates.Select(p => "SELECT META().id AS id FROM items d WHERE " + p).ToArray();
        var before = new List<string?[]>();
        foreach (string query in queries) { before.Add((await RowsAsync(session, query, parameters)).Select(r => r.GetString(0)).ToArray()); }

        // Act / Assert.
        await session.ExecuteAsync($"CREATE INDEX ix ON items ({keys})", cancellationToken: CancellationToken.None);
        for (int i = 0; i < queries.Length; i++)
        {
            (await RowsAsync(session, queries[i], parameters)).Select(r => r.GetString(0)).ShouldBe(before[i], queries[i]);
            ((await PlanAsync(database, queries[i], parameters)).Access is DocumentIndexPath).ShouldBe(i < 8, queries[i]);
        }
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Identity ordered array pages stop after OFFSET + LIMIT survivors (#1530)")]
    [InlineData(false, false, 0, null)]
    [InlineData(false, false, 20, null)]
    [InlineData(false, true, 0, null)]
    [InlineData(false, true, 20, null)]
    [InlineData(true, false, 0, null)]
    [InlineData(true, false, 20, null)]
    [InlineData(true, true, 0, null)]
    [InlineData(true, true, 20, null)]
    [InlineData(false, false, 0, "category DESC")]
    [InlineData(false, false, 0, "META().version DESC")]
    [InlineData(false, false, 0, "META().id, META().version")]
    public async Task Execute_SelectiveArrayPrice_ShouldMeetFetchBound(bool descending, bool residual, int offset, string? otherOrder)
    {
        // Arrange: 256 candidates, duplicate numeric postings and interleaved residual rejections.
        await using var engine = DocumentDatabaseEngine.Create("array-fetch", new());
        var database = await engine.CreateDatabaseAsync("test");
        await using var session = await database.CreateSessionAsync();
        var collection = await session.CreateCollectionAsync("items");
        await using (var transaction = await session.BeginTransactionAsync(CancellationToken.None))
        {
            for (int i = 0; i < 256; i++)
            {
                string keep = i % 2 == 0 ? "true" : "false";
                await PutAsync(collection, session, $"{i:D4}",
                    $"{{\"category\":\"X\",\"keep\":{keep},\"variants\":[{{\"price\":5}},{{\"price\":5.0}},{{\"price\":9}}]}}");
            }
            await transaction.CommitAsync(CancellationToken.None);
        }
        string order = residual ? "id" : offset == 0 ? "META().id" : "META(d).id";
        string query = "SELECT META(d).id AS id FROM items d WHERE category = 'X' AND ANY v IN variants SATISFIES v.price <= @price END" +
            (residual ? " AND keep = TRUE" : "") + " ORDER BY " + (otherOrder ?? order + (descending ? " DESC" : "")) + $" LIMIT 50 OFFSET {offset}";
        var parameters = new Dictionary<string, object?> { ["price"] = 10m };
        var expected = (await RowsAsync(session, query, parameters)).Select(r => r.GetString(0)).ToArray();
        await session.ExecuteAsync("CREATE INDEX ix ON items (category, DISTINCT ARRAY v.price FOR v IN variants END)", cancellationToken: CancellationToken.None);

        // Act.
        var rows = await RowsAsync(session, query, parameters);
        long fetches = session.LastQueryDocumentsRead;

        // Assert.
        rows.Select(r => r.GetString(0)).ShouldBe(expected);
        rows.Count.ShouldBe(50);
        Console.WriteLine($"Array page descending={descending}, residual={residual}, offset={offset}, otherOrder={otherOrder}: {fetches} content reads.");
        if (otherOrder is null)
        {
            var ids = Enumerable.Range(0, 256).Where(i => !residual || i % 2 == 0);
            if (descending) { ids = ids.Reverse(); }
            rows.Select(r => r.GetString(0)).ShouldBe(ids.Skip(offset).Take(50).Select(i => $"{i:D4}"));
            fetches.ShouldBeLessThanOrEqualTo(2 * (offset + 50));
            fetches.ShouldBe(residual ? 2 * (offset + 50) - (descending ? 0 : 1) : offset + 50);
        }
        else { fetches.ShouldBe(256); }
        (await PlanAsync(database, query, parameters)).Access.ShouldBeOfType<DocumentIndexPath>();
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Same-element residual and WHEN match have measured fetch limits (#1530)")]
    [InlineData(false, 1001)]
    [InlineData(true, 1)]
    public async Task Execute_CrossElementPredicate_ShouldRejectFalsePositivesAndMeasureFetches(bool filtered, int fetches)
    {
        // Arrange.
        await using var engine = DocumentDatabaseEngine.Create("array-correlation", new());
        var database = await engine.CreateDatabaseAsync("test");
        await using var session = await database.CreateSessionAsync();
        var collection = await session.CreateCollectionAsync("items");
        await using (var transaction = await session.BeginTransactionAsync(CancellationToken.None))
        {
            for (int i = 0; i < 1000; i++)
            { await PutAsync(collection, session, $"{i:D4}", "{\"variants\":[{\"price\":20,\"stock\":0},{\"price\":5,\"stock\":3}]}"); }
            await PutAsync(collection, session, "good", "{\"variants\":[{\"price\":20,\"stock\":3},{\"price\":20,\"stock\":3}]}");
            await transaction.CommitAsync(CancellationToken.None);
        }
        string when = filtered ? " WHEN v.stock > 0" : "";
        await session.ExecuteAsync($"CREATE INDEX ix ON items (DISTINCT ARRAY v.price FOR v IN variants{when} END)");
        const string query = "SELECT META().id AS id FROM items WHERE ANY x IN variants SATISFIES x.price > 10 AND 0 < x.stock END";

        // Act.
        var rows = await RowsAsync(session, query);

        // Assert.
        rows.ShouldHaveSingleItem().GetString(0).ShouldBe("good");
        session.LastQueryDocumentsRead.ShouldBe(fetches);
        if (filtered) { session.LastQueryDocumentsRead.ShouldBeLessThanOrEqualTo(2 * rows.Count); }
        (await PlanAsync(database, query)).Access.ShouldBeOfType<DocumentIndexPath>();
        if (filtered)
        {
            const string unmatched = "SELECT META().id AS id FROM items WHERE ANY x IN variants SATISFIES x.price > 10 END";
            (await PlanAsync(database, unmatched)).Access.ShouldBeOfType<DocumentScanPath>();
            (await RowsAsync(session, unmatched)).Count.ShouldBe(1001);
        }
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - UNNEST deduplicates candidates and keeps repeated array rows (#1530)")]
    [InlineData("UNNEST")]
    [InlineData("LEFT UNNEST")]
    [InlineData("UNNEST_IMPLICIT")]
    public async Task Execute_UnnestArrayIndex_ShouldKeepElementMultiplicity(string expansion)
    {
        // Arrange.
        await using var engine = DocumentDatabaseEngine.Create("array-unnest", new());
        var database = await engine.CreateDatabaseAsync("test");
        await using var session = await database.CreateSessionAsync();
        var collection = await session.CreateCollectionAsync("items");
        await PutAsync(collection, session, "a", "{\"category\":1,\"tags\":[1,1,2]}");
        await PutAsync(collection, session, "b", "{\"category\":1,\"tags\":[]}");
        await PutAsync(collection, session, "c", "{\"category\":1,\"tags\":null}");
        await session.ExecuteAsync("CREATE INDEX ix ON items (category, DISTINCT ARRAY x FOR x IN tags END)");
        bool implicitBinding = expansion == "UNNEST_IMPLICIT";
        string query = implicitBinding
            ? "SELECT tags FROM items d UNNEST tags WHERE d.category = 1 AND tags >= 1"
            : $"SELECT t FROM items d {expansion} d.tags AS t WHERE d.category = 1 AND t >= 1";

        // Act.
        var rows = await RowsAsync(session, query);

        // Assert.
        rows.Select(r => r.GetValue(0)).ShouldBe([1m, 1m, 2m]);
        session.LastQueryDocumentsRead.ShouldBe(1);
        var plan = (await PlanAsync(database, query)).Access.ShouldBeOfType<DocumentIndexPath>();
        plan.Index.Name.ShouldBe("ix");
        string unfiltered = implicitBinding ? "SELECT tags FROM items d UNNEST tags WHERE d.category = 1" :
            $"SELECT t FROM items d {expansion} d.tags AS t WHERE d.category = 1";
        (await PlanAsync(database, unfiltered)).Access.ShouldBeOfType<DocumentScanPath>();
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Restricted array DDL fails with a located code before catalog writes (#1530)")]
    [InlineData("DISTINCT ARRAY z.price FOR v IN variants END", "CD01028")]
    [InlineData("DISTINCT ARRAY v.price + 1 FOR v IN variants END", "CD03027")]
    [InlineData("DISTINCT ARRAY v.price FOR v IN LOWER(variants) END", "CD03027")]
    [InlineData("DISTINCT ARRAY v.price FOR v IN variants WHEN v.stock = @stock END", "CD02027")]
    [InlineData("DISTINCT ARRAY v.price FOR v IN variants WHEN v.stock > 1e99 END", "CD02027")]
    [InlineData("DISTINCT ARRAY v.price FOR v IN variants WHEN v.stock > 1e-99 END", "CD02027")]
    [InlineData("DISTINCT ARRAY v.price FOR v IN variants WHEN stock > 0 END", "CD01028")]
    [InlineData("DISTINCT ARRAY v.price FOR v IN variants WHEN v.stock IN (1,2) END", "CD03027")]
    [InlineData("DISTINCT ARRAY v.price FOR v IN variants END, DISTINCT ARRAY x FOR x IN tags END", "CD03025")]
    public async Task Execute_InvalidArrayDdl_ShouldReturnLocatedCodeAndKeepCatalog(string keys, string code)
    {
        // Arrange.
        await using var engine = DocumentDatabaseEngine.Create("array-ddl-errors", new());
        var database = await engine.CreateDatabaseAsync("test");
        await using var session = await database.CreateSessionAsync();
        await session.CreateCollectionAsync("items");

        // Act.
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync($"CREATE INDEX ix ON items ({keys})"));

        // Assert.
        var diagnostic = error.Diagnostics.ShouldHaveSingleItem();
        diagnostic.Code.ShouldBe(code);
        diagnostic.Start.ShouldNotBeNull();
        (await RowsAsync(session, "SELECT * FROM COHESION_SCHEMA.INDEXES")).ShouldBeEmpty();
    }
}
