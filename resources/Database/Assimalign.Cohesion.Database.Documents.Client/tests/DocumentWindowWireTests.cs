using System;
using System.Linq;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Documents.Client.Tests;

public sealed class DocumentWindowWireTests
{
    [Theory(DisplayName = "Cohesion Test [Documents.Client] - Windows: complete partitions navigation diagnostics and EXPLAIN cross both transports")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Query_Windows_ShouldPreserveWireContractAsync(bool tcp)
    {
        // Arrange
        await using var harness = await DocumentClientTestHarness.StartAsync(tcp);
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        // Act
        var rows = await connection.QueryAsync("SELECT ROW_NUMBER() OVER w AS n,RANK() OVER w AS rank,DENSE_RANK() OVER w AS dense,PERCENT_RANK() OVER w AS percent,CUME_DIST() OVER w AS cumulative,NTILE(8) OVER w AS bucket,LAG(x,0) OVER w AS previous,LEAD(x) OVER w AS next FROM [2,1,1] AS x WINDOW base AS (),w AS (base ORDER BY x) ORDER BY n LIMIT 1 OFFSET 1", cancellationToken: harness.Token);
        var unknown = await connection.QueryAsync("SELECT ROW_NUMBER() OVER (PARTITION BY x.p ORDER BY x.o) AS n FROM [{},{p:NULL},{p:NULL},{p:1},{p:1.0}] AS x ORDER BY n", cancellationToken: harness.Token);
        var plan = await connection.QueryAsync("EXPLAIN SELECT RANK() OVER () AS r FROM [1,2] AS x", cancellationToken: harness.Token);
        // Assert
        var row = rows.ShouldHaveSingleItem();
        row["n"].ShouldBe(2L); row["rank"].ShouldBe(1L); row["dense"].ShouldBe(1L);
        row["percent"].ShouldBe(0m); row["cumulative"].ShouldBe(2m / 3);
        row["bucket"].ShouldBe(2L); row["previous"].ShouldBe(1m); row["next"].ShouldBe(2m);
        rows.Columns.Select(c => c.Type).ShouldBe(new[] { DatabaseType.Int64, DatabaseType.Int64, DatabaseType.Int64, DatabaseType.Decimal, DatabaseType.Decimal, DatabaseType.Int64, DatabaseType.Decimal, DatabaseType.Decimal });
        unknown.Select(r => r["n"]).ShouldBe(new object?[] { 1L, 1L, 1L, 2L, 2L });
        plan.Documents.Count(d => d.GetProperty("operator").GetString() == "WindowAggregate").ShouldBe(1);
        var refusal = await Should.ThrowAsync<DocumentClientException>(async () => await connection.QueryAsync("SELECT NTILE(0) OVER () FROM [] AS x", cancellationToken: harness.Token));
        refusal.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CK00001");
        (await connection.QueryAsync("SELECT RAW ROW_NUMBER() OVER () FROM [1] AS x", cancellationToken: harness.Token)).Documents.ShouldHaveSingleItem().GetInt64().ShouldBe(1L);
    }

    [Theory(DisplayName = "Cohesion Test [Documents.Client] - Windows: memory refusal is structured and a later query reuses the connection")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Query_WindowBudget_ShouldRefuseAndReuseAsync(bool tcp)
    {
        // Arrange
        await using var harness = await DocumentClientTestHarness.StartAsync(tcp, configureEngine: builder => builder.Options.StatementMemoryBudget = 8192);
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        string values = string.Join(",", Enumerable.Range(0, 64));
        // Act
        var error = await Should.ThrowAsync<DocumentClientException>(async () => await connection.QueryAsync("SELECT ROW_NUMBER() OVER () AS n FROM [" + values + "] AS x LIMIT 1", cancellationToken: harness.Token));
        // Assert
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD05018");
        (await connection.QueryAsync("SELECT RAW 1 FROM [1] AS x", cancellationToken: harness.Token)).Documents.ShouldHaveSingleItem().GetDecimal().ShouldBe(1m);
    }
}
