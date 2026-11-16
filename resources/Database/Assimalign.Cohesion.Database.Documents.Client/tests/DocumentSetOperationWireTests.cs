using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Documents.Client.Tests;

public sealed class DocumentSetOperationWireTests
{
    [Theory(DisplayName = "Cohesion Test [Documents.Client] - Set operations: TCP runs the generated multiset first-representative oracle")]
    [InlineData(false, "raw", "UNION")]
    [InlineData(true, "raw", "UNION")]
    [InlineData(false, "document", "UNION")]
    [InlineData(true, "document", "UNION")]
    [InlineData(false, "columns", "UNION")]
    [InlineData(true, "columns", "UNION")]
    [InlineData(false, "raw", "INTERSECT")]
    [InlineData(true, "raw", "INTERSECT")]
    [InlineData(false, "document", "INTERSECT")]
    [InlineData(true, "document", "INTERSECT")]
    [InlineData(false, "columns", "INTERSECT")]
    [InlineData(true, "columns", "INTERSECT")]
    [InlineData(false, "raw", "EXCEPT")]
    [InlineData(true, "raw", "EXCEPT")]
    [InlineData(false, "document", "EXCEPT")]
    [InlineData(true, "document", "EXCEPT")]
    [InlineData(false, "columns", "EXCEPT")]
    [InlineData(true, "columns", "EXCEPT")]
    public async Task QueryAsync_GeneratedValues_ShouldMatchOracleOverTcpAsync(bool all, string mode, string operation)
    {
        // Arrange: the first 16 trials of the engine's 64-trial generator, with the same seed.
        await using var harness = await DocumentClientTestHarness.StartAsync(tcp: true);
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        bool named = mode != "raw";
        var random = new Random(1803);
        string[] palette = ["null", "true", "false", "1", "1.0", "2", "-1", "1.25", "\"a\"", "[]", "[1,null]", "[1.0,null]", "{}", "{\"a\":1,\"b\":2}", "{\"b\":2.0,\"a\":1.0}"];
        if (named) { palette = ["{}", "{\"a\":null}", "{\"b\":null}", "{\"a\":1}", "{\"a\":1.0}", "{\"a\":1,\"b\":2}", "{\"b\":2.0,\"a\":1.0}", "{\"a\":[1,null],\"b\":{\"x\":2}}", "{\"b\":{\"x\":2.0},\"a\":[1.0,null]}"]; }
        if (mode == "columns") { palette = ["{\"a\":{\"x\":1,\"y\":2},\"b\":1}", "{\"a\":{\"y\":2.0,\"x\":1.0},\"b\":1.0}", "{\"a\":[1,null],\"b\":-1}", "{\"a\":[1.0,null],\"b\":-1.0}", "{\"a\":{},\"b\":1.25}"]; }
        for (int trial = 0; trial < 16; trial++)
        {
            string[] left = Enumerable.Range(0, random.Next(25)).Select(_ => palette[random.Next(palette.Length)]).ToArray();
            string[] right = Enumerable.Range(0, random.Next(25)).Select(_ => palette[random.Next(palette.Length)]).ToArray();
            // Pin a left representative, duplicates within/across inputs, then a right representative.
            if (mode == "columns" && trial == 0)
            {
                left = ["{\"a\":{\"z\":1,\"a\":2},\"b\":3.00}", "{\"a\":{\"a\":2.0,\"z\":1.0},\"b\":3}"];
                right = [left[1], "{\"a\":{\"r\":4,\"q\":5},\"b\":4.50}", "{\"a\":{\"q\":5.0,\"r\":4.0},\"b\":4.5}"];
            }
            using var a = JsonDocument.Parse("[" + string.Join(",", left) + "]");
            using var b = JsonDocument.Parse("[" + string.Join(",", right) + "]");
            using var projected = JsonDocument.Parse("[" + string.Join(",", a.RootElement.EnumerateArray().Select(value => Project(value, named, false))
                .Concat(b.RootElement.EnumerateArray().Select(value => Project(value, named, mode == "document")))) + "]");
            IEnumerable<JsonElement> expected = projected.RootElement.EnumerateArray();
            if (operation != "UNION")
            {
                var values = expected.ToArray();
                var counts = values.Skip(left.Length).GroupBy(Canonical).ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
                var kept = new List<JsonElement>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var value in values.Take(left.Length))
                {
                    string key = Canonical(value);
                    bool present = counts.GetValueOrDefault(key) > 0;
                    if (all && present) { counts[key]--; }
                    if (present == (operation == "INTERSECT") && (all || seen.Add(key))) { kept.Add(value); }
                }
                expected = kept;
            }
            else if (!all) { expected = expected.DistinctBy(Canonical, StringComparer.Ordinal); }
            string text = named ? "SELECT a.a AS a,a.b AS b FROM @a AS a " + operation + " " + (all ? "ALL " : "") + (mode == "columns" ? "SELECT b.a AS a,b.b AS b FROM @b AS b" : "SELECT b.b AS b,b.a AS a FROM @b AS b")
                : "SELECT RAW a FROM @a AS a " + operation + " " + (all ? "ALL " : "") + "SELECT RAW b FROM @b AS b";
            // Act
            var result = await connection.QueryAsync(text, new Dictionary<string, object?> { ["a"] = a.RootElement, ["b"] = b.RootElement }, harness.Token);
            // Assert: canonical keys choose equality; JSON keeps member order and numeric cells compare by value.
            if (mode == "columns")
            {
                var cells = result.Select(row => (row[0].ShouldBeOfType<JsonElement>().GetRawText(), Convert.ToDecimal(row[1], CultureInfo.InvariantCulture))).ToArray();
                result.Columns.Select(column => column.Name).ShouldBe(new[] { "a", "b" });
                result.Columns.Select(column => column.Type).ShouldBe(expected.Any()
                    ? new[] { DatabaseType.Json, DatabaseType.Decimal } : new[] { DatabaseType.Null, DatabaseType.Null });
                cells.ShouldBe(expected.Select(value => (value.GetProperty("a").GetRawText(), value.GetProperty("b").GetDecimal())));
                if (trial == 0 && !all && operation == "UNION")
                {
                    cells.ShouldBe(new[] { ("{\"z\":1,\"a\":2}", 3m), ("{\"r\":4,\"q\":5}", 4.5m) });
                    result.Select(row => ((decimal)row[1]!).ToString(CultureInfo.InvariantCulture)).ShouldBe(new[] { "3.00", "4.50" });
                }
            }
            else
            {
                result.Columns.Single().Name.ShouldBe(named ? "document" : "value");
                result.Documents.Select(value => value.GetRawText()).ShouldBe(expected.Select(value => value.GetRawText()));
            }
        }
    }

    [Theory(DisplayName = "Cohesion Test [Documents.Client] - UNION: static headers values pages MISSING and diagnostics cross both transports")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueryAsync_Compounds_ShouldPreserveWireShapesAndReuseAsync(bool tcp)
    {
        // Arrange
        await using var harness = await DocumentClientTestHarness.StartAsync(tcp);
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        // Act
        var raw = await connection.QueryAsync("SELECT RAW x FROM [1,1,NULL] AS x UNION SELECT VALUE y FROM [1.0,2,NULL] AS y", cancellationToken: harness.Token);
        var columns = await connection.QueryAsync("SELECT LENGTH('x') AS n FROM [1] AS x UNION ALL SELECT 2 AS n FROM [1] AS y ORDER BY n DESC", cancellationToken: harness.Token);
        var heterogeneous = await connection.QueryAsync("SELECT LENGTH('x') AS n FROM [1] AS x UNION ALL SELECT 's' AS n FROM [1] AS y UNION ALL SELECT 2 AS n FROM [1] AS z", cancellationToken: harness.Token);
        var whole = await connection.QueryAsync("SELECT x AS a FROM [2,1] AS x UNION ALL SELECT y AS b FROM [4,3] AS y ORDER BY a ASC NULLS LAST,b DESC OFFSET 1 LIMIT 2", cancellationToken: harness.Token);
        var missing = await connection.QueryAsync("SELECT MISSING AS a FROM [1] AS x UNION SELECT NULL AS a FROM [1] AS y", cancellationToken: harness.Token);
        var mixed = await connection.QueryAsync("SELECT RAW 1 FROM [1] AS x UNION ALL SELECT 2 AS a FROM [1] AS y", cancellationToken: harness.Token);
        var empty = await connection.QueryAsync("SELECT 1 AS a FROM [1/0] AS x UNION SELECT 2 AS b FROM [1/0] AS y LIMIT 0", cancellationToken: harness.Token);
        var page = await connection.QueryAsync("(SELECT x AS n FROM [3,1,2] AS x ORDER BY x LIMIT 2) UNION ALL (SELECT y AS n FROM [5,4] AS y ORDER BY y DESC LIMIT 1) ORDER BY n DESC OFFSET 1 LIMIT 2", cancellationToken: harness.Token);
        // Assert
        raw.Columns.Single().Name.ShouldBe("value");
        raw.Documents.Select(value => value.GetRawText()).ShouldBe(new[] { "1", "null", "2" });
        columns.Columns.Single().Name.ShouldBe("n");
        columns.Columns.Single().Type.ShouldBe(DatabaseType.Decimal);
        columns.Select(row => row[0]).ShouldBe(new object?[] { 2m, 1m });
        heterogeneous.Columns.Single().Type.ShouldBe(DatabaseType.Null);
        heterogeneous.Select(row => row[0]).ShouldBe(new object?[] { 1m, "s", 2m });
        whole.Columns.Single().Name.ShouldBe("document");
        whole.Columns.Single().Type.ShouldBe(DatabaseType.Json);
        whole.Documents.Select(value => value.GetRawText()).ShouldBe(new[] { "{\"a\":2}", "{\"b\":4}" });
        whole.Select(row => row[0]).ShouldAllBe(value => value is JsonElement);
        missing.Count.ShouldBe(2);
        missing[0].IsMissing(0).ShouldBeTrue();
        missing[1].IsMissing(0).ShouldBeFalse();
        missing[1].IsNull(0).ShouldBeTrue();
        mixed.Columns.Single().Name.ShouldBe("document");
        mixed.Documents.Select(value => value.ValueKind).ShouldBe(new[] { JsonValueKind.Number, JsonValueKind.Object });
        empty.Columns.Single().Name.ShouldBe("document");
        empty.ShouldBeEmpty();
        page.Select(row => row[0]).ShouldBe(new object?[] { 2m, 1m });
        var plan = await connection.QueryAsync("EXPLAIN SELECT 1 AS n FROM [1/0] AS a UNION ALL SELECT 2 AS n FROM [1/0] AS b", cancellationToken: harness.Token);
        plan.Documents.Count(value => value.GetProperty("operator").GetString() == "ExpressionSource").ShouldBe(2);
        plan.Documents[0].GetProperty("operator").GetString().ShouldBe("UnionAll");
        foreach (var test in new[] {
            ("SELECT RAW 1 FROM [1] AS a UNION SELECT RAW 2 FROM [1] AS b ORDER BY n", "CD01037"),
            ("SELECT 1 AS n FROM [1] AS a UNION SELECT 2 AS n FROM [1] AS b ORDER BY absent", "CD01038"),
            ("SELECT RAW 1 FROM [1] AS a INTERSECT DISTINCT SELECT RAW 2 FROM [1] AS b", "CK00001"),
            ("SELECT 1 AS n FROM [1] AS a EXCEPT SELECT 2 AS n FROM [1] AS b ORDER BY (SELECT RAW 1 FROM [0] AS z)", "CK00001") })
        {
            var error = await Should.ThrowAsync<DocumentClientException>(async () => await connection.QueryAsync(test.Item1, cancellationToken: harness.Token));
            error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(test.Item2);
            error.Diagnostics[0].Start.ShouldNotBeNull();
        }
        connection.IsOpen.ShouldBeTrue();
        (await connection.QueryAsync("SELECT RAW 1 FROM [1] AS x UNION SELECT RAW 1 FROM [1] AS y", cancellationToken: harness.Token)).Count.ShouldBe(1);
    }

    [Theory(DisplayName = "Cohesion Test [Documents.Client] - Multisets: all query positions preserve values headers atomic writes and session reuse")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueryAsync_MultisetPositions_ShouldPreserveValuesAndReuseAsync(bool tcp)
    {
        // Arrange
        await using var harness = await DocumentClientTestHarness.StartAsync(tcp);
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        await connection.CreateCollectionAsync("dest", harness.Token);
        // Act / Assert
        var intersect = await connection.QueryAsync("SELECT RAW x FROM [1.00,2,1.0,3,2] AS x INTERSECT ALL SELECT RAW y FROM [1,2.0,2] AS y", cancellationToken: harness.Token);
        intersect.Columns.Single().Name.ShouldBe("value");
        intersect.Documents.Select(value => value.GetRawText()).ShouldBe(new[] { "1.00", "2", "2" });
        var except = await connection.QueryAsync("SELECT x AS n FROM [1,1,2,3] AS x EXCEPT ALL SELECT y AS n FROM [1,3] AS y ORDER BY n DESC", cancellationToken: harness.Token);
        except.Select(row => row[0]).ShouldBe(new object?[] { 2m, 1m });
        var rawUnknown = await connection.QueryAsync("SELECT RAW MISSING FROM [0,1] AS x INTERSECT ALL SELECT VALUE NULL FROM [0] AS y", cancellationToken: harness.Token);
        rawUnknown.Documents.ShouldHaveSingleItem().ValueKind.ShouldBe(JsonValueKind.Null);
        var missingUnknown = await connection.QueryAsync("SELECT MISSING AS a FROM [0] AS x EXCEPT SELECT NULL AS a FROM [0] AS y", cancellationToken: harness.Token);
        missingUnknown.ShouldHaveSingleItem().IsMissing(0).ShouldBeTrue();
        var structural = await connection.QueryAsync("SELECT RAW {b:2,a:1.00} FROM [0] AS x INTERSECT SELECT RAW {a:1,b:2.0} FROM [0] AS y", cancellationToken: harness.Token);
        structural.Documents.ShouldHaveSingleItem().GetRawText().ShouldBe("{\"b\":2,\"a\":1.00}");
        var correlated = await connection.QueryAsync("SELECT RAW (SELECT RAW i.n FROM o AS i INTERSECT SELECT RAW o.n FROM [0] AS y) FROM [{n:1},{n:2}] AS o", cancellationToken: harness.Token);
        correlated.Documents.Select(value => value.GetRawText()).ShouldBe(new[] { "[1]", "[2]" });
        var derived = await connection.QueryAsync("SELECT RAW q FROM (SELECT 1 AS a FROM [0] AS x EXCEPT SELECT 1 AS b FROM [0] AS y) AS q", cancellationToken: harness.Token);
        derived.Documents.Single().GetRawText().ShouldBe("{\"a\":1}");
        var namedArray = await connection.QueryAsync("SELECT RAW (SELECT MISSING AS a FROM [0] AS x UNION SELECT NULL AS b FROM [0] AS y) FROM [0] AS seed", cancellationToken: harness.Token);
        namedArray.Documents.Single().GetRawText().ShouldBe("[{},{\"b\":null}]");
        var orderedScope = await connection.QueryAsync("SELECT RAW (SELECT x AS n FROM [2,1] AS x INTERSECT SELECT y AS n FROM [1,2] AS y ORDER BY n) FROM [0] AS n", cancellationToken: harness.Token);
        orderedScope.Documents.Single().GetRawText().ShouldBe("[{\"n\":1},{\"n\":2}]");
        var missingScope = await connection.QueryAsync("SELECT RAW (SELECT x AS n FROM [1,2] AS x EXCEPT ALL SELECT y AS m FROM [] AS y ORDER BY m - n) FROM [0] AS m", cancellationToken: harness.Token);
        missingScope.Documents.Single().GetRawText().ShouldBe("[{\"n\":1},{\"n\":2}]");
        var membership = await connection.QueryAsync("SELECT RAW x FROM [1,2] AS x WHERE x IN ((SELECT RAW 1 FROM [0] AS a) UNION SELECT RAW 2 FROM [0] AS b)", cancellationToken: harness.Token);
        membership.Count.ShouldBe(2);
        (await connection.QueryAsync("SELECT RAW x FROM [1,2] AS x WHERE x IN ((SELECT RAW 1 FROM [0] AS a))", cancellationToken: harness.Token)).ShouldBeEmpty();
        var inserted = await connection.QueryAsync("INSERT INTO dest (KEY k,VALUE v) SELECT 'a' AS k,{n:1} AS v FROM [0] AS x INTERSECT ALL SELECT {n:1} AS v,'a' AS k FROM [0] AS y RETURNING RAW dest.n", cancellationToken: harness.Token);
        inserted.AffectedCount.ShouldBe(1);
        inserted.Documents.Single().GetInt32().ShouldBe(1);
        var upserted = await connection.QueryAsync("UPSERT INTO dest (KEY k) SELECT 'a' AS k,2 AS n FROM [0] AS x EXCEPT SELECT 'b' AS other FROM [0] AS y RETURNING RAW dest.n", cancellationToken: harness.Token);
        upserted.AffectedCount.ShouldBe(1);
        upserted.Documents.Single().GetInt32().ShouldBe(2);
        var invalid = await Should.ThrowAsync<DocumentClientException>(async () => await connection.QueryAsync("INSERT INTO dest (KEY k,VALUE v) SELECT 'safe' AS k,{n:1} AS v FROM [0] AS x UNION ALL SELECT 'bad' AS other,{n:2} AS v FROM [0] AS y", cancellationToken: harness.Token));
        invalid.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD02008");
        (await connection.GetAsync("dest", "safe", harness.Token)).ShouldBeNull();
        var explain = await connection.QueryAsync("EXPLAIN UPSERT INTO dest (KEY k) SELECT 'a' AS k FROM [1/0] AS x EXCEPT ALL SELECT 'b' AS k FROM [1/0] AS y", cancellationToken: harness.Token);
        explain.Documents.Select(value => value.GetProperty("operator").GetString()).ShouldBe(new[] { "UpsertSelect", "ExceptAll", "Select", "Project", "ExpressionSource", "Select", "Project", "ExpressionSource" });
        connection.IsOpen.ShouldBeTrue();
        (await connection.QueryAsync("SELECT RAW x FROM [1/0] AS x INTERSECT ALL SELECT RAW y FROM [1/0] AS y LIMIT 0", cancellationToken: harness.Token)).ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [Documents.Client] - UNION: memory and work refusals preserve structured identities and rollback reuse")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueryAsync_Budgets_ShouldRefuseAndReuseAsync(bool tcp)
    {
        // Arrange
        await using var harness = await DocumentClientTestHarness.StartAsync(tcp, configureEngine: builder =>
        { builder.Options.StatementMemoryBudget = 64 * 1024; builder.Options.StatementWorkBudget = 1500; });
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        await connection.CreateCollectionAsync("items", harness.Token);
        for (int index = 0; index < 80; index++)
        { await connection.QueryAsync("INSERT INTO items (KEY,VALUE) VALUES ('" + index + "',{v:'" + index + new string('x', 512) + "'})", cancellationToken: harness.Token); }
        // Act / Assert
        await connection.QueryAsync("BEGIN", cancellationToken: harness.Token);
        var memory = await Should.ThrowAsync<DocumentClientException>(async () => await connection.QueryAsync("SELECT v FROM items UNION SELECT v FROM items OFFSET 1000", cancellationToken: harness.Token));
        memory.Diagnostics.Single().Code.ShouldBe("CD05018");
        await connection.QueryAsync("ROLLBACK", cancellationToken: harness.Token);
        var all = await connection.QueryAsync("SELECT v FROM items UNION ALL SELECT v FROM items OFFSET 1000", cancellationToken: harness.Token);
        all.ShouldBeEmpty();
        string values = "[" + string.Join(",", Enumerable.Range(0, 500)) + "]";
        var work = await Should.ThrowAsync<DocumentClientException>(async () => await connection.QueryAsync("SELECT RAW x FROM " + values + " AS x UNION ALL SELECT RAW 1 FROM [1] AS y OFFSET 1000", cancellationToken: harness.Token));
        work.Diagnostics.Single().Code.ShouldBe("CD05020");
        foreach (string operationName in new[] { "INTERSECT", "INTERSECT ALL", "EXCEPT", "EXCEPT ALL" })
        {
            await connection.QueryAsync("BEGIN", cancellationToken: harness.Token);
            var retained = await Should.ThrowAsync<DocumentClientException>(async () => await connection.QueryAsync("SELECT RAW 1 FROM [] AS x " + operationName + " SELECT RAW v FROM items", cancellationToken: harness.Token));
            retained.Diagnostics.Single().Code.ShouldBe("CD05018");
            await connection.QueryAsync("ROLLBACK", cancellationToken: harness.Token);
            var counted = await Should.ThrowAsync<DocumentClientException>(async () => await connection.QueryAsync("SELECT RAW 1 FROM [] AS x " + operationName + " SELECT RAW y FROM " + values + " AS y", cancellationToken: harness.Token));
            counted.Diagnostics.Single().Code.ShouldBe("CD05020");
        }
        connection.IsOpen.ShouldBeTrue();
        (await connection.QueryAsync("SELECT RAW 1 FROM [1] AS x UNION SELECT RAW 1 FROM [1] AS y", cancellationToken: harness.Token)).Count.ShouldBe(1);
    }

    private static string Canonical(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number => value.GetDecimal().ToString("G29", CultureInfo.InvariantCulture),
        JsonValueKind.Object => "{" + string.Join(",", value.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal).Select(property => property.Name + ":" + Canonical(property.Value))) + "}",
        JsonValueKind.Array => "[" + string.Join(",", value.EnumerateArray().Select(Canonical)) + "]",
        _ => value.GetRawText(),
    };

    private static string Project(JsonElement value, bool named, bool reverse)
        => !named ? value.GetRawText() : "{" + string.Join(",", (reverse ? new[] { "b", "a" } : new[] { "a", "b" })
            .Where(name => value.TryGetProperty(name, out _)).Select(name => "\"" + name + "\":" + value.GetProperty(name).GetRawText())) + "}";
}
