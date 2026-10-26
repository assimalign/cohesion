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
    [Theory(DisplayName = "Cohesion Test [Documents] - Transaction Ready status is negotiated and legacy peers retain terminal framing")]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Server_TextTransactionControl_ShouldReportNegotiatedState(bool tcp, bool legacy)
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
        await WriteAsync(ProtocolMessageType.Startup, new ProtocolStartupMessage(legacy ? new ProtocolVersion(2, 0) : ProtocolVersion.Current, "documents", "tester")
            { Capabilities = legacy ? [] : [ProtocolCapability.TransactionStatus.Name] }.Encode());
        (await channel.Reader.ReadFrameAsync(token)).ShouldNotBeNull().Type.ShouldBe(ProtocolMessageType.Authenticate);
        await WriteAsync(ProtocolMessageType.AuthenticateResponse, []);
        var ready = (await channel.Reader.ReadFrameAsync(token)).ShouldNotBeNull();
        ready.Type.ShouldBe(ProtocolMessageType.Ready);
        var agreement = ProtocolReadyMessage.Decode(ready.Payload.Span);
        agreement.Capabilities.ShouldBe(legacy ? [] : new[] { ProtocolCapability.TransactionStatus.Name });
        var session = server.Sessions.ShouldHaveSingleItem();

        // Act / Assert: commands and failures share the same negotiated status contract.
        await ExecuteAsync("CREATE COLLECTION items", false, 'I');
        await ExecuteAsync("BEGIN", false, 'T', duplicateParameters: true);
        session.DatabaseSession.ShouldNotBeNull().CurrentTransaction.ShouldNotBeNull().IsolationLevel
            .ShouldBe(Transactions.IsolationLevel.Snapshot);
        await WriteAsync((ProtocolMessageType)DocumentProtocolMessageType.Command,
            new DocumentProtocolCommandMessage(DocumentProtocolOperation.ListCollections, "", "", null, default).Encode());
        (await channel.Reader.ReadFrameAsync(token)).ShouldNotBeNull().Type.ShouldBe((ProtocolMessageType)DocumentProtocolMessageType.Collection);
        (await channel.Reader.ReadFrameAsync(token)).ShouldNotBeNull().Type.ShouldBe((ProtocolMessageType)DocumentProtocolMessageType.Complete);
        await StatusAsync('T');
        await ExecuteAsync("SELECT * FROM items", true, 'E', duplicateParameters: true, code: "CD01009");
        await ExecuteAsync("SELECT * FROM absent", true, 'E', code: "CD04101");
        await ExecuteAsync("ROLLBACK", false, 'I', duplicateParameters: true);
        await ExecuteAsync("START WORK ISOLATION LEVEL READ COMMITTED", false, 'T');
        await ExecuteAsync("COMMIT TRAN", false, 'I', duplicateParameters: true);
        foreach (var operation in Enum.GetValues<DocumentProtocolOperation>().Where(item => item != DocumentProtocolOperation.ListCollections))
        {
            await ExecuteAsync("BEGIN", false, 'T');
            await ExecuteAsync("INSERT INTO items (KEY, VALUE) VALUES ('pending', {})", false, 'T');
            await InvalidCommandAsync(operation, "CD05001");
            session.DatabaseSession!.CurrentTransaction.ShouldNotBeNull().State.ShouldBe(Transactions.TransactionState.Faulted);
            await InvalidCommandAsync(operation, "CD04101");
            await ExecuteAsync("COMMIT", true, 'I', code: "CD04101");
            await using var observer = await ((DocumentDatabaseSession)session.DatabaseSession!).Database.CreateSessionAsync(token);
            var items = await observer.GetCollectionAsync("items", token);
            (await items.GetAsync(observer, "pending", token)).ShouldBeNull();
        }
        await WriteAsync(ProtocolMessageType.Ping, []);
        (await channel.Reader.ReadFrameAsync(token)).ShouldNotBeNull().Type.ShouldBe(ProtocolMessageType.Pong);
        server.Sessions.ShouldHaveSingleItem().Id.ShouldBe(session.Id);

        async Task ExecuteAsync(string source, bool failure, char status, bool duplicateParameters = false, string? code = null)
        {
            await WriteAsync((ProtocolMessageType)DocumentProtocolMessageType.Execute,
                new DocumentProtocolExecuteMessage(source, duplicateParameters ? "{\"p\":1,\"p\":2}"u8.ToArray() : "{}"u8.ToArray()).Encode());
            var response = (await channel.Reader.ReadFrameAsync(token)).ShouldNotBeNull();
            response.Type.ShouldBe((ProtocolMessageType)(failure ? DocumentProtocolMessageType.Failure : DocumentProtocolMessageType.Complete));
            if (code is not null) { DocumentProtocolFailureMessage.Decode(response.Payload.Span).Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(code); }
            await StatusAsync(status);
        }

        async Task StatusAsync(char status)
        {
            if (legacy) { return; }
            var response = (await channel.Reader.ReadFrameAsync(token)).ShouldNotBeNull();
            response.Type.ShouldBe(ProtocolMessageType.Ready);
            response.Payload.ToArray().ShouldBe([(byte)status]);
        }

        async Task InvalidCommandAsync(DocumentProtocolOperation operation, string code)
        {
            // The codec leaves blank collection names to semantic server validation.
            var payload = new System.Collections.Generic.List<byte> { (byte)operation };
            ProtocolPayload.WriteString(payload, " ");
            if (operation is DocumentProtocolOperation.Get or DocumentProtocolOperation.Put or DocumentProtocolOperation.Delete)
            { ProtocolPayload.WriteString(payload, "pending"); }
            if (operation is DocumentProtocolOperation.Put or DocumentProtocolOperation.Delete) { payload.Add(0); }
            if (operation == DocumentProtocolOperation.Put)
            {
                ProtocolPayload.WriteInt32(payload, 2);
                payload.AddRange("{}"u8.ToArray());
            }
            await WriteAsync((ProtocolMessageType)DocumentProtocolMessageType.Command, payload.ToArray());
            var response = (await channel.Reader.ReadFrameAsync(token)).ShouldNotBeNull();
            response.Type.ShouldBe((ProtocolMessageType)DocumentProtocolMessageType.Failure);
            DocumentProtocolFailureMessage.Decode(response.Payload.Span).Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(code);
            await StatusAsync('E');
        }

        async Task WriteAsync(ProtocolMessageType type, byte[] payload)
        {
            await channel.Writer.WriteFrameAsync(new(type, payload), token);
            await channel.Writer.FlushAsync(token);
        }
    }
}
