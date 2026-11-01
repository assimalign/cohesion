using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Documents.Client.Tests;

public sealed class DocumentOrderingWireTests
{
    [Theory(DisplayName = "Cohesion Test [Documents.Client] - Ordered indexes preserve missing keys, complete ties, residuals and pages on both transports (#1632)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueryAsync_OrderedIndex_ShouldMatchForcedSortAndExplainConsumptionAsync(bool tcp)
    {
        // Arrange: trailing keys reverse the physical order within query-key ties.
        await using var harness = await DocumentClientTestHarness.StartAsync(tcp);
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        await connection.CreateCollectionAsync("items", harness.Token);
        for (int i = 135; i >= 0; i--)
        {
            string key = i < 2 ? "" : "\"v\":" + (i < 4 ? "null" : (i / 68).ToString(System.Globalization.CultureInfo.InvariantCulture)) + ",";
            string json = "{" + key + "\"tail\":" + (135 - i) + ",\"keep\":" + (i % 2 == 0 ? "true" : "false") + "}";
            await connection.PutAsync("items", i.ToString("D3"), Encoding.UTF8.GetBytes(json), cancellationToken: harness.Token);
        }
        await connection.ExecuteAsync("CREATE INDEX ix ON items (v INCLUDE MISSING, tail)", cancellationToken: harness.Token);

        // Act / Assert
        foreach (string direction in new[] { "ASC", "DESC", "ASC NULLS LAST", "DESC NULLS FIRST" })
        {
            string tail = direction + " LIMIT 50 OFFSET 1";
            var expected = await connection.QueryAsync("SELECT META().id AS id FROM items WHERE keep ORDER BY CASE WHEN TRUE THEN v ELSE v END " + tail, cancellationToken: harness.Token);
            var actual = await connection.QueryAsync("SELECT META().id AS id FROM items WHERE keep ORDER BY v " + tail, cancellationToken: harness.Token);
            actual.Select(row => row.GetValue(0)).ShouldBe(expected.Select(row => row.GetValue(0)));
        }
        var plan = await connection.QueryAsync("EXPLAIN SELECT META().id FROM items WHERE v >= 0 AND keep ORDER BY v DESC LIMIT 50 OFFSET 1", cancellationToken: harness.Token);
        plan.Documents.ShouldContain(document => document.GetProperty("operator").GetString() == "IndexSeek" &&
            document.GetProperty("detail").GetString()!.Contains("consumed=True; sort=elided", System.StringComparison.Ordinal));
        var fallback = await connection.QueryAsync("EXPLAIN SELECT META().id FROM items ORDER BY v NULLS LAST", cancellationToken: harness.Token);
        fallback.Documents.ShouldContain(document => document.GetProperty("operator").GetString() == "IndexOrderCandidate" &&
            document.GetProperty("detail").GetString()!.Contains("order_reason=null_placement_mismatch", System.StringComparison.Ordinal));
        var dynamicPage = await connection.QueryAsync("SELECT v FROM items ORDER BY v LIMIT 1", cancellationToken: harness.Token);
        dynamicPage.Count.ShouldBe(1);
        dynamicPage.Single().IsMissing(0).ShouldBeTrue();
        dynamicPage.Columns.Single().Type.ShouldBe(Types.DatabaseType.Decimal);
        var valuePlan = await connection.QueryAsync("EXPLAIN SELECT VALUE v FROM items ORDER BY v", cancellationToken: harness.Token);
        valuePlan.Documents.ShouldContain(document => document.GetProperty("operator").GetString() == "IndexOrderCandidate" &&
            document.GetProperty("detail").GetString()!.Contains("order_reason=value_missing_becomes_null", System.StringComparison.Ordinal));
    }

    [Theory(DisplayName = "Cohesion Test [Documents.Client] - Ordered tie-bucket budget failure preserves CD05018 and explicit transaction recovery (#1632)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueryAsync_OrderedBucketBudget_ShouldFailAndReuseConnectionAsync(bool tcp)
    {
        // Arrange
        await using var harness = await DocumentClientTestHarness.StartAsync(tcp,
            configureEngine: builder => builder.Options.StatementMemoryBudget = 16 * 1024);
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        await connection.CreateCollectionAsync("items", harness.Token);
        for (int i = 0; i < 128; i++)
        { await connection.PutAsync("items", i.ToString("D3"), "{\"v\":1}"u8.ToArray(), cancellationToken: harness.Token); }
        await connection.ExecuteAsync("CREATE INDEX ix ON items (v INCLUDE MISSING)", cancellationToken: harness.Token);
        await connection.ExecuteAsync("BEGIN", cancellationToken: harness.Token);

        // Act
        var failure = await Should.ThrowAsync<DocumentClientException>(async () =>
            await connection.QueryAsync("SELECT META().id FROM items ORDER BY v LIMIT 1", cancellationToken: harness.Token));

        // Assert
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD05018");
        var faulted = await Should.ThrowAsync<DocumentClientException>(async () =>
            await connection.QueryAsync("SELECT 1 FROM items", cancellationToken: harness.Token));
        faulted.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD04101");
        await connection.ExecuteAsync("ROLLBACK", cancellationToken: harness.Token);
        (await connection.GetAsync("items", "000", harness.Token)).ShouldNotBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [Documents.Client] - Both transports preserve null placement, alias ordering and paging (#1730)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueryAsync_NullPlacement_ShouldMatchAllCombinationsAsync(bool tcp)
    {
        // Arrange
        await using var harness = await DocumentClientTestHarness.StartAsync(tcp);
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        await connection.CreateCollectionAsync("items", harness.Token);
        string[] ids = ["m", "n", "f", "t", "l", "h", "s", "a", "o"];
        string[] values = ["{}", "{\"v\":null}", "{\"v\":false}", "{\"v\":true}", "{\"v\":-1}", "{\"v\":1}", "{\"v\":\"a\"}", "{\"v\":[]}", "{\"v\":{}}"];
        for (int i = ids.Length - 1; i >= 0; i--)
        { await connection.PutAsync("items", ids[i], Encoding.UTF8.GetBytes(values[i]), cancellationToken: harness.Token); }
        (string Suffix, string Ids)[] cases =
        [
            ("ASC", "m,n,f,t,l,h,s,a,o"), ("DESC", "o,a,s,h,l,t,f,n,m"),
            ("ASC NULLS FIRST", "m,n,f,t,l,h,s,a,o"), ("ASC NULLS LAST", "f,t,l,h,s,a,o,m,n"),
            ("DESC NULLS FIRST", "n,m,o,a,s,h,l,t,f"), ("DESC NULLS LAST", "o,a,s,h,l,t,f,n,m"),
        ];

        // Act / Assert
        foreach (var test in cases)
        {
            var rows = await connection.QueryAsync($"SELECT META().id AS id, v AS k FROM items ORDER BY k {test.Suffix}, META().id DESC NULLS LAST", cancellationToken: harness.Token);
            rows.Select(row => row.GetValue(0)).ShouldBe(test.Ids.Split(','));
            var page = await connection.QueryAsync($"SELECT META().id AS id, v AS k FROM items ORDER BY k {test.Suffix} OFFSET 1 LIMIT 4", cancellationToken: harness.Token);
            page.Select(row => row.GetValue(0)).ShouldBe(test.Ids.Split(',').Skip(1).Take(4));
        }
        await connection.ExecuteAsync("CREATE INDEX ix ON items (v INCLUDE MISSING)", cancellationToken: harness.Token);
        var schema = await connection.QueryAsync("SELECT PATH, INCLUDE_MISSING FROM COHESION_SCHEMA.INDEXES", cancellationToken: harness.Token);
        schema.Single().GetValue(0).ShouldBe("v INCLUDE MISSING");
        schema.Single().GetValue(1).ShouldBe(true);
        var plan = await connection.QueryAsync("EXPLAIN SELECT META().id FROM items ORDER BY v DESC NULLS LAST", cancellationToken: harness.Token);
        plan.Documents.ShouldContain(document => document.GetProperty("operator").GetString() == "IndexOrderCandidate" &&
            document.GetProperty("detail").GetString()!.Contains("order_reason=complex_values"));
    }
}
