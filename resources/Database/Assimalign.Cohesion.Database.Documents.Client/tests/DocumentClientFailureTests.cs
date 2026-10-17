using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Client;
using Assimalign.Cohesion.Database.Protocol;

namespace Assimalign.Cohesion.Database.Documents.Client.Tests;

public sealed class DocumentClientFailureTests
{
    [Fact(DisplayName = "Cohesion Test [Documents.Client] - A terminal error drains queued transport bytes before disposal")]
    public async Task ConnectAsync_DeferredErrorSend_ShouldDeliverTypedFailureBeforeClosing()
    {
        // Arrange: the transport accepts flushes but defers delivery until output completes.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var token = timeout.Token;
        await using var listener = new DeferredOutputListener();
        await using var engine = await DocumentDatabaseEngine.CreateBuilder("drain").AddDatabase("documents")
            .AddServer(options => options.Listener = listener).BuildAsync(token);
        await engine.Servers.Single().StartAsync(token);
        await using var client = DocumentClient.Create(new()
        {
            Settings = new DatabaseConnectionSettings { Database = "absent", EndPoint = listener.EndPoint },
            ConnectionFactory = listener.Factory,
        });

        // Act
        var failure = await Should.ThrowAsync<DocumentClientException>(async () => await client.ConnectAsync(token));

        // Assert
        failure.Code.ShouldBe(ProtocolErrorCode.DatabaseNotFound);
        listener.CompletedOutputs.ShouldBe(1);
        listener.DiscardedBytes.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Documents.Client] - A near-frame-sized missing identifier fails without closing the session")]
    public async Task GetCollectionAsync_LargeMissingName_ShouldBoundErrorAndKeepSession()
    {
        // Arrange
        await using var harness = await DocumentClientTestHarness.StartAsync();
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        string name = new('x', (int)ProtocolFrameHeader.MaxPayloadLength - 9);

        // Act
        var failure = await Should.ThrowAsync<DocumentClientException>(async () => await connection.GetCollectionAsync(name, harness.Token));

        // Assert
        failure.Code.ShouldBe(ProtocolErrorCode.ExecutionFailure);
        failure.Message.Length.ShouldBeLessThan(4200);
        failure.Message.ShouldEndWith(" [truncated]");
        connection.IsOpen.ShouldBeTrue();
        (await connection.GetCollectionsAsync(harness.Token)).ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [Documents.Client] - Statement failures keep the same pooled session")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueryAsync_StatementFailures_ShouldReuseExactSession(bool tcp)
    {
        // Arrange
        await using var harness = await DocumentClientTestHarness.StartAsync(tcp);
        Guid sessionId;
        await using (var connection = await harness.Client.ConnectAsync(harness.Token))
        {
            sessionId = harness.Server.Sessions.ShouldHaveSingleItem().Id;
            await connection.CreateCollectionAsync("items", harness.Token);
            await connection.PutAsync("items", "one", "{\"n\":2}"u8.ToArray(), cancellationToken: harness.Token);

            // Act / Assert
            foreach (string statement in new[] { "SELECT * FROM items LIMIT 1", "BEGIN", "COMMIT", "ROLLBACK" })
            {
                var error = await Should.ThrowAsync<DocumentClientException>(async () => await connection.QueryAsync(statement, cancellationToken: harness.Token));
                error.Code.ShouldBe(ProtocolErrorCode.ParseFailure);
                error.Diagnostics.ShouldContain(d => d.Code == "COHDBL001");
                error.Diagnostics.ShouldAllBe(d => d.Start.HasValue && d.End.HasValue && d.Line > 0);
                connection.IsOpen.ShouldBeTrue();
            }
            foreach (string statement in new[] { "SELECT * FROM absent", "SELECT n / 0 FROM items", "DROP INDEX absent ON items" })
            {
                var error = await Should.ThrowAsync<DocumentClientException>(async () => await connection.QueryAsync(statement, cancellationToken: harness.Token));
                error.Code.ShouldBe(ProtocolErrorCode.ExecutionFailure);
                error.Diagnostics.ShouldNotBeNull();
                (await connection.QueryAsync("SELECT n FROM items", cancellationToken: harness.Token)).ShouldHaveSingleItem()[0].ShouldBe(2m);
            }
            await Should.ThrowAsync<DocumentClientException>(async () => await connection.CreateCollectionAsync("items", harness.Token));
            // Valid wire JSON, invalid stored JSON: duplicate properties fail the statement, not the connection.
            await Should.ThrowAsync<DocumentClientException>(async () => await connection.PutAsync("items", "one", "{\"n\":3,\"n\":4}"u8.ToArray(), cancellationToken: harness.Token));
            (await connection.QueryAsync("SELECT n FROM items", cancellationToken: harness.Token)).ShouldHaveSingleItem()[0].ShouldBe(2m);
            harness.Server.Sessions.ShouldHaveSingleItem().Id.ShouldBe(sessionId);
        }
        await using var again = await harness.Client.ConnectAsync(harness.Token);
        (await again.GetCollectionsAsync(harness.Token)).ShouldBe(["items"]);
        harness.Server.Sessions.ShouldHaveSingleItem().Id.ShouldBe(sessionId);
    }

    [Theory(DisplayName = "Cohesion Test [Documents.Client] - A server-closed connection fails with a typed error")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueryAsync_ClosedByServer_ShouldFailAndDiscardSession(bool tcp)
    {
        // Arrange
        await using var harness = await DocumentClientTestHarness.StartAsync(tcp);
        var connection = await harness.Client.ConnectAsync(harness.Token);
        Guid first = harness.Server.Sessions.ShouldHaveSingleItem().Id;
        await harness.Server.Sessions.Single().DisposeAsync();

        // Act
        await Should.ThrowAsync<DocumentClientException>(async () => await connection.GetCollectionsAsync(harness.Token));

        // Assert
        connection.IsOpen.ShouldBeFalse();
        await connection.DisposeAsync();
        await using var again = await harness.Client.ConnectAsync(harness.Token);
        (await again.GetCollectionsAsync(harness.Token)).ShouldBeEmpty();
        harness.Server.Sessions.ShouldHaveSingleItem().Id.ShouldNotBe(first);
        await Should.ThrowAsync<ObjectDisposedException>(async () => await connection.GetCollectionsAsync(harness.Token));
        await connection.DisposeAsync();
        again.IsOpen.ShouldBeTrue();
    }

    [Theory(DisplayName = "Cohesion Test [Documents.Client] - Unknown database is refused during handshake on both transports")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConnectAsync_UnknownDatabase_ShouldFailWithDatabaseNotFound(bool tcp)
    {
        // Arrange
        await using var harness = await DocumentClientTestHarness.StartAsync(tcp);
        await using var client = DocumentClient.Create(new()
        {
            Settings = new DatabaseConnectionSettings { Database = "absent", EndPoint = harness.Listener.EndPoint },
            ConnectionFactory = harness.Factory,
        });

        // Act
        var error = await Should.ThrowAsync<DocumentClientException>(async () =>
        {
            await using var connection = await client.ConnectAsync(harness.Token);
        });

        // Assert
        error.Code.ShouldBe(ProtocolErrorCode.DatabaseNotFound);
        error.Diagnostics.ShouldBeEmpty();
    }
}
