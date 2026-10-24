using System;
using System.Linq;
using System.Threading.Tasks;
using Assimalign.Cohesion.Database.Types;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Graph.Client.Tests;

public sealed class GraphRegisteredFunctionWireTests
{
    [Theory(DisplayName = "Cohesion Test [Graph.Client] - Functions: Query Registered Functions Should Use Server Catalog And Reuse Wire Session")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Query_RegisteredFunctions_ShouldUseServerCatalogAndReuseWireSessionAsync(bool tcp)
    {
        var callbackFailure = new InvalidOperationException("callback sentinel");
        await using var harness = await GraphClientTestHarness.StartAsync(tcp, functions =>
        {
            functions.Add(GraphScalarFunction.Create<long, long>("developer_echo", value => value));
            functions.Add(GraphScalarFunction.Create<long, long>("developer_fail", _ => throw callbackFailure));
            functions.Add(GraphAggregateFunction.Create<long, long, long>("developer_sum",
                () => 0L, (state, value) => state + value, state => state, _ => 8));
        });
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        var first = await connection.QueryAsync("RETURN developer_echo(42) AS echoed", cancellationToken: harness.Token);
        first.Columns.ShouldHaveSingleItem().Type.ShouldBe(DatabaseType.Int64);
        first.ShouldHaveSingleItem()[0].ShouldBe(42L);
        Guid session = harness.Server.Sessions.ShouldHaveSingleItem().Id;
        var grouped = await connection.QueryAsync("FOR v IN [1,2,2,NULL] RETURN developer_sum(DISTINCT v), count(*)", cancellationToken: harness.Token);
        grouped.ShouldHaveSingleItem().ToArray().ShouldBe(new object?[] { 3L, 4L });
        var failure = await Should.ThrowAsync<GraphClientException>(async () =>
            await connection.QueryAsync("RETURN developer_fail(1)", cancellationToken: harness.Token));
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG05021");
        failure.Diagnostics[0].Message.ShouldNotStartWith("CG05021", Case.Sensitive);
        var restored = await connection.QueryAsync("RETURN developer_echo(7)", cancellationToken: harness.Token);
        restored.ShouldHaveSingleItem()[0].ShouldBe(7L);
        harness.Server.Sessions.ShouldHaveSingleItem().Id.ShouldBe(session);
    }
}
