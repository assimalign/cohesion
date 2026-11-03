using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Documents.Client.Tests;

public sealed class DocumentSetOperationWireTests
{
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
}
