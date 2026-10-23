using System.Threading.Tasks;
using Assimalign.Cohesion.Database.Protocol;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Documents.Client.Tests;

public sealed class DocumentUniqueIndexWireTests
{
    [Theory(DisplayName = "Cohesion Test [Documents.Client] - UNIQUE failures use ExecutionFailure and preserve the connection (#1135)")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Execute_UniqueViolation_ShouldFailWithoutClosingConnectionAsync(bool tcp)
    {
        // Arrange.
        await using var harness = await DocumentClientTestHarness.StartAsync(tcp);
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        await connection.CreateCollectionAsync("items", harness.Token);
        await connection.PutAsync("items", "one", "{\"a\":1,\"b\":2}"u8.ToArray(), cancellationToken: harness.Token);
        await connection.ExecuteAsync("CREATE UNIQUE INDEX uq ON items (a,b)", cancellationToken: harness.Token);

        // Act / Assert: both data and DDL constraint failures leave this rental usable.
        var put = await Should.ThrowAsync<DocumentClientException>(async () =>
            await connection.PutAsync("items", "two", "{\"a\":1.00,\"b\":2}"u8.ToArray(), cancellationToken: harness.Token));
        put.Code.ShouldBe(ProtocolErrorCode.ExecutionFailure);
        ((int)put.Code).ShouldBe(6);
        put.Message.ShouldContain("uq"); put.Message.ShouldContain("items");
        put.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD03020");
        (await connection.QueryAsync("SELECT a FROM items WHERE a = 1 AND b = 2", cancellationToken: harness.Token)).ShouldHaveSingleItem()[0].ShouldBe(1m);
        await connection.ExecuteAsync("DROP INDEX uq ON items", cancellationToken: harness.Token);
        await connection.PutAsync("items", "two", "{\"a\":1,\"b\":2}"u8.ToArray(), cancellationToken: harness.Token);
        var build = await Should.ThrowAsync<DocumentClientException>(async () =>
            await connection.ExecuteAsync("CREATE UNIQUE INDEX uq ON items (a,b)", cancellationToken: harness.Token));
        build.Code.ShouldBe(ProtocolErrorCode.ExecutionFailure);
        build.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD03020");
        (await connection.QueryAsync("SELECT * FROM COHESION_SCHEMA.INDEXES", cancellationToken: harness.Token)).ShouldBeEmpty();
        (await connection.QueryAsync("SELECT COUNT(*) FROM items", cancellationToken: harness.Token)).ShouldHaveSingleItem()[0].ShouldBe(2L);
    }
}
