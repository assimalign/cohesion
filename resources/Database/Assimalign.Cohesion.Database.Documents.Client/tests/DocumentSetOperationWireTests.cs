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
    [Theory(DisplayName = "Cohesion Test [Documents.Client] - UNION: TCP runs the generated RAW document and typed-column first-representative oracle")]
    [InlineData(false, "raw")]
    [InlineData(true, "raw")]
    [InlineData(false, "document")]
    [InlineData(true, "document")]
    [InlineData(false, "columns")]
    [InlineData(true, "columns")]
    public async Task QueryAsync_GeneratedValues_ShouldMatchOracleOverTcpAsync(bool all, string mode)
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
            if (!all) { expected = expected.DistinctBy(Canonical, StringComparer.Ordinal); }
            string text = named ? "SELECT a.a AS a,a.b AS b FROM @a AS a UNION " + (all ? "ALL " : "") + (mode == "columns" ? "SELECT b.a AS a,b.b AS b FROM @b AS b" : "SELECT b.b AS b,b.a AS a FROM @b AS b")
                : "SELECT RAW a FROM @a AS a UNION " + (all ? "ALL " : "") + "SELECT RAW b FROM @b AS b";
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
                if (trial == 0 && !all)
                { cells.ShouldBe(new[] { ("{\"z\":1,\"a\":2}", 3m), ("{\"r\":4,\"q\":5}", 4.5m) }); }
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
            ("SELECT RAW 1 FROM [1] AS a INTERSECT ALL SELECT RAW 2 FROM [1] AS b", "CK00001"),
            ("SELECT RAW 1 FROM [1] AS a EXCEPT SELECT RAW 2 FROM [1] AS b", "CK00001") })
        {
            var error = await Should.ThrowAsync<DocumentClientException>(async () => await connection.QueryAsync(test.Item1, cancellationToken: harness.Token));
            error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(test.Item2);
            error.Diagnostics[0].Start.ShouldNotBeNull();
        }
        connection.IsOpen.ShouldBeTrue();
        (await connection.QueryAsync("SELECT RAW 1 FROM [1] AS x UNION SELECT RAW 1 FROM [1] AS y", cancellationToken: harness.Token)).Count.ShouldBe(1);
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
