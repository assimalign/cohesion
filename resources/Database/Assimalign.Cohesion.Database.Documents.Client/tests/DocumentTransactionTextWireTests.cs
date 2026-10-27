using System;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Client;
using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Documents.Client.Tests;

[Collection(nameof(DocumentClientEventSourceCollection))]
public sealed class DocumentTransactionTextWireTests
{
    [Theory(DisplayName = "Cohesion Test [Documents.Client] - Transaction spellings retain own writes and isolate other sessions on both transports (#1725)")]
    [InlineData(false, "BEGIN", "COMMIT", true)]
    [InlineData(true, "BEGIN", "COMMIT", true)]
    [InlineData(false, "BEGIN WORK", "ROLLBACK WORK", false)]
    [InlineData(true, "BEGIN WORK", "ROLLBACK WORK", false)]
    [InlineData(false, "BEGIN TRAN", "COMMIT TRAN", true)]
    [InlineData(true, "BEGIN TRAN", "COMMIT TRAN", true)]
    [InlineData(false, "BEGIN TRANSACTION ISOLATION LEVEL SNAPSHOT", "ROLLBACK TRANSACTION", false)]
    [InlineData(true, "BEGIN TRANSACTION ISOLATION LEVEL SNAPSHOT", "ROLLBACK TRANSACTION", false)]
    [InlineData(false, "START WORK", "COMMIT WORK", true)]
    [InlineData(true, "START WORK", "COMMIT WORK", true)]
    [InlineData(false, "START TRAN", "ROLLBACK TRAN", false)]
    [InlineData(true, "START TRAN", "ROLLBACK TRAN", false)]
    [InlineData(false, "START TRANSACTION ISOLATION LEVEL READ COMMITTED", "COMMIT TRANSACTION", true)]
    [InlineData(true, "START TRANSACTION ISOLATION LEVEL READ COMMITTED", "COMMIT TRANSACTION", true)]
    public async Task ExecuteAsync_TransactionSpellings_ShouldPreserveVisibility(bool tcp, string begin, string end, bool committed)
    {
        // Arrange
        await using var harness = await DocumentClientTestHarness.StartAsync(tcp);
        await using var writer = await harness.Client.ConnectAsync(harness.Token);
        await writer.CreateCollectionAsync("items", harness.Token);
        await using var peer = CreatePeer(harness);
        await using var reader = await peer.ConnectAsync(harness.Token);

        // Act / Assert
        (await writer.ExecuteAsync(begin, cancellationToken: harness.Token)).ShouldBe(0);
        harness.Server.Sessions.ShouldContain(session => session.DatabaseSession!.CurrentTransaction != null);
        await writer.PutAsync("items", "pending", "{}"u8.ToArray(), cancellationToken: harness.Token);
        (await writer.GetAsync("items", "pending", harness.Token)).ShouldNotBeNull();
        (await reader.GetAsync("items", "pending", harness.Token)).ShouldBeNull();
        (await writer.ExecuteAsync(end, cancellationToken: harness.Token)).ShouldBe(0);
        (await reader.GetAsync("items", "pending", harness.Token)).HasValue.ShouldBe(committed);
    }

    [Theory(DisplayName = "Cohesion Test [Documents.Client] - Returning active and failed transactions resets the exact session before reuse (#1725)")]
    [InlineData(false, "active")]
    [InlineData(true, "active")]
    [InlineData(false, "query")]
    [InlineData(true, "query")]
    [InlineData(false, "parse")]
    [InlineData(true, "parse")]
    [InlineData(false, "command")]
    [InlineData(true, "command")]
    [InlineData(false, "ddl")]
    [InlineData(true, "ddl")]
    [InlineData(false, "nested")]
    [InlineData(true, "nested")]
    public async Task DisposeAsync_OpenOrFailedTransaction_ShouldResetExactSession(bool tcp, string failure)
    {
        // Arrange
        await using var harness = await DocumentClientTestHarness.StartAsync(tcp);
        await using var first = await harness.Client.ConnectAsync(harness.Token);
        await first.CreateCollectionAsync("items", harness.Token);
        await first.ExecuteAsync("BEGIN", cancellationToken: harness.Token);
        await first.PutAsync("items", "pending", "{}"u8.ToArray(), cancellationToken: harness.Token);
        var session = harness.Server.Sessions.ShouldHaveSingleItem();

        // Act / Assert
        if (failure != "active")
        {
            var error = await Should.ThrowAsync<DocumentClientException>(async () =>
            {
                if (failure == "command") { await first.GetCollectionAsync("absent", harness.Token); }
                else { await first.ExecuteAsync(failure == "ddl" ? "CREATE COLLECTION escaped" : failure == "nested" ? "BEGIN" : failure == "parse" ? "SELECT (" : "SELECT * FROM absent", cancellationToken: harness.Token); }
            });
            if (failure == "ddl") { error.Diagnostics.ShouldContain(item => item.Code == "CD04105"); }
            if (failure == "nested") { error.Diagnostics.ShouldContain(item => item.Code == "CK04004"); }
            session.DatabaseSession!.CurrentTransaction.ShouldNotBeNull().State.ShouldBe(TransactionState.Faulted);
            var aborted = await Should.ThrowAsync<DocumentClientException>(async () => await first.GetCollectionsAsync(harness.Token));
            aborted.Diagnostics.ShouldContain(item => item.Code == "CD04101");
        }
        await first.DisposeAsync();
        session.DatabaseSession!.CurrentTransaction.ShouldBeNull();
        await using var next = await harness.Client.ConnectAsync(harness.Token);
        harness.Server.Sessions.ShouldHaveSingleItem().Id.ShouldBe(session.Id);
        (await next.GetAsync("items", "pending", harness.Token)).ShouldBeNull();
        await next.ExecuteAsync("BEGIN", cancellationToken: harness.Token);
        await next.ExecuteAsync("ROLLBACK", cancellationToken: harness.Token);
    }

    [Theory(DisplayName = "Cohesion Test [Documents.Client] - Explicit recovery and failed COMMIT end faulted transactions without writes (#1725)")]
    [InlineData(false, "ROLLBACK")]
    [InlineData(true, "ROLLBACK")]
    [InlineData(false, "COMMIT")]
    [InlineData(true, "COMMIT")]
    public async Task ExecuteAsync_FaultedTransactionEnd_ShouldDiscardWrites(bool tcp, string end)
    {
        // Arrange
        await using var harness = await DocumentClientTestHarness.StartAsync(tcp);
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        await connection.CreateCollectionAsync("items", harness.Token);
        await connection.ExecuteAsync("BEGIN", cancellationToken: harness.Token);
        await connection.PutAsync("items", "pending", "{}"u8.ToArray(), cancellationToken: harness.Token);
        await Should.ThrowAsync<DocumentClientException>(async () => await connection.QueryAsync("SELECT * FROM absent", cancellationToken: harness.Token));

        // Act
        if (end == "COMMIT")
        {
            var error = await Should.ThrowAsync<DocumentClientException>(async () => await connection.ExecuteAsync(end, cancellationToken: harness.Token));
            error.Diagnostics.ShouldContain(item => item.Code == "CD04101");
        }
        else { await connection.ExecuteAsync(end, cancellationToken: harness.Token); }

        // Assert
        harness.Server.Sessions.ShouldHaveSingleItem().DatabaseSession!.CurrentTransaction.ShouldBeNull();
        (await connection.GetAsync("items", "pending", harness.Token)).ShouldBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [Documents.Client] - Disconnect rolls back open writes and releases the writer for a new session (#1725)")]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    public async Task AbortAsync_OpenTransaction_ShouldRollbackBeforeNewWriter(bool tcp, bool serverDisconnect, bool clientShutdown)
    {
        // Arrange
        await using var harness = await DocumentClientTestHarness.StartAsync(tcp);
        await using var first = await harness.Client.ConnectAsync(harness.Token);
        await first.CreateCollectionAsync("items", harness.Token);
        await first.ExecuteAsync("BEGIN", cancellationToken: harness.Token);
        await first.PutAsync("items", "pending", "{}"u8.ToArray(), cancellationToken: harness.Token);
        var session = harness.Server.Sessions.ShouldHaveSingleItem();

        // Act
        if (serverDisconnect) { await session.DisposeAsync(); }
        if (clientShutdown)
        {
            await harness.Client.DisposeAsync();
            await first.DisposeAsync();
        }
        else { await first.AbortAsync(); }
        await using var replacement = CreatePeer(harness);
        await using var next = await (clientShutdown ? replacement : harness.Client).ConnectAsync(harness.Token);
        await next.PutAsync("items", "clean", "{}"u8.ToArray(), cancellationToken: harness.Token);

        // Assert: the successful new writer proves teardown released the old writer lock.
        (await next.GetAsync("items", "pending", harness.Token)).ShouldBeNull();
        (await next.GetAsync("items", "clean", harness.Token)).ShouldNotBeNull();
        while (harness.Server.Sessions.Count != 1) { await Task.Delay(10, harness.Token); }
        harness.Server.Sessions.ShouldHaveSingleItem().Id.ShouldNotBe(session.Id);
    }

    [Theory(DisplayName = "Cohesion Test [Documents.Client] - Snapshot default and READ COMMITTED retain their wire read horizons (#1725)")]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ExecuteAsync_TransactionIsolation_ShouldUseSelectedReadHorizon(bool tcp, bool readCommitted)
    {
        // Arrange
        await using var harness = await DocumentClientTestHarness.StartAsync(tcp);
        await using var first = await harness.Client.ConnectAsync(harness.Token);
        await first.CreateCollectionAsync("items", harness.Token);
        await using var peer = CreatePeer(harness);
        await using var writer = await peer.ConnectAsync(harness.Token);
        await first.ExecuteAsync(readCommitted ? "BEGIN ISOLATION LEVEL READ COMMITTED" : "BEGIN", cancellationToken: harness.Token);

        // Act
        (await first.GetAsync("items", "later", harness.Token)).ShouldBeNull();
        await writer.PutAsync("items", "later", "{}"u8.ToArray(), cancellationToken: harness.Token);

        // Assert
        (await first.GetAsync("items", "later", harness.Token)).HasValue.ShouldBe(readCommitted);
        await first.ExecuteAsync("COMMIT", cancellationToken: harness.Token);
        (await first.GetAsync("items", "later", harness.Token)).ShouldNotBeNull();
    }

    [Theory(DisplayName = "Cohesion Test [Documents.Client] - Wire response size failures abort explicit transactions before sending Failure (#1725)")]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ExecuteAsync_ResponseSizeFailure_ShouldAbortPendingWrites(bool tcp, bool putPreflight)
    {
        // Arrange: a one-character collection makes the maximum Put request's response one byte larger.
        await using var harness = await DocumentClientTestHarness.StartAsync(tcp);
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        await connection.CreateCollectionAsync("c", harness.Token);
        byte[] content = new byte[putPreflight ? (int)ProtocolFrameHeader.MaxPayloadLength - 16 : 9 * 1024 * 1024];
        Array.Fill(content, (byte)'x');
        "{\"text\":\""u8.CopyTo(content);
        "\"}"u8.CopyTo(content.AsSpan(content.Length - 2));
        if (!putPreflight) { await connection.PutAsync("c", "x", content, cancellationToken: harness.Token); }
        await connection.ExecuteAsync("BEGIN", cancellationToken: harness.Token);
        await connection.PutAsync("c", "pending", "{}"u8.ToArray(), cancellationToken: harness.Token);
        var session = harness.Server.Sessions.ShouldHaveSingleItem();

        // Act
        var failure = await Should.ThrowAsync<DocumentClientException>(async () =>
        {
            if (putPreflight) { await connection.PutAsync("c", "x", content, cancellationToken: harness.Token); }
            else { await connection.QueryAsync("SELECT text, text AS copy FROM c", cancellationToken: harness.Token); }
        });

        // Assert: response preflight and serialization retain the same failed-statement contract.
        failure.Diagnostics.ShouldContain(item => item.Code == "CD05016");
        session.DatabaseSession!.CurrentTransaction.ShouldNotBeNull().State.ShouldBe(TransactionState.Faulted);
        var refused = await Should.ThrowAsync<DocumentClientException>(async () => await connection.GetCollectionsAsync(harness.Token));
        refused.Diagnostics.ShouldContain(item => item.Code == "CD04101");
        if (putPreflight)
        {
            var commit = await Should.ThrowAsync<DocumentClientException>(async () => await connection.ExecuteAsync("COMMIT", cancellationToken: harness.Token));
            commit.Diagnostics.ShouldContain(item => item.Code == "CD04101");
        }
        else { await connection.ExecuteAsync("ROLLBACK", cancellationToken: harness.Token); }
        (await connection.GetAsync("c", "pending", harness.Token)).ShouldBeNull();
        (await connection.GetAsync("c", "x", harness.Token)).HasValue.ShouldBe(!putPreflight);
        await connection.DisposeAsync();
        await using var next = await harness.Client.ConnectAsync(harness.Token);
        harness.Server.Sessions.ShouldHaveSingleItem().Id.ShouldBe(session.Id);
        (await next.GetCollectionsAsync(harness.Token)).ShouldBe(["c"]);
    }

    private static DocumentClient CreatePeer(DocumentClientTestHarness harness) => DocumentClient.Create(new()
    {
        Settings = new DatabaseConnectionSettings { Database = "documents", Principal = "peer", EndPoint = harness.Listener.EndPoint, MaxPoolSize = 1 },
        ConnectionFactory = harness.Factory,
    });
}
