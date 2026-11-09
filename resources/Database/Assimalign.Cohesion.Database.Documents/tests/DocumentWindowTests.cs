using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Documents.Internal;
using Assimalign.Cohesion.Database.Documents.Language;
using Assimalign.Cohesion.Database.Execution;

namespace Assimalign.Cohesion.Database.Documents.Tests;

public sealed class DocumentWindowTests
{
    [Theory(DisplayName = "Cohesion Test [Documents] - Windows: seeded brute force partition peer and navigation oracle")]
    [InlineData(false, "")]
    [InlineData(true, "")]
    [InlineData(false, "NULLS FIRST")]
    [InlineData(true, "NULLS FIRST")]
    [InlineData(false, "NULLS LAST")]
    [InlineData(true, "NULLS LAST")]
    public async Task Execute_GeneratedPartitions_ShouldMatchOracleAsync(bool descending, string placement)
    {
        // Arrange: missing, null, mixed numeric representations and ordinal string collation.
        await using var engine = DocumentDatabaseEngine.Create("window-oracle", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var random = new Random(1805);
        string?[] palette = [null, "null", "false", "true", "0", "1", "1.0", "-2.5", "\"A\"", "\"a\"", "\"é\"", "\"☃\""];
        long[] offsets = [0, 1, 2, 9223372036854775807];
        for (int trial = 0; trial < 64; trial++)
        {
            int count = trial < 2 ? trial : random.Next(2, 35);
            var objects = new List<string>();
            for (int i = 0; i < count; i++)
            {
                string? p = palette[random.Next(palette.Length)], o = palette[random.Next(palette.Length)];
                if (trial == 2 && i < 2) { p = o = i == 0 ? "1" : "1.0"; }
                objects.Add("{\"id\":" + i + (p is null ? "" : ",\"p\":" + p) + (o is null ? "" : ",\"o\":" + o) + "}");
            }
            using var input = JsonDocument.Parse("[" + string.Join(",", objects) + "]");
            var source = input.RootElement.EnumerateArray().ToArray();
            long offset = offsets[trial % offsets.Length];
            int buckets = trial % 2 == 0 ? count + 3 : 3;
            string direction = descending ? "DESC" : "ASC";
            // LENGTH supplies Int64 while JSON supplies Decimal, preserving each key's value.
            const string partitionKey = "CASE WHEN r.id % 2 = 0 AND r.p = 1 THEN LENGTH('x') ELSE r.p END";
            const string orderKey = "CASE WHEN r.id % 2 = 0 AND r.o = 1 THEN LENGTH('x') ELSE r.o END";
            string text = $"SELECT r.id AS id, ROW_NUMBER() OVER w AS n, RANK() OVER w AS rank, DENSE_RANK() OVER w AS dense, PERCENT_RANK() OVER w AS percent, CUME_DIST() OVER w AS cumulative, NTILE({buckets}) OVER w AS bucket, LAG(r.id,{offset},-1) OVER w AS previous, LEAD(r.id,{offset},-1) OVER w AS next FROM @rows AS r WINDOW base AS (PARTITION BY {partitionKey}), w AS (base ORDER BY {orderKey} {direction} {placement})";
            // Act
            var actual = await RowsAsync(session, text, new() { ["rows"] = input.RootElement });
            // Assert: derive each permitted peer permutation from ROW_NUMBER, never pin input ties.
            actual.Count.ShouldBe(count);
            foreach (var partition in source.GroupBy(r => Key(r, "p")))
            {
                var ids = partition.Select(r => r.GetProperty("id").GetInt32()).ToHashSet();
                var ordered = actual.Where(r => ids.Contains((int)Number(r[0]))).OrderBy(r => Number(r[1])).ToArray();
                var expectedBuckets = new List<long>();
                for (int b = 1; b <= Math.Min(buckets, ordered.Length); b++)
                { expectedBuckets.AddRange(Enumerable.Repeat((long)b, ordered.Length / buckets + (b <= ordered.Length % buckets ? 1 : 0))); }
                for (int i = 0; i < ordered.Length; i++)
                {
                    int id = (int)Number(ordered[i][0]);
                    var key = source[id];
                    int earlier = partition.Count(r => Compare(r, key) < 0);
                    int through = partition.Count(r => Compare(r, key) <= 0);
                    int dense = partition.Where(r => Compare(r, key) < 0).Select(r => Key(r, "o")).Distinct().Count() + 1;
                    Number(ordered[i][1]).ShouldBe(i + 1);
                    Number(ordered[i][2]).ShouldBe(earlier + 1);
                    Number(ordered[i][3]).ShouldBe(dense);
                    Number(ordered[i][4]).ShouldBe(ordered.Length == 1 ? 0m : (decimal)earlier / (ordered.Length - 1));
                    Number(ordered[i][5]).ShouldBe((decimal)through / ordered.Length);
                    Number(ordered[i][6]).ShouldBe(expectedBuckets[i]);
                    Number(ordered[i][7]).ShouldBe(offset <= i ? Number(ordered[(int)(i - offset)][0]) : -1m);
                    Number(ordered[i][8]).ShouldBe(offset < ordered.Length - i ? Number(ordered[(int)(i + offset)][0]) : -1m);
                    if (i > 0) { Compare(source[(int)Number(ordered[i - 1][0])], key).ShouldBeLessThanOrEqualTo(0); }
                }
            }

            int Compare(JsonElement a, JsonElement b)
            {
                bool aFound = a.TryGetProperty("o", out var av), bFound = b.TryGetProperty("o", out var bv);
                bool au = !aFound || av.ValueKind == JsonValueKind.Null, bu = !bFound || bv.ValueKind == JsonValueKind.Null;
                bool first = placement == "NULLS FIRST" || placement == "" && !descending;
                if (au != bu) { return au == first ? -1 : 1; }
                int comparison = au ? (aFound ? 1 : 0).CompareTo(bFound ? 1 : 0) : ValueCompare(av, bv);
                return descending ? -comparison : comparison;
            }
        }
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - Windows: grouping HAVING aliases no-order peers and full partitions precede paging")]
    public async Task Execute_StageOrder_ShouldUseCompletePartitionsAsync()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("window-stages", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        // Act / Assert
        (await RowsAsync(session, "SELECT RANK() OVER () AS r,DENSE_RANK() OVER () AS d,PERCENT_RANK() OVER () AS p,CUME_DIST() OVER () AS c FROM [1,2,3] AS x"))
            .Select(r => string.Join(",", r)).ShouldBe(new[] { "1,1,0,1", "1,1,0,1", "1,1,0,1" });
        var page = await RowsAsync(session, "SELECT ROW_NUMBER() OVER (ORDER BY x) AS n, LEAD(x) OVER (ORDER BY x) AS following FROM [3,1,2] AS x ORDER BY n LIMIT 1 OFFSET 1");
        page.ShouldHaveSingleItem().Select(Number).ShouldBe(new[] { 2m, 3m });
        var grouped = await RowsAsync(session, "SELECT x AS k,COUNT(*) AS count,RANK() OVER (ORDER BY COUNT(*) DESC) AS r FROM [1,1,2,3,3,3] AS x GROUP BY x HAVING COUNT(*) > 1 ORDER BY r");
        grouped.Select(r => string.Join(",", r)).ShouldBe(new[] { "3,3,1", "1,2,2" });
        var aliases = await RowsAsync(session, "SELECT g AS alias_value,items.g AS source_value,ROW_NUMBER() OVER (ORDER BY g) AS a,ROW_NUMBER() OVER (ORDER BY items.g) AS b FROM [{x:1,g:20},{x:2,g:10}] AS items GROUP BY x AS g,items.g ORDER BY a");
        aliases.Select(r => string.Join(",", r)).ShouldBe(new[] { "1,20,1,2", "2,10,2,1" });
        var named = await RowsAsync(session, "SELECT LAG(x,1,-9) OVER w AS v FROM [NULL,1,2] AS x WINDOW w AS (ORDER BY x) ORDER BY ROW_NUMBER() OVER w");
        named[0][0].ShouldBe(-9m); named[1][0].ShouldBeNull(); named[2][0].ShouldBe(1m);
        var nested = await RowsAsync(session, "SELECT RAW q.n FROM (SELECT ROW_NUMBER() OVER () AS n FROM [1,2,3] AS x) AS q ORDER BY q.n DESC LIMIT 1");
        Number(nested.ShouldHaveSingleItem()[0]).ShouldBe(3m);
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Windows: binding and unsupported argument refusals precede empty input")]
    [InlineData("SELECT RANK() OVER w FROM [] AS x", "CD01042")]
    [InlineData("SELECT RANK() OVER w FROM [] AS x WINDOW w AS (later), later AS ()", "CD01042")]
    [InlineData("SELECT RANK() OVER () FROM [] AS x WINDOW w AS (),w AS ()", "CD01041")]
    [InlineData("SELECT RANK() OVER (w PARTITION BY x) FROM [] AS x WINDOW w AS ()", "CD01043")]
    [InlineData("SELECT RANK() OVER (w ORDER BY x) FROM [] AS x WINDOW w AS (ORDER BY x)", "CD01043")]
    [InlineData("SELECT RANK() OVER w FROM [] AS x WINDOW w AS (w)", "CD01042")]
    [InlineData("SELECT NTILE($n) OVER () FROM [] AS x", "CK00001")]
    [InlineData("SELECT LAG(x,$n) OVER () FROM [] AS x", "CK00001")]
    [InlineData("SELECT NTILE(0) OVER () FROM [] AS x", "CK00001")]
    [InlineData("SELECT NTILE(x) OVER () FROM [] AS x", "CK00001")]
    [InlineData("SELECT LAG(x,-1) OVER () FROM [] AS x", "CK00001")]
    [InlineData("SELECT LEAD(x,1.5) OVER () FROM [] AS x", "CK00001")]
    public async Task Execute_InvalidWindow_ShouldRefuseBeforeDataAsync(string text, string code)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("window-refusal", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        // Act / Assert
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(text, new Dictionary<string, object?> { ["n"] = 1L }, CancellationToken.None));
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(code);
        error.Diagnostics[0].Start.ShouldNotBeNull();
        if (text.Contains("$n", StringComparison.Ordinal))
        { error.Diagnostics[0].Start.ShouldBe(text.IndexOf("$n", StringComparison.Ordinal)); error.Diagnostics[0].End.ShouldBe(error.Diagnostics[0].Start + 2); }
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - Windows: navigation defaults evaluate only outside the partition")]
    public async Task Execute_DefaultExpression_ShouldEvaluateLazilyAsync()
    {
        // Arrange
        int defaults = 0, values = 0;
        var builder = DocumentDatabaseEngine.CreateBuilder("window-defaults");
        builder.Functions.Add(DocumentScalarFunction.Create<decimal, decimal>("fallback", n => { defaults++; return -n; }));
        builder.Functions.Add(DocumentScalarFunction.Create<decimal, decimal>("target_value", n =>
        { values++; if (n == 3) { throw new InvalidOperationException("An unused navigation value must stay unevaluated."); } return n; }));
        await using var engine = builder.Build();
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        // Act
        var rows = await RowsAsync(session, "SELECT LAG(target_value(x),1,fallback(x)) OVER (ORDER BY x) AS n FROM [1,2,3] AS x ORDER BY x");
        // Assert
        rows.Select(r => Number(r[0])).ShouldBe(new[] { -1m, 1m, 2m });
        defaults.ShouldBe(1);
        values.ShouldBe(2);
        (await RowsAsync(session, "SELECT LAG(target_value(x),9223372036854775807,-1) OVER () AS n FROM [1,2,3] AS x"))
            .Select(r => Number(r[0])).ShouldBe(new[] { -1m, -1m, -1m });
        values.ShouldBe(2);
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - Windows: unknown navigation cells implicit paths and stored paging preserve their contracts")]
    public async Task Execute_StoredAndMissingValues_ShouldPreserveSemanticsAsync()
    {
        // Arrange
        int keys = 0;
        var builder = DocumentDatabaseEngine.CreateBuilder("window-stored");
        builder.Functions.Add(DocumentScalarFunction.Create<decimal, decimal>("sort_key", value => { keys++; return value; }));
        await using var engine = builder.Build();
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.CreateCollectionAsync("items", CancellationToken.None);
        await session.ExecuteAsync("INSERT INTO items (KEY,VALUE) VALUES ('a',{v:3}),('b',{v:1}),('c',{v:2})", cancellationToken: CancellationToken.None);
        await session.ExecuteAsync("CREATE INDEX ordered ON items (v INCLUDE MISSING)", cancellationToken: CancellationToken.None);
        // Act / Assert
        var rows = await RowsAsync(session, "SELECT ROW_NUMBER() OVER (ORDER BY sort_key(items.v)) AS n,LEAD(v) OVER (ORDER BY sort_key(v)) AS next FROM items ORDER BY v LIMIT 1");
        rows.ShouldHaveSingleItem().Select(Number).ShouldBe(new[] { 1m, 2m });
        keys.ShouldBe(3); // EquivalentPath recognizes implicit items.v and v and shares the sort.
        session.LastQueryDocumentsRead.ShouldBe(3);
        var joined = await RowsAsync(session, "SELECT items.*,ROW_NUMBER() OVER (ORDER BY items.v) AS n FROM items JOIN items AS other ON META(other).id=META(items).id ORDER BY n LIMIT 1");
        ((JsonElement)joined.ShouldHaveSingleItem()[0]!).GetProperty("v").GetDecimal().ShouldBe(1m);
        Number(joined[0][1]).ShouldBe(1m);
        var plan = await RowsAsync(session, "EXPLAIN SELECT RANK() OVER () AS r FROM items");
        plan.Select(r => r[2]).ShouldBe(new object?[] { "Select", "Project", "WindowAggregate", "CollectionScan" });
        plan[2][5].ShouldBe("functions=RANK; partitions=complete; spill=False; sort_groups=1; group1={partition=[]; order=[]; functions=RANK}");
        await using var missing = (DatabaseResultSet)await session.ExecuteAsync("SELECT LAG(x.v,0,1/0) OVER () AS n FROM [{},{v:NULL},{v:2}] AS x", cancellationToken: CancellationToken.None);
        int ordinal = 0;
        await foreach (var row in missing.GetRowsAsync(CancellationToken.None))
        {
            row.IsMissing(0).ShouldBe(ordinal == 0);
            if (ordinal == 2) { Number(row.GetValue(0)).ShouldBe(2m); }
            ordinal++;
        }
        ordinal.ShouldBe(3);
        (await RowsAsync(session, "SELECT NTILE(9223372036854775807) OVER (ORDER BY x) AS n FROM [3,1,2] AS x ORDER BY n"))
            .Select(r => Number(r[0])).ShouldBe(new[] { 1m, 2m, 3m });
        (await RowsAsync(session, "SELECT RAW RANK() OVER () FROM [1] AS x UNION ALL SELECT RAW RANK() OVER () FROM [2] AS y"))
            .Select(r => Number(r[0])).ShouldBe(new[] { 1m, 1m });
        (await RowsAsync(session, "SELECT RAW (SELECT RAW ROW_NUMBER() OVER w FROM [1,2] AS x WINDOW w AS (ORDER BY CASE WHEN y=1 THEN x ELSE -x END)) FROM [1,2] AS y"))
            .Select(r => ((JsonElement)r[0]!).GetRawText()).ShouldBe(new[] { "[1,2]", "[2,1]" });
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - Windows: direct aggregate arguments refuse placement before grouping keys")]
    public async Task Execute_DirectNestedWindow_ShouldRefusePlacementAsync()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("window-direct", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var key = new OqlPathExpression([new OqlPathSegment("x", null)]);
        var window = new OqlWindowExpression(new OqlCallExpression("RANK", []), new OqlWindowSpecification([], [new OqlOrdering(key, false)]));
        var query = new OqlSelectExpression("", "x", [new OqlProjection(new OqlCallExpression("SUM", [window]), "n")],
            null, [], null, [], false, false, source: new OqlArrayExpression([]));
        // Act / Assert
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(new DocumentQueryRequest(new OqlQueryStatement(query)), CancellationToken.None));
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD01044");
        // An execution-only result frame cannot shadow a caller's identical binding name.
        string name = DocumentPlanExecutor.WindowBindingName;
        var source = new OqlArrayExpression([new OqlObjectExpression([new KeyValuePair<string, OqlExpression>("v", new OqlLiteralExpression(1))])]);
        var sameName = new OqlSelectExpression("", name,
            [new OqlProjection(new OqlPathExpression([new(name, null), new("v", null)]), "v"),
             new OqlProjection(new OqlWindowExpression(new OqlCallExpression("ROW_NUMBER", []), new OqlWindowSpecification([], [])), "n")],
            null, [], null, [], false, false, source: source);
        await using var result = (DatabaseResultSet)await session.ExecuteAsync(new DocumentQueryRequest(new OqlQueryStatement(sameName)), CancellationToken.None);
        int count = 0;
        await foreach (var row in result.GetRowsAsync(CancellationToken.None))
        { Number(row.GetValue(0)).ShouldBe(1m); row.GetInt64(1).ShouldBe(1L); count++; }
        count.ShouldBe(1);
    }


    [Theory(DisplayName = "Cohesion Test [Documents] - Windows: direct OVER shapes have a binding diagnostic (#1805)")]
    [InlineData(false)] [InlineData(true)]
    public async Task Execute_InvalidDirectShape_ShouldRefuseAsync(bool both)
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("window-shape", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var window = new OqlWindowExpression(new OqlCallExpression("RANK", []), both ? new OqlWindowSpecification([], []) : null, both ? "w" : null);
        var query = new OqlSelectExpression("", "x", [new OqlProjection(window, "n")], null, [], null, [], false, false,
            source: new OqlArrayExpression([]));
        // Act / Assert
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(
            new DocumentQueryRequest(new OqlQueryStatement(query)), CancellationToken.None));
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD01043");
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - Windows: system sources DISTINCT and UNNEST execute in stage order (#1805)")]
    public async Task Execute_AdditionalSources_ShouldPreserveStageOrderAsync()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("window-sources", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.CreateCollectionAsync("items", CancellationToken.None);
        // Act / Assert
        await session.ExecuteAsync("CREATE INDEX ix ON items (v)", cancellationToken: CancellationToken.None);
        var system = await RowsAsync(session, "SELECT ROW_NUMBER() OVER (ORDER BY c.COLLECTION_NAME) AS n FROM COHESION_SCHEMA.INDEXES AS c ORDER BY n");
        system.Count.ShouldBeGreaterThan(0);
        system.Select(r => Number(r[0])).ShouldBe(Enumerable.Range(1, system.Count).Select(n => (decimal)n));
        var explain = await RowsAsync(session, "EXPLAIN SELECT RANK() OVER () AS r FROM COHESION_SCHEMA.INDEXES AS c");
        explain.Select(r => r[2]).ShouldContain("WindowAggregate");
        explain.Select(r => r[2]).ShouldContain("SystemCollection");
        (await RowsAsync(session, "SELECT DISTINCT ROW_NUMBER() OVER (ORDER BY x) AS n FROM [1,1,1] AS x ORDER BY n"))
            .Select(r => Number(r[0])).ShouldBe(new[] { 1m, 2m, 3m });
        (await RowsAsync(session, "SELECT u AS v, ROW_NUMBER() OVER (ORDER BY u) AS n FROM [[3,1],[2]] AS a UNNEST a AS u ORDER BY n"))
            .Select(r => string.Join(",", r)).ShouldBe(new[] { "1,1", "2,2", "3,3" });
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - Windows: EXPLAIN names keys and sort sharing (#1805)")]
    public async Task Explain_SharedSpecifications_ShouldNameKeysAndSortGroupsAsync()
    {
        // Arrange
        await using var engine = DocumentDatabaseEngine.Create("window-explain", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.CreateCollectionAsync("items", CancellationToken.None);
        // Act
        var plan = await RowsAsync(session, "EXPLAIN SELECT ROW_NUMBER() OVER (PARTITION BY items.p ORDER BY items.o DESC NULLS FIRST) AS n," +
            "RANK() OVER (PARTITION BY p ORDER BY o DESC NULLS FIRST) AS r,DENSE_RANK() OVER (ORDER BY p) AS d FROM items ORDER BY n");
        // Assert
        string detail = (string)plan.Single(row => Equals(row[2], "WindowAggregate"))[5]!;
        detail.ShouldContain("sort_groups=2", Case.Sensitive);
        detail.ShouldContain("group1={partition=[\"items\".\"p\"]; order=[\"items\".\"o\" DESC NULLS FIRST]; functions=ROW_NUMBER,RANK}", Case.Sensitive);
        detail.ShouldContain("group2={partition=[]; order=[\"p\" ASC NULLS FIRST]; functions=DENSE_RANK}", Case.Sensitive);
        session.LastQueryDocumentsRead.ShouldBe(0);
    }

    private static string Key(JsonElement row, string name) => !row.TryGetProperty(name, out var value) ? "missing" :
        value.ValueKind == JsonValueKind.Number ? "number:" + value.GetDecimal().ToString("G29", CultureInfo.InvariantCulture) : value.GetRawText();
    private static int ValueCompare(JsonElement a, JsonElement b)
    {
        static int Kind(JsonElement v) => v.ValueKind switch { JsonValueKind.False => 0, JsonValueKind.True => 1, JsonValueKind.Number => 2, _ => 3 };
        int kind = Kind(a).CompareTo(Kind(b));
        return kind != 0 ? kind : a.ValueKind == JsonValueKind.Number ? a.GetDecimal().CompareTo(b.GetDecimal()) :
            a.ValueKind == JsonValueKind.String ? string.CompareOrdinal(a.GetString(), b.GetString()) : 0;
    }
    private static decimal Number(object? value) => Convert.ToDecimal(value, CultureInfo.InvariantCulture);
    private static async Task<List<object?[]>> RowsAsync(DocumentDatabaseSession session, string text, Dictionary<string, object?>? parameters = null)
    {
        await using var result = (DatabaseResultSet)await session.ExecuteAsync(text, parameters, CancellationToken.None);
        var rows = new List<object?[]>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None))
        { rows.Add(Enumerable.Range(0, result.Columns.Count).Select(row.GetValue).ToArray()); }
        return rows;
    }
}
