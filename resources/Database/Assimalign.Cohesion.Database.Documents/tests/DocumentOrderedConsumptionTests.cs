using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Documents.Internal;
using Assimalign.Cohesion.Database.Documents.Language;
using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Documents.Tests;

public sealed class DocumentOrderedConsumptionTests
{
    private readonly ITestOutputHelper _output;

    public DocumentOrderedConsumptionTests(ITestOutputHelper output) { _output = output; }

    [Fact(DisplayName = "Cohesion Test [Documents] - Ordered candidates match forced sorting across generated scalar, complex and paging cases (#1632)")]
    public async Task Execute_GeneratedOrderedPages_ShouldMatchSortedFallbackAsync()
    {
        // Arrange: shuffled identity/insertion/key orders, long duplicate buckets,
        // missing/null keys, numeric scales, escaped UTF-16 and trailing coarse tags.
        await using var engine = DocumentDatabaseEngine.Create("ordered-equivalence", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var collection = await session.CreateCollectionAsync("items", CancellationToken.None);
        string[] values = ["0", "1.00", "-1", "0.0000000000000000000000000001", "79228162514264337593543950335",
            "-79228162514264337593543950335", "false", "true", "null", "\"a\\u0000b\"", "\"\uE000\"", "\"\U0001F600\""];
        for (int i = 71; i >= 0; i--)
        {
            string v = i % 13 == 0 ? "" : "\"v\":" + values[i % values.Length] + ",";
            string json = "{" + v + "\"b\":" + (i % 4) + ",\"tail\":" + (i % 2 == 0 ? "[]" : "{}") + ",\"keep\":" + (i % 3 == 0 ? "true" : "false") + "}";
            await collection.PutAsync(session, ((i * 17) % 73).ToString("D3"), Encoding.UTF8.GetBytes(json), cancellationToken: CancellationToken.None);
        }
        string[] predicates = ["", "v IS NOT MISSING", "v IS NOT NULL", "v IS NULL", "v IS MISSING", "v >= 0", "v < 2",
            "v >= -1 AND v <= 1", "b = 2", "b = 2 AND v >= 0", "v IS NOT MISSING AND keep = TRUE",
            "v >= 0 OR v IS NULL", "v != 0", "v IS VALUED"];
        (string Expression, string Suffix)[][] orders =
        [
            [("v", "ASC")], [("v", "DESC")], [("v", "ASC NULLS LAST")], [("v", "DESC NULLS FIRST")],
            [("v", "ASC NULLS FIRST")], [("v", "DESC NULLS LAST")], [("b", "ASC"), ("v", "ASC")],
            [("b", "DESC"), ("v", "DESC")], [("v", "ASC"), ("b", "DESC")],
        ];
        string[] pages = ["", "LIMIT 0", "LIMIT 1", "LIMIT 5 OFFSET 1", "OFFSET 4 LIMIT 3", "LIMIT 2 OFFSET 71", "LIMIT 4 OFFSET 100", "OFFSET 2"];
        int combinations = 0, eligible = 0, comparisons = 0;

        // Act / Assert: CASE preserves the exact key and forces the existing sorter.
        foreach (string keys in new[] { "v", "v INCLUDE MISSING", "v INCLUDE MISSING, tail", "b INCLUDE MISSING, v" })
        {
            await session.ExecuteAsync($"CREATE INDEX ix ON items ({keys})", cancellationToken: CancellationToken.None);
            foreach (string predicate in predicates)
            {
                foreach (var terms in orders)
                {
                    string where = predicate.Length == 0 ? "" : "WHERE " + predicate;
                    string ordering = string.Join(", ", terms.Select(term => term.Expression + " " + term.Suffix));
                    string fallback = string.Join(", ", terms.Select(term => $"CASE WHEN TRUE THEN {term.Expression} ELSE {term.Expression} END {term.Suffix}"));
                    string query = $"SELECT META().id FROM items {where} ORDER BY {ordering}";
                    bool consumed = await database.RunAsync(session, operation => ValueTask.FromResult(
                        new DocumentPlanner(database.Catalog, operation.Context.Snapshot, null).Plan(
                            (OqlSelectExpression)DocumentQueryRequest.FromOql(query).Statement.OqlExpression).Access is
                            DocumentOrderingPath { Consumed: not null }), CancellationToken.None);
                    combinations++;
                    if (consumed) { eligible++; }
                    foreach (string page in pages)
                    {
                        var expected = await IdsAsync(session, $"SELECT META().id FROM items {where} ORDER BY {fallback} {page}");
                        var actual = await IdsAsync(session, query + " " + page);
                        actual.ShouldBe(expected, query + " " + page);
                        comparisons++;
                    }
                }
            }
            await session.ExecuteAsync("DROP INDEX ix ON items", cancellationToken: CancellationToken.None);
        }
        eligible.ShouldBeGreaterThan(150);
        combinations.ShouldBe(504);
        comparisons.ShouldBe(4032);
        _output.WriteLine($"Generated combinations={combinations}; consumed={eligible}; fallback/page comparisons={comparisons}.");
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Equal ORDER BY prefixes finish before identity paging and residual filtering (#1632)")]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(false, 20)]
    [InlineData(true, 20)]
    public async Task Execute_DuplicatePrefixes_ShouldReadCompleteBucketsAndBoundFetchesAsync(bool reverse, int offset)
    {
        // Arrange: every physical trailing key differs; ties span several cursor batches.
        await using var engine = DocumentDatabaseEngine.Create("ordered-buckets", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var collection = await session.CreateCollectionAsync("items", CancellationToken.None);
        for (int i = 255; i >= 0; i--)
        { await collection.PutAsync(session, i.ToString("D3"), Encoding.UTF8.GetBytes($"{{\"v\":{i / 128},\"tail\":{255 - i},\"keep\":{(i % 2 == 0 ? "true" : "false")}}}"), cancellationToken: CancellationToken.None); }
        await session.ExecuteAsync("CREATE INDEX ix ON items (v INCLUDE MISSING, tail)", cancellationToken: CancellationToken.None);
        string direction = reverse ? "DESC" : "ASC";
        string page = $"LIMIT 50 OFFSET {offset}";
        var expected = await IdsAsync(session, $"SELECT META().id FROM items WHERE keep ORDER BY CASE WHEN TRUE THEN v ELSE v END {direction} {page}");
        session.LastQueryDocumentsRead.ShouldBe(256);

        // Act
        var actual = await IdsAsync(session, $"SELECT META().id FROM items WHERE keep ORDER BY v {direction} {page}");

        // Assert: one full 128-entry prefix bucket, plus boundary lookahead. Offset 20
        // needs six survivors from the second bucket, whose metadata must all be read.
        actual.ShouldBe(expected);
        session.LastQueryIndexEntriesRead.ShouldBe(offset == 0 ? 129 : 256);
        session.LastQueryDocumentsRead.ShouldBe(offset == 0 ? 99 : 139);
        _output.WriteLine($"direction={direction}; offset={offset}; entries advanced={session.LastQueryIndexEntriesRead}; documents fetched={session.LastQueryDocumentsRead}; sorted fallback fetches=256.");
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - Unique-key pages stop the ordered cursor before scanning the collection (#1632)")]
    public async Task Execute_Limit50_ShouldAdvanceOnlyWindowAndBoundaryAsync()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("ordered-window", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var collection = await session.CreateCollectionAsync("items", CancellationToken.None);
        for (int i = 255; i >= 0; i--)
        { await collection.PutAsync(session, i.ToString("D3"), Encoding.UTF8.GetBytes($"{{\"v\":{i}}}"), cancellationToken: CancellationToken.None); }
        await session.ExecuteAsync("CREATE INDEX ix ON items (v INCLUDE MISSING)", cancellationToken: CancellationToken.None);

        // Act / Assert
        foreach (string direction in new[] { "ASC", "DESC" })
        {
            foreach (int offset in new[] { 0, 20 })
            {
                string query = $"SELECT META().id FROM items WHERE v >= 0 ORDER BY v {direction} LIMIT 50 OFFSET {offset}";
                var fallback = await IdsAsync(session, query.Replace("ORDER BY v", "ORDER BY CASE WHEN TRUE THEN v ELSE v END", StringComparison.Ordinal));
                session.LastQueryDocumentsRead.ShouldBe(256);
                var ids = await IdsAsync(session, query);
                ids.ShouldBe(fallback);
                ids.ShouldBe((direction == "ASC" ? Enumerable.Range(0, 256) : Enumerable.Range(0, 256).Reverse()).Skip(offset).Take(50).Select(i => i.ToString("D3")));
                session.LastQueryIndexEntriesRead.ShouldBe(51 + offset);
                session.LastQueryDocumentsRead.ShouldBe(50 + offset);
                _output.WriteLine($"{direction} OFFSET {offset} LIMIT 50: entries advanced={session.LastQueryIndexEntriesRead}; documents fetched={session.LastQueryDocumentsRead}; sorted range fallback=256 visible entries/256 fetches.");
            }
        }
        (await IdsAsync(session, "SELECT META().id FROM items ORDER BY v LIMIT 0")).ShouldBeEmpty();
        session.LastQueryIndexEntriesRead.ShouldBe(0);
        session.LastQueryDocumentsRead.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - Ordered scans preserve full-result dynamic type inference (#1632)")]
    public async Task Execute_DynamicProjection_ShouldElideSortAndRetainTypeInferenceAsync()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("ordered-types", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.CreateCollectionAsync("items", CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO items (KEY, VALUE) VALUES ('a', {'v':1,'p':1}), ('b', {'v':2,'p':'text'})", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("CREATE INDEX ix ON items (v INCLUDE MISSING)", cancellationToken: CancellationToken.None);

        // Act / Assert
        await using var result = (DatabaseResultSet)await session.ExecuteAsync("SELECT p, v AS sort_key FROM items ORDER BY sort_key LIMIT 1", cancellationToken: CancellationToken.None);
        result.Columns[0].Type.ShouldBe(DatabaseType.Null);
        session.LastQueryDocumentsRead.ShouldBe(2);
        session.LastQueryIndexEntriesRead.ShouldBe(2);
        var plan = await DetailsAsync(session, "EXPLAIN SELECT p, v AS sort_key FROM items ORDER BY sort_key LIMIT 1");
        plan.ShouldContain(detail => detail.Contains("sort=elided; early_paging=False; paging_reason=result_type_inference", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - Complete equal-key buckets charge the existing statement budget (#1632)")]
    public async Task Execute_OversizedTieBucket_ShouldFailBudgetBeforeFetchingAndReleaseStatementAsync()
    {
        // Arrange: even LIMIT 1 must retain this whole metadata bucket before emitting.
        await using var engine = DocumentDatabaseEngine.Create("ordered-budget", new() { StatementMemoryBudget = 16 * 1024 });
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var collection = await session.CreateCollectionAsync("items", CancellationToken.None);
        for (int i = 0; i < 128; i++)
        { await collection.PutAsync(session, i.ToString("D3"), "{\"v\":1}"u8.ToArray(), cancellationToken: CancellationToken.None); }
        await session.ExecuteAsync("CREATE INDEX ix ON items (v INCLUDE MISSING)", cancellationToken: CancellationToken.None);

        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("SELECT META().id FROM items ORDER BY v LIMIT 1", cancellationToken: CancellationToken.None));

        // Assert
        error.Diagnostics[0].Code.ShouldBe("CD05018");
        session.LastQueryDocumentsRead.ShouldBe(0);
        await session.ExecuteAsync("DROP INDEX ix ON items", cancellationToken: CancellationToken.None);
        await session.CreateCollectionAsync("empty", CancellationToken.None);
        (await IdsAsync(session, "SELECT META().id FROM empty LIMIT 1")).ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - VALUE and RAW retain sorting when projected MISSING collapses into a NULL tie (#1632)")]
    [InlineData("VALUE")]
    [InlineData("RAW")]
    public async Task Execute_ValueUnknownTie_ShouldRetainFallbackAsync(string mode)
    {
        // Arrange: source MISSING precedes NULL, but the projected values tie and
        // the secondary descending key puts the NULL document before MISSING.
        await using var engine = DocumentDatabaseEngine.Create("ordered-value-null", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.CreateCollectionAsync("items", CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO items (KEY, VALUE) VALUES ('a', {'v':NULL,'b':2}), ('z', {'b':1}), ('n', {'v':1,'b':3})", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("CREATE INDEX ix ON items (v INCLUDE MISSING, b)", cancellationToken: CancellationToken.None);

        // Act / Assert
        foreach (string direction in new[] { "ASC", "DESC" })
        {
            await using var result = (DatabaseResultSet)await session.ExecuteAsync($"SELECT {mode} v FROM items ORDER BY v {direction} LIMIT 1", cancellationToken: CancellationToken.None);
            session.LastQueryIndexEntriesRead.ShouldBe(0);
            var details = await DetailsAsync(session, $"EXPLAIN SELECT {mode} v FROM items ORDER BY v {direction}");
            details.ShouldContain(detail => detail.Contains("order_reason=value_missing_becomes_null", StringComparison.Ordinal));
        }
        var admitted = await DetailsAsync(session, $"EXPLAIN SELECT {mode} v FROM items WHERE v IS NOT MISSING ORDER BY v");
        admitted.ShouldContain(detail => detail.Contains("consumed=True; sort=elided", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - Complex query keys and row-shaping stages preserve sorted fallback results (#1632)")]
    public async Task Execute_UnsupportedOrderShapes_ShouldMatchFallbackAsync()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("ordered-fallbacks", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.CreateCollectionAsync("items", CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO items (KEY, VALUE) VALUES ('z', {'v':[2],'arr':[1,1]}), ('a', {'v':[1],'arr':[2]}), ('c', {'v':{'a':2}}), ('b', {'v':{'a':1}}), ('m', {}), ('n', {'v':NULL})", cancellationToken: CancellationToken.None);
        string[] queries = ["SELECT META().id FROM items ORDER BY v LIMIT 3 OFFSET 1", "SELECT META().id FROM items ORDER BY v DESC LIMIT 3 OFFSET 1",
            "SELECT META().id FROM items UNNEST arr x ORDER BY v LIMIT 2 OFFSET 1", "SELECT DISTINCT v FROM items ORDER BY v",
            "SELECT v, COUNT(*) FROM items GROUP BY v ORDER BY v DESC"];
        var before = new List<string[]>();
        foreach (string query in queries) { before.Add(await RowsAsync(query)); }
        await session.ExecuteAsync("CREATE INDEX ix ON items (v INCLUDE MISSING)", cancellationToken: CancellationToken.None);

        // Act / Assert
        for (int i = 0; i < queries.Length; i++)
        {
            (await RowsAsync(queries[i])).ShouldBe(before[i]);
            session.LastQueryIndexEntriesRead.ShouldBe(0);
        }

        async Task<string[]> RowsAsync(string query)
        {
            await using var result = (DatabaseResultSet)await session.ExecuteAsync(query, cancellationToken: CancellationToken.None);
            var rows = new List<string>();
            await foreach (var row in result.GetRowsAsync(CancellationToken.None))
            { rows.Add(string.Join("|", Enumerable.Range(0, row.FieldCount).Select(i => row.GetValue(i)?.ToString() ?? "NULL"))); }
            return rows.ToArray();
        }
    }

    private static async Task<string[]> IdsAsync(DocumentDatabaseSession session, string query)
    {
        await using var result = (DatabaseResultSet)await session.ExecuteAsync(query, cancellationToken: CancellationToken.None);
        var ids = new List<string>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None)) { ids.Add((string)row.GetValue(0)!); }
        return ids.ToArray();
    }

    private static async Task<string[]> DetailsAsync(DocumentDatabaseSession session, string query)
    {
        await using var result = (DatabaseResultSet)await session.ExecuteAsync(query, cancellationToken: CancellationToken.None);
        var details = new List<string>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None)) { if (row.GetValue(5) is string detail) { details.Add(detail); } }
        return details.ToArray();
    }
}
