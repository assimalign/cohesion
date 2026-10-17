using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Connections.InMemory;
using Assimalign.Cohesion.Database.Client;
using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Documents.Client.Tests;

public sealed class DocumentClientProtocolTests
{
    [Theory(DisplayName = "Cohesion Test [Documents.Client] - Blank collection names in command 69 preserve the wire session")]
    [InlineData(false, " ")]
    [InlineData(true, " ")]
    [InlineData(false, "")]
    [InlineData(true, "")]
    [InlineData(false, "\t\u2003\r\n")]
    [InlineData(true, "\t\u2003\r\n")]
    public async Task Server_InvalidCollectionCommandName_ShouldReturnDiagnosticAndKeepSession(bool tcp, string name)
    {
        // Arrange: bypass the typed client's argument guard to exercise command 69 itself.
        await using var harness = await DocumentClientTestHarness.StartAsync(tcp);
        await using var transport = await harness.Factory.ConnectAsync(harness.Listener.EndPoint, harness.Token);
        await using var channel = new ProtocolChannel(transport.AsStream(), DocumentProtocol.Family, leaveOpen: true);
        await WriteAsync(channel, (byte)ProtocolMessageType.Startup, new ProtocolStartupMessage(ProtocolVersion.Current, "documents", "tester").Encode(), harness.Token);
        (await channel.Reader.ReadFrameAsync(harness.Token)).ShouldNotBeNull().Type.ShouldBe(ProtocolMessageType.Authenticate);
        await WriteAsync(channel, (byte)ProtocolMessageType.AuthenticateResponse, [], harness.Token);
        (await channel.Reader.ReadFrameAsync(harness.Token)).ShouldNotBeNull().Type.ShouldBe(ProtocolMessageType.Ready);
        Guid sessionId = harness.Server.Sessions.ShouldHaveSingleItem().Id;
        byte[] encodedName = Encoding.UTF8.GetBytes(name);

        foreach (var operation in new[] { DocumentProtocolOperation.CreateCollection, DocumentProtocolOperation.DropCollection, DocumentProtocolOperation.GetCollection })
        {
            byte[] payload = [(byte)operation, 0, 0, 0, (byte)encodedName.Length, .. encodedName];

            // Act
            await WriteAsync(channel, (byte)DocumentProtocolMessageType.Command, payload, harness.Token);
            var response = (await channel.Reader.ReadFrameAsync(harness.Token)).ShouldNotBeNull();

            // Assert
            response.Type.ShouldBe((ProtocolMessageType)DocumentProtocolMessageType.Failure);
            var failure = DocumentProtocolFailureMessage.Decode(response.Payload.Span);
            failure.Code.ShouldBe(ProtocolErrorCode.ExecutionFailure);
            var diagnostic = failure.Diagnostics.ShouldHaveSingleItem();
            diagnostic.Code.ShouldBe("COHDOC001");
            diagnostic.Start.ShouldBeNull();
            diagnostic.End.ShouldBeNull();
            await WriteAsync(channel, (byte)DocumentProtocolMessageType.Command,
                new DocumentProtocolCommandMessage(DocumentProtocolOperation.ListCollections).Encode(), harness.Token);
            var complete = (await channel.Reader.ReadFrameAsync(harness.Token)).ShouldNotBeNull();
            complete.Type.ShouldBe((ProtocolMessageType)DocumentProtocolMessageType.Complete);
            DocumentProtocolCompleteMessage.Decode(complete.Payload.Span).ResultCount.ShouldBe(0);
            harness.Server.Sessions.ShouldHaveSingleItem().Id.ShouldBe(sessionId);
        }
    }

    [Theory(DisplayName = "Cohesion Test [Documents.Client] - Malformed UTF-8 receives a fatal error and creates nothing on both transports")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Server_MalformedUtf8_ShouldDeliverFatalErrorAndCreateNothing(bool tcp)
    {
        // Arrange
        await using var harness = await DocumentClientTestHarness.StartAsync(tcp);
        await using (var transport = await harness.Factory.ConnectAsync(harness.Listener.EndPoint, harness.Token))
        await using (var channel = new ProtocolChannel(transport.AsStream(), DocumentProtocol.Family, leaveOpen: true))
        {
            await WriteAsync(channel, (byte)ProtocolMessageType.Startup, new ProtocolStartupMessage(ProtocolVersion.Current, "documents", "tester").Encode(), harness.Token);
            (await channel.Reader.ReadFrameAsync(harness.Token)).ShouldNotBeNull().Type.ShouldBe(ProtocolMessageType.Authenticate);
            await WriteAsync(channel, (byte)ProtocolMessageType.AuthenticateResponse, [], harness.Token);
            (await channel.Reader.ReadFrameAsync(harness.Token)).ShouldNotBeNull().Type.ShouldBe(ProtocolMessageType.Ready);

            // Act: the reviewer's exact CreateCollection payload.
            await WriteAsync(channel, (byte)DocumentProtocolMessageType.Command, [4, 0, 0, 0, 1, 0xFF], harness.Token);
            var error = (await channel.Reader.ReadFrameAsync(harness.Token)).ShouldNotBeNull();

            // Assert
            error.Type.ShouldBe(ProtocolMessageType.Error);
            ProtocolErrorMessage.Decode(error.Payload.Span).Code.ShouldBe(ProtocolErrorCode.ProtocolViolation);
            (await channel.Reader.ReadFrameAsync(harness.Token)).ShouldBeNull();
        }
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        (await connection.GetCollectionsAsync(harness.Token)).ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [Documents.Client] - Malformed responses invalidate the pooled session")]
    [InlineData("row-before-header")]
    [InlineData("row-width")]
    [InlineData("count")]
    [InlineData("query-affected-count")]
    [InlineData("duplicate-header")]
    [InlineData("invalid-json")]
    public async Task QueryAsync_MalformedResponse_ShouldDiscardConnection(string scenario)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var token = timeout.Token;
        await using var listener = new InMemoryConnectionListener();
        await listener.BindAsync(token);
        await using var client = DocumentClient.Create(new()
        {
            Settings = new DatabaseConnectionSettings { Database = "documents", EndPoint = listener.EndPoint },
            ConnectionFactory = listener.CreateFactory(),
        });
        Task peer = RespondAsync();
        await using var connection = await client.ConnectAsync(token);

        // Act
        var failure = await Should.ThrowAsync<DocumentClientException>(async () => await connection.QueryAsync("SELECT n FROM items", cancellationToken: token));

        // Assert
        failure.Code.ShouldBe(ProtocolErrorCode.ProtocolViolation);
        connection.IsOpen.ShouldBeFalse();
        await connection.DisposeAsync();
        await peer;

        async Task RespondAsync()
        {
            await using var transport = await listener.AcceptAsync(token);
            await using var channel = new ProtocolChannel(transport.AsStream(), DocumentProtocol.Family, leaveOpen: true);
            (await channel.Reader.ReadFrameAsync(token)).ShouldNotBeNull().Type.ShouldBe(ProtocolMessageType.Startup);
            await WriteAsync(channel, (byte)ProtocolMessageType.Authenticate, [], token);
            (await channel.Reader.ReadFrameAsync(token)).ShouldNotBeNull().Type.ShouldBe(ProtocolMessageType.AuthenticateResponse);
            await WriteAsync(channel, (byte)ProtocolMessageType.Ready, [], token);
            (await channel.Reader.ReadFrameAsync(token)).ShouldNotBeNull().Type.ShouldBe((ProtocolMessageType)DocumentProtocolMessageType.Execute);
            byte[] header = new DocumentProtocolResultHeaderMessage([("n", (byte)DatabaseType.Decimal)], false).Encode();
            if (scenario != "row-before-header") { await WriteAsync(channel, 67, header, token); }
            switch (scenario)
            {
                case "row-before-header": await WriteAsync(channel, 68, "[1]"u8.ToArray(), token); break;
                case "row-width": await WriteAsync(channel, 68, "[1,2]"u8.ToArray(), token); break;
                case "count": await WriteAsync(channel, 66, new DocumentProtocolCompleteMessage(1, -1).Encode(), token); break;
                case "query-affected-count": await WriteAsync(channel, 66, new DocumentProtocolCompleteMessage(0, 1).Encode(), token); break;
                case "duplicate-header": await WriteAsync(channel, 67, header, token); break;
                case "invalid-json": await WriteAsync(channel, 68, "{"u8.ToArray(), token); break;
            }
            // Keep the peer alive until the client has discarded it.
            try { (await channel.Reader.ReadFrameAsync(token)).ShouldBeNull(); }
            catch (ConnectionAbortedException) { }
        }
    }

    [Theory(DisplayName = "Cohesion Test [Documents.Client] - Invalid requests close the server session")]
    [InlineData(64)]
    [InlineData(69)]
    [InlineData(66)]
    public async Task Server_MalformedRequest_ShouldSendFatalError(int type)
    {
        // Arrange
        await using var harness = await DocumentClientTestHarness.StartAsync();
        await using var transport = await harness.Factory.ConnectAsync(harness.Listener.EndPoint, harness.Token);
        await using var channel = new ProtocolChannel(transport.AsStream(), DocumentProtocol.Family, leaveOpen: true);
        await WriteAsync(channel, (byte)ProtocolMessageType.Startup, new ProtocolStartupMessage(ProtocolVersion.Current, "documents", "tester").Encode(), harness.Token);
        (await channel.Reader.ReadFrameAsync(harness.Token)).ShouldNotBeNull().Type.ShouldBe(ProtocolMessageType.Authenticate);
        await WriteAsync(channel, (byte)ProtocolMessageType.AuthenticateResponse, [], harness.Token);
        (await channel.Reader.ReadFrameAsync(harness.Token)).ShouldNotBeNull().Type.ShouldBe(ProtocolMessageType.Ready);

        // Act
        await WriteAsync(channel, (byte)type, [], harness.Token);
        var error = (await channel.Reader.ReadFrameAsync(harness.Token)).ShouldNotBeNull();

        // Assert
        error.Type.ShouldBe(ProtocolMessageType.Error);
        ProtocolErrorMessage.Decode(error.Payload.Span).Code.ShouldBe(ProtocolErrorCode.ProtocolViolation);
        (await channel.Reader.ReadFrameAsync(harness.Token)).ShouldBeNull();
    }

    private static async Task WriteAsync(ProtocolChannel channel, byte type, byte[] payload, CancellationToken token)
    {
        await channel.Writer.WriteFrameAsync(new((ProtocolMessageType)type, payload), token);
        await channel.Writer.FlushAsync(token);
    }
}
