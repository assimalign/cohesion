using System.Linq;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Protocol;

namespace Assimalign.Cohesion.Database.Documents.Client.Tests;

public sealed class DocumentMergeWireTests
{
    [Theory(DisplayName = "Cohesion Test [Documents.Client] - MERGE: EXPLAIN and the execution refusal cross both transports")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueryAsync_Merge_ShouldDescribeAndRefuseExecutionAsync(bool tcp)
    {
        // Arrange
        await using var harness = await DocumentClientTestHarness.StartAsync(tcp);
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        await connection.CreateCollectionAsync("items", harness.Token);
        await connection.QueryAsync("INSERT INTO items (KEY, VALUE) VALUES ('a',{n:1})", cancellationToken: harness.Token);
        foreach (var on in new[] { "KEY s.k", "META(t).id = s.k" })
        {
            string statement = "MERGE INTO items t USING [{k:'a',n:2}] AS s ON " + on + " WHEN MATCHED THEN UPDATE SET t.n = s.n RETURNING RAW t";
            // Act
            var plan = await connection.QueryAsync("EXPLAIN " + statement, cancellationToken: harness.Token);
            var error = await Should.ThrowAsync<DocumentClientException>(async () => await connection.QueryAsync(statement, cancellationToken: harness.Token));
            // Assert
            plan.Documents.Select(row => row.GetProperty("operator").GetString()).ShouldBe(new[] {
                "Merge", on.StartsWith("KEY", System.StringComparison.Ordinal) ? "KeyLookup" : "KeyJoin", "MergeUpdate", "ReturningValue", "Select", "Project", "ExpressionSource" });
            error.Code.ShouldBe(ProtocolErrorCode.ExecutionFailure);
            error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD05022");
            error.Diagnostics.Single().Start.ShouldBe(0);
            connection.IsOpen.ShouldBeTrue();
        }
        (await connection.QueryAsync("SELECT RAW items.n FROM items", cancellationToken: harness.Token)).Documents.Single().GetInt32().ShouldBe(1);
    }
}
