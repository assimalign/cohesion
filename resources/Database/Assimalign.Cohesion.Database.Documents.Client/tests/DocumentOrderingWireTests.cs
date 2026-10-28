using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Documents.Client.Tests;

public sealed class DocumentOrderingWireTests
{
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
