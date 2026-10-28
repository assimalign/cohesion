using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Documents.Internal;
using Assimalign.Cohesion.Database.Execution;

namespace Assimalign.Cohesion.Database.Documents.Tests;

public sealed class DocumentOrderedAccessTests
{
    [Fact(DisplayName = "Cohesion Test [Documents] - Complex order existence probing stops at its first visible match (#1632)")]
    public async Task Plan_ComplexDomainProbe_ShouldStopAtFirstVisibleEntryAsync()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("complex-order-probe", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var collection = await session.CreateCollectionAsync("items", CancellationToken.None);
        for (int i = 0; i < 512; i++)
        { await collection.PutAsync(session, i.ToString("D3"), "{\"v\":[1,2]}"u8.ToArray(), cancellationToken: CancellationToken.None); }
        await session.ExecuteAsync("CREATE INDEX ix ON items (v INCLUDE MISSING)", cancellationToken: CancellationToken.None);

        // Act
        var probe = await database.RunAsync(session, operation =>
        {
            var metadata = database.Catalog.FindCollection("items", operation.Context.Snapshot)!.Value;
            var index = database.Catalog.GetIndexes(metadata.Id, operation.Context.Snapshot).Single();
            return ValueTask.FromResult(database.Catalog.ProbeComplexOrderKeys(index, [], operation.Context.Snapshot, CancellationToken.None));
        }, CancellationToken.None);

        // Assert: existence is sufficient to reject; completeness is irrelevant.
        probe.Count.ShouldBe(1);
        probe.EntriesExamined.ShouldBe(1);
        probe.PagesVisited.ShouldBeLessThanOrEqualTo(32);
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - EXPLAIN proves index ordering and reports consumption (#1632)")]
    [InlineData("a", "", "a", false, "leading_missing_not_rejected")]
    [InlineData("a", "WHERE a IS NOT MISSING", "a", true, "forward")]
    [InlineData("a", "WHERE a IS NOT NULL", "a DESC", true, "reverse")]
    [InlineData("a", "WHERE a IS NOT VALUED", "a", false, "leading_missing_not_rejected")]
    [InlineData("a", "WHERE a > 0 OR b > 0", "a", false, "leading_missing_not_rejected")]
    [InlineData("a", "WHERE a > 0 OR a IS NULL", "a", true, "forward")]
    [InlineData("a INCLUDE MISSING", "", "a", true, "forward")]
    [InlineData("a INCLUDE MISSING", "", "a DESC NULLS LAST", true, "reverse")]
    [InlineData("a INCLUDE MISSING", "", "a NULLS LAST", false, "null_placement_mismatch")]
    [InlineData("a", "WHERE a >= 1", "a DESC NULLS FIRST", true, "reverse")]
    [InlineData("a, b", "WHERE a = 1", "b DESC", true, "reverse")]
    [InlineData("a, b", "WHERE a = 1 AND b = 2", "a", true, "forward")]
    [InlineData("a, b", "WHERE a > 0 AND b > 0", "a, b", true, "forward")]
    [InlineData("a, b", "WHERE a > 0 AND b > 0", "a, b DESC", false, "mixed_directions")]
    [InlineData("a, b", "WHERE a IS NOT MISSING", "b", false, "not_an_index_prefix")]
    [InlineData("a, b", "WHERE a > 0", "a, b", false, "scalar_domain_unproven")]
    [InlineData("a INCLUDE MISSING", "", "a + 1", false, "non_index_expression")]
    [InlineData("DISTINCT ARRAY v FOR v IN arr END", "", "a", false, "array_index")]
    public async Task Explain_IndexShape_ShouldDescribeProofAndConsumptionAsync(string keys, string where, string ordering,
        bool eligible, string reason)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("order-eligibility", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.CreateCollectionAsync("items", CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO items (KEY, VALUE) VALUES ('z', {'a':1,'b':2,'arr':[1]}), ('a', {'a':2,'b':1,'arr':[2]}), ('missing', {})", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync($"CREATE INDEX ix ON items ({keys})", cancellationToken: CancellationToken.None);

        // Act
        var nodes = await NodesAsync(session, $"EXPLAIN SELECT META().id FROM items {where} ORDER BY {ordering}");

        // Assert: eligibility and actual selection are both visible.
        string detail = (string)nodes.Single(row => Equals(row[2], "IndexOrderCandidate"))[5]!;
        detail.ShouldContain("order_eligible=" + eligible, Case.Sensitive);
        detail.ShouldContain(eligible ? "order_direction=" + reason : "order_reason=" + reason, Case.Sensitive);
        detail.ShouldContain(eligible ? "consumed=True; sort=elided" : "consumed=False; sort=retained", Case.Sensitive);
        if (eligible) { detail.ShouldContain("tie_break=document_identity; equal_key_buckets=complete", Case.Sensitive); }
        if (where == "WHERE a = 1 AND b = 2")
        { detail.ShouldContain("equality_keys=2; order_prefix_keys=1", Case.Sensitive); }
        ((string)nodes[0][5]!).ShouldContain(eligible ? "sort=elided" : "sort=retained", Case.Sensitive);
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Complex indexed values never prove complete scalar order (#1632)")]
    [InlineData("[]")]
    [InlineData("{}")]
    public async Task Explain_ComplexValues_ShouldRetainFallbackAsync(string value)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("complex-order", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.CreateCollectionAsync("items", CancellationToken.None);
        await session.ExecuteAsync($"INSERT INTO items (KEY, VALUE) VALUES ('a', {{'v':{value}}})", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("CREATE INDEX ix ON items (v INCLUDE MISSING)", cancellationToken: CancellationToken.None);

        // Act
        var nodes = await NodesAsync(session, "EXPLAIN SELECT META().id FROM items ORDER BY v");

        // Assert
        ((string)nodes.Single(row => Equals(row[2], "IndexOrderCandidate"))[5]!).ShouldContain("order_reason=complex_values", Case.Sensitive);
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Row-shaping stages cannot deliver index order (#1632)")]
    [InlineData("SELECT SUM(a) FROM items ORDER BY SUM(a)", "grouping_changes_order")]
    [InlineData("SELECT DISTINCT a FROM items ORDER BY a", "distinct_changes_order")]
    [InlineData("SELECT a FROM items UNNEST arr v ORDER BY a", "unnest_changes_order")]
    public async Task Explain_ShapedRows_ShouldStateFallbackAsync(string query, string reason)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("shaped-order", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.CreateCollectionAsync("items", CancellationToken.None);
        await session.ExecuteAsync("CREATE INDEX ix ON items (a INCLUDE MISSING)", cancellationToken: CancellationToken.None);

        // Act
        var nodes = await NodesAsync(session, "EXPLAIN " + query);

        // Assert
        ((string)nodes.Single(row => Equals(row[2], "IndexOrderCandidate"))[5]!).ShouldContain("order_reason=" + reason, Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - Intersections retain the identity merge despite eligible inputs (#1632)")]
    public async Task Explain_Intersection_ShouldReportLostOrderAsync()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("intersection-order", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.CreateCollectionAsync("items", CancellationToken.None);
        for (int i = 0; i < 32; i++)
        { await session.ExecuteAsync($"INSERT INTO items (KEY, VALUE) VALUES ('{i}', {{'a':1,'b':2}})", cancellationToken: CancellationToken.None); }
        await session.ExecuteAsync("CREATE INDEX ia ON items (a)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("CREATE INDEX ib ON items (b)", cancellationToken: CancellationToken.None);

        // Act
        var nodes = await NodesAsync(session, "EXPLAIN SELECT META().id FROM items WHERE a = 1 AND b = 2 ORDER BY a");

        // Assert
        ((string)nodes.Single(row => Equals(row[2], "IndexIntersection"))[5]!).ShouldContain("order_reason=intersection_erases_order", Case.Sensitive);
        nodes.Count(row => Equals(row[2], "IndexOrderCandidate")).ShouldBe(2);
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - INCLUDE MISSING catalog rendering reparses and sparse coverage is unchanged (#1632)")]
    public async Task Execute_IncludeMissing_ShouldRoundTripCatalogAndKeepSparseIndexAsync()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("include-missing", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var collection = await session.CreateCollectionAsync("items", CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO items (KEY, VALUE) VALUES ('m', {}), ('n', {'v':NULL}), ('v', {'v':1})", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("CREATE INDEX sparse ON items (v)", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("CREATE INDEX complete ON items (v INCLUDE MISSING)", cancellationToken: CancellationToken.None);

        // Act
        string? path = null;
        await using (var result = (DatabaseResultSet)await session.ExecuteAsync("SELECT PATH, PATHS[0] AS k, INCLUDE_MISSING FROM COHESION_SCHEMA.INDEXES WHERE INDEX_NAME = 'complete'", cancellationToken: CancellationToken.None))
        {
            await foreach (var row in result.GetRowsAsync(CancellationToken.None))
            { path = (string)row.GetValue(0)!; row.GetValue(1).ShouldBe(path); row.GetValue(2).ShouldBe(true); }
        }
        path.ShouldBe("v INCLUDE MISSING");
        await session.ExecuteAsync($"CREATE INDEX copy ON items ({path})", cancellationToken: CancellationToken.None);
        var counts = await database.RunAsync(session, async operation =>
        {
            var snapshot = operation.Context.Snapshot;
            var sparse = await database.Catalog.SearchIndexAsync(database.Catalog.FindCollection("items", snapshot)!.Value.Id, "sparse", null, false, null, false, snapshot);
            var complete = await database.Catalog.SearchIndexAsync(database.Catalog.FindCollection("items", snapshot)!.Value.Id, "copy", null, false, null, false, snapshot);
            return (sparse.Count, complete.Count);
        }, CancellationToken.None);

        // Assert
        counts.ShouldBe((2, 3));
    }

    private static async Task<List<object?[]>> NodesAsync(DocumentDatabaseSession session, string query)
    {
        await using var result = (DatabaseResultSet)await session.ExecuteAsync(query, cancellationToken: CancellationToken.None);
        var nodes = new List<object?[]>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None))
        { nodes.Add(Enumerable.Range(0, row.FieldCount).Select(row.GetValue).ToArray()); }
        return nodes;
    }
}
