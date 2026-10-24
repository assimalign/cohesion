using System;
using System.Linq;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Documents.Client.Tests;

public sealed class DocumentRegisteredFunctionWireTests
{
    [Theory(DisplayName = "Cohesion Test [Documents.Client] - Functions: registered scalar aggregates and coded faults cross both transports")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueryAsync_RegisteredFunctions_ShouldPreserveValuesCodesAndConnectionAsync(bool tcp)
    {
        // The harness timeout is a forty-second hang guard, not a performance assertion.
        await using var harness = await DocumentClientTestHarness.StartAsync(tcp, configureEngine: builder =>
        {
            builder.Functions.Add(DocumentScalarFunction.Create<string, string>("slug", value => value.ToLowerInvariant()));
            builder.Functions.Add(DocumentAggregateFunction.Create<decimal, decimal, decimal>("total_value", () => 0m, (state, value) => state + value, state => state));
            builder.Functions.Add(DocumentScalarFunction.Create<decimal, decimal>("fail_value", _ => throw new InvalidOperationException("application wire failure")));
            builder.Functions.Add(DocumentScalarFunction.Create<DocumentValue, string>("presence", value => value.IsMissing ? "missing" : value.IsNull ? "null" : "valued",
                DocumentNullBehavior.CalledOnNullOrMissingInput));
        });
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        await connection.CreateCollectionAsync("items", harness.Token);
        await connection.PutAsync("items", "a", "{\"name\":\"Alpha\",\"amount\":2}"u8.ToArray(), cancellationToken: harness.Token);
        await connection.PutAsync("items", "b", "{\"name\":\"Beta\",\"amount\":4}"u8.ToArray(), cancellationToken: harness.Token);

        var rows = await connection.QueryAsync("SELECT slug(name) AS name, presence(absent) AS missing, presence(NULL) AS nil FROM items ORDER BY slug(name)", cancellationToken: harness.Token);
        rows.Select(row => row[0]).ShouldBe(new object?[] { "alpha", "beta" });
        rows.All(row => Equals(row[1], "missing") && Equals(row[2], "null")).ShouldBeTrue();
        rows.Columns.Select(column => column.Type).ShouldBe([DatabaseType.String, DatabaseType.String, DatabaseType.String]);
        var aggregate = await connection.QueryAsync("SELECT total_value(amount) AS total FROM items", cancellationToken: harness.Token);
        aggregate.ShouldHaveSingleItem()[0].ShouldBe(6m);
        aggregate.Columns.ShouldHaveSingleItem().Type.ShouldBe(DatabaseType.Decimal);
        const string statement = "SELECT fail_value(amount) FROM items";
        var failure = await Should.ThrowAsync<DocumentClientException>(async () => await connection.QueryAsync(statement, cancellationToken: harness.Token));
        failure.Code.ShouldBe(ProtocolErrorCode.ExecutionFailure);
        var diagnostic = failure.Diagnostics.ShouldHaveSingleItem();
        diagnostic.Code.ShouldBe("CD05017");
        diagnostic.Start.ShouldBe(7);
        diagnostic.End.ShouldBe(statement.IndexOf(" FROM", StringComparison.Ordinal));
        diagnostic.Message.ShouldBe("Document function 'fail_value' failed: application wire failure");
        failure.Message.ShouldBe("CD05017: " + diagnostic.Message);
        (await connection.QueryAsync("SELECT slug(name) FROM items WHERE amount = 2", cancellationToken: harness.Token)).ShouldHaveSingleItem()[0].ShouldBe("alpha");
        connection.IsOpen.ShouldBeTrue();
    }
}
