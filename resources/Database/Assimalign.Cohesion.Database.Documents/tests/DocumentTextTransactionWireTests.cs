using System;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Connections.InMemory;
using Assimalign.Cohesion.Connections.Tcp;
using Assimalign.Cohesion.Database.Documents.Protocol;
using Assimalign.Cohesion.Database.Protocol;

namespace Assimalign.Cohesion.Database.Documents.Tests;

public sealed class DocumentTextTransactionWireTests
{
    [Theory(DisplayName = "Cohesion Test [Documents] - Text transactions: wire control is refused until status and pooled reset are safe")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Server_TextTransactionControl_ShouldRefuseWithoutOpeningTransaction(bool tcp)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        var token = timeout.Token;
        IDuplexConnectionListener listener;
        IDuplexConnectionFactory factory;
        if (tcp)
        {
            listener = new TcpConnectionListener(new() { EndPoint = new IPEndPoint(IPAddress.Loopback, 0) });
            factory = new TcpConnectionFactory();
        }
        else
        {
            var memory = new InMemoryConnectionListener();
            listener = memory;
            factory = memory.CreateFactory();
        }
        await using var engine = await DocumentDatabaseEngine.CreateBuilder("text-wire").AddDatabase("documents")
            .AddServer(options => options.Listener = listener).BuildAsync(token);
        var server = engine.Servers.Single();
        await server.StartAsync(token);
        await using var transport = await factory.ConnectAsync(listener.EndPoint, token);
        await using var channel = new ProtocolChannel(transport.AsStream(), DocumentProtocol.Family, leaveOpen: true);
        await WriteAsync(ProtocolMessageType.Startup, new ProtocolStartupMessage(ProtocolVersion.Current, "documents", "tester").Encode());
        (await channel.Reader.ReadFrameAsync(token)).ShouldNotBeNull().Type.ShouldBe(ProtocolMessageType.Authenticate);
        await WriteAsync(ProtocolMessageType.AuthenticateResponse, []);
        (await channel.Reader.ReadFrameAsync(token)).ShouldNotBeNull().Type.ShouldBe(ProtocolMessageType.Ready);
        var session = server.Sessions.ShouldHaveSingleItem();

        // Act / Assert: every spelling is parsed, then refused by the server before dispatch.
        foreach (string source in new[] { "BEGIN", "BEGIN WORK", "BEGIN TRAN", "BEGIN TRANSACTION ISOLATION LEVEL SNAPSHOT",
            "START WORK", "START TRAN", "START TRANSACTION ISOLATION LEVEL READ COMMITTED",
            "COMMIT", "COMMIT WORK", "COMMIT TRAN", "COMMIT TRANSACTION",
            "ROLLBACK", "ROLLBACK WORK", "ROLLBACK TRAN", "ROLLBACK TRANSACTION" })
        {
            await WriteAsync((ProtocolMessageType)DocumentProtocolMessageType.Execute,
                new DocumentProtocolExecuteMessage(source, "{}"u8.ToArray()).Encode());
            var response = (await channel.Reader.ReadFrameAsync(token)).ShouldNotBeNull();
            response.Type.ShouldBe((ProtocolMessageType)DocumentProtocolMessageType.Failure);
            var failure = DocumentProtocolFailureMessage.Decode(response.Payload.Span);
            failure.Code.ShouldBe(ProtocolErrorCode.ExecutionFailure);
            var diagnostic = failure.Diagnostics.ShouldHaveSingleItem();
            diagnostic.Code.ShouldBe("CD07002");
            diagnostic.Start.ShouldBe(0);
            diagnostic.End.ShouldBe(source.Length);
            session.DatabaseSession.ShouldNotBeNull().CurrentTransaction.ShouldBeNull();
        }
        await WriteAsync((ProtocolMessageType)DocumentProtocolMessageType.Execute,
            new DocumentProtocolExecuteMessage("CREATE COLLECTION reusable", "{}"u8.ToArray()).Encode());
        (await channel.Reader.ReadFrameAsync(token)).ShouldNotBeNull().Type.ShouldBe((ProtocolMessageType)DocumentProtocolMessageType.Complete);
        server.Sessions.ShouldHaveSingleItem().Id.ShouldBe(session.Id);

        async Task WriteAsync(ProtocolMessageType type, byte[] payload)
        {
            await channel.Writer.WriteFrameAsync(new(type, payload), token);
            await channel.Writer.FlushAsync(token);
        }
    }
}
