using System;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Connections.InMemory;
using Assimalign.Cohesion.Database.Documents.Internal;
using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Transactions;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Documents.Protocol;

namespace Assimalign.Cohesion.Database.Documents.Tests;

public sealed class DocumentProtocolTests
{
    [Fact(DisplayName = "Cohesion Test [Documents] - INSERT: completion counts writes and emits no Document frames")]
    public async Task Execute_Insert_ShouldCompleteWithAffectedCountAndNoDocumentFramesAsync()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        await using var listener = new InMemoryConnectionListener();
        await using var engine = DocumentDatabaseEngine.Create("insert-wire", new());
        var database = await engine.CreateDatabaseAsync("documents", token);
        await using var setup = await database.CreateSessionAsync(token);
        var collection = await setup.CreateCollectionAsync("items", token);
        await using var server = DocumentDatabaseServer.Create(engine, new() { Listener = listener });
        await server.StartAsync(token);
        await using var peer = await listener.CreateFactory().ConnectAsync(listener.EndPoint, token);
        await using var channel = new ProtocolChannel(peer.AsStream(), DocumentProtocol.Family, leaveOpen: true);
        await WriteAsync(channel, ProtocolMessageType.Startup,
            new ProtocolStartupMessage(ProtocolVersion.Current, "documents", "tester").Encode(), token);
        (await ReadAsync(channel, token)).Type.ShouldBe(ProtocolMessageType.Authenticate);
        await WriteAsync(channel, ProtocolMessageType.AuthenticateResponse, [], token);
        (await ReadAsync(channel, token)).Type.ShouldBe(ProtocolMessageType.Ready);
        // Act
        await WriteAsync(channel, (ProtocolMessageType)DocumentProtocolMessageType.Execute,
            DocumentProtocolExecuteMessage.Create("INSERT INTO items (KEY, VALUE) VALUES ('e', {'name':'epsilon'}), ('f', {'name':'phi'})").Encode(), token);
        var frame = await ReadAsync(channel, token);
        // Assert: Complete is the first and only frame for a successful write.
        frame.Type.ShouldBe((ProtocolMessageType)DocumentProtocolMessageType.Complete);
        var complete = DocumentProtocolCompleteMessage.Decode(frame.Payload.Span);
        complete.ResultCount.ShouldBe(0);
        complete.AffectedCount.ShouldBe(2);
        (await collection.GetAsync(setup, "e", token)).ShouldNotBeNull();
        (await collection.GetAsync(setup, "f", token)).ShouldNotBeNull();
        await WriteAsync(channel, (ProtocolMessageType)DocumentProtocolMessageType.Execute,
            DocumentProtocolExecuteMessage.Create("INSERT INTO items (KEY, VALUE) VALUES ('e', 1)").Encode(), token);
        frame = await ReadAsync(channel, token);
        frame.Type.ShouldBe((ProtocolMessageType)DocumentProtocolMessageType.Failure);
        DocumentProtocolFailureMessage.Decode(frame.Payload.Span).Code.ShouldBe(ProtocolErrorCode.ExecutionFailure);
        await WriteAsync(channel, (ProtocolMessageType)DocumentProtocolMessageType.Execute,
            DocumentProtocolExecuteMessage.Create("INSERT INTO items (KEY, VALUE) VALUES ('g', NULL)").Encode(), token);
        frame = await ReadAsync(channel, token);
        frame.Type.ShouldBe((ProtocolMessageType)DocumentProtocolMessageType.Complete);
        DocumentProtocolCompleteMessage.Decode(frame.Payload.Span).AffectedCount.ShouldBe(1);
        await WriteAsync(channel, ProtocolMessageType.Terminate, [], token);
        await server.StopAsync(token);
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Collection DDL: locked drops return ExecutionFailure and preserve the wire session")]
    [InlineData("DROP COLLECTION owned")]
    [InlineData("DROP COLLECTION IF EXISTS owned")]
    public async Task Execute_SchemaOwnedDrop_ShouldReturnFailureAndKeepConnectionUsableAsync(string statement)
    {
        // Arrange: ownership setup uses this assembly's existing test grant; the real server
        // and public wire codecs exercise failure classification without a new boundary.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        await using var listener = new InMemoryConnectionListener();
        await using var engine = DocumentDatabaseEngine.Create("collection-wire", new());
        var database = await engine.CreateDatabaseAsync("documents", token);
        await using (var setup = await database.CreateSessionAsync(token))
        {
            await setup.CreateCollectionAsync("owned", token);
        }
        var context = await database.Coordinator.BeginAsync(IsolationLevel.Snapshot, token);
        var metadata = database.Catalog.FindCollection("owned", context.Snapshot).ShouldNotBeNull();
        await database.Catalog.SaveCollectionAsync(metadata with { Owner = DatabaseObjectOwner.Schema, OwningSchema = "Sales" }, context, token);
        await database.Coordinator.CommitAsync(context, token);
        await using var server = DocumentDatabaseServer.Create(engine, new() { Listener = listener });
        await server.StartAsync(token);
        await using var peer = await listener.CreateFactory().ConnectAsync(listener.EndPoint, token);
        await using var channel = new ProtocolChannel(peer.AsStream(), DocumentProtocol.Family, leaveOpen: true);
        await WriteAsync(channel, ProtocolMessageType.Startup,
            new ProtocolStartupMessage(ProtocolVersion.Current, "documents", "tester").Encode(), token);
        (await ReadAsync(channel, token)).Type.ShouldBe(ProtocolMessageType.Authenticate);
        await WriteAsync(channel, ProtocolMessageType.AuthenticateResponse, [], token);
        (await ReadAsync(channel, token)).Type.ShouldBe(ProtocolMessageType.Ready);

        // Act
        await WriteAsync(channel, (ProtocolMessageType)DocumentProtocolMessageType.Execute,
            DocumentProtocolExecuteMessage.Create(statement).Encode(), token);
        var frame = await ReadAsync(channel, token);

        // Assert: the next Execute on this same authenticated connection completes.
        frame.Type.ShouldBe((ProtocolMessageType)DocumentProtocolMessageType.Failure);
        var failure = DocumentProtocolFailureMessage.Decode(frame.Payload.Span);
        failure.Code.ShouldBe(ProtocolErrorCode.ExecutionFailure);
        ((int)failure.Code).ShouldBe(6);
        failure.Message.ShouldContain("DROP COLLECTION", Case.Sensitive);
        await WriteAsync(channel, (ProtocolMessageType)DocumentProtocolMessageType.Execute,
            DocumentProtocolExecuteMessage.Create("CREATE COLLECTION IF NOT EXISTS owned").Encode(), token);
        var complete = await ReadAsync(channel, token);
        complete.Type.ShouldBe((ProtocolMessageType)DocumentProtocolMessageType.Complete);
        DocumentProtocolCompleteMessage.Decode(complete.Payload.Span).AffectedCount.ShouldBe(0);
        await WriteAsync(channel, ProtocolMessageType.Terminate, [], token);
        await server.StopAsync(token);
    }

    [Fact(DisplayName = "Cohesion Test [Database.Documents] - Protocol: nested query results cross an in-memory connection")]
    public async Task Execute_NestedDocuments_ShouldPreserveShapeOverInMemory()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        await using var engine = DocumentDatabaseEngine.Create("document-engine", new());
        var database = await engine.CreateDatabaseAsync("documents", token);
        await using var session = await database.CreateSessionAsync(token);
        var collection = await session.CreateCollectionAsync("items", cancellationToken: token);
        byte[] content = "{\"profile\":{\"city\":\"東京\"},\"items\":[1,null,{\"flags\":[true,false]}]}"u8.ToArray();
        await collection.PutAsync(session, "one", content, cancellationToken: token);
        var pair = InMemoryConnectionPair.Create();
        await using var clientConnection = pair.Client;
        await using var serverConnection = pair.Server;
        await using var client = new ProtocolChannel(clientConnection.AsStream(), DocumentProtocol.Family, leaveOpen: true);
        await using var server = new ProtocolChannel(serverConnection.AsStream(), DocumentProtocol.Family, leaveOpen: true);

        Task serverTask = RespondAsync();
        await WriteAsync(client, ProtocolMessageType.Startup, new ProtocolStartupMessage(ProtocolVersion.Current, "documents", "test").Encode(), token);
        (await ReadAsync(client, token)).Type.ShouldBe(ProtocolMessageType.Authenticate);
        await WriteAsync(client, ProtocolMessageType.AuthenticateResponse, [], token);
        (await ReadAsync(client, token)).Type.ShouldBe(ProtocolMessageType.Ready);
        await WriteAsync(client, (ProtocolMessageType)DocumentProtocolMessageType.Execute,
            DocumentProtocolExecuteMessage.Create("SELECT * FROM items").Encode(), token);

        var frame = await ReadAsync(client, token);
        frame.Type.ShouldBe((ProtocolMessageType)DocumentProtocolMessageType.Document);
        var result = DocumentProtocolResultMessage.Decode(frame.Payload.Span);
        result.Json.ToArray().ShouldBe(content);
        using var json = JsonDocument.Parse(result.Json);
        json.RootElement.GetProperty("profile").GetProperty("city").GetString().ShouldBe("東京");
        json.RootElement.GetProperty("items")[2].GetProperty("flags")[1].GetBoolean().ShouldBeFalse();
        var complete = await ReadAsync(client, token);
        complete.Type.ShouldBe((ProtocolMessageType)DocumentProtocolMessageType.Complete);
        DocumentProtocolCompleteMessage.Decode(complete.Payload.Span).ResultCount.ShouldBe(1);
        await WriteAsync(client, ProtocolMessageType.Terminate, [], token);
        await serverTask;

        async Task RespondAsync()
        {
            var startup = await ReadAsync(server, token);
            startup.Type.ShouldBe(ProtocolMessageType.Startup);
            ProtocolStartupMessage.Decode(startup.Payload.Span).Database.ShouldBe("documents");
            await WriteAsync(server, ProtocolMessageType.Authenticate, [], token);
            (await ReadAsync(server, token)).Type.ShouldBe(ProtocolMessageType.AuthenticateResponse);
            await WriteAsync(server, ProtocolMessageType.Ready, [], token);
            var request = await ReadAsync(server, token);
            request.Type.ShouldBe((ProtocolMessageType)DocumentProtocolMessageType.Execute);
            var message = DocumentProtocolExecuteMessage.Decode(request.Payload.Span);
            Encoding.UTF8.GetString(message.Parameters.Span).ShouldBe("{}");
            await using var results = (await session.ExecuteAsync(DocumentQueryRequest.FromOql(message.Statement), token)).ShouldBeOfType<DocumentQueryResult>();
            using var writer = new DocumentRowWriter();
            long count = 0;
            await foreach (var item in results.GetRowsAsync(token))
            {
                await WriteAsync(server, (ProtocolMessageType)DocumentProtocolMessageType.Document,
                    new DocumentProtocolResultMessage(writer.Write(item, results.Columns, results.BareValue)).Encode(), token);
                count++;
            }
            await WriteAsync(server, (ProtocolMessageType)DocumentProtocolMessageType.Complete,
                new DocumentProtocolCompleteMessage(count, -1).Encode(), token);
            (await ReadAsync(server, token)).Type.ShouldBe(ProtocolMessageType.Terminate);
        }
    }

    [Theory(DisplayName = "Cohesion Test [Database.Documents] - Protocol: scalar and array roots retain their JSON shape")]
    [InlineData("[1,{\"nested\":[null,true]}]")]
    [InlineData("null")]
    [InlineData("42")]
    [InlineData("\"value\"")]
    public void Decode_JsonRoot_ShouldPreserveBytes(string json)
        => DocumentProtocolResultMessage.Decode(Encoding.UTF8.GetBytes(json)).Encode().ShouldBe(Encoding.UTF8.GetBytes(json));

    [Theory(DisplayName = "Cohesion Test [Database.Documents] - Protocol: malformed JSON is a protocol violation")]
    [InlineData("")]
    [InlineData("{} []")]
    [InlineData("{\"a\":}")]
    [InlineData("[1,]")]
    public void Decode_InvalidJson_ShouldReject(string json)
        => Should.Throw<ProtocolException>(() => DocumentProtocolResultMessage.Decode(Encoding.UTF8.GetBytes(json)));

    [Fact(DisplayName = "Cohesion Test [Database.Documents] - Protocol: request and completion bounds are enforced")]
    public void Decode_InvalidRequestOrCompletion_ShouldReject()
    {
        Should.Throw<ProtocolException>(() => DocumentProtocolResultMessage.Decode([34, 255, 34]));
        Should.Throw<ProtocolException>(() => new DocumentProtocolExecuteMessage("SELECT * FROM items", "[]"u8.ToArray()).Encode());
        Should.Throw<ProtocolException>(() => DocumentProtocolExecuteMessage.Decode([0, 0, 0, 0, 127, 255, 255, 255]));
        Should.Throw<ProtocolException>(() => DocumentProtocolCompleteMessage.Decode([255, 255, 255, 255, 255, 255, 255, 255]));
        Should.Throw<ProtocolException>(() => DocumentProtocolCompleteMessage.Decode(new byte[9]));
    }

    private static async Task WriteAsync(ProtocolChannel channel, ProtocolMessageType type, byte[] payload, CancellationToken token)
    {
        await channel.Writer.WriteFrameAsync(new(type, payload), token);
        await channel.Writer.FlushAsync(token);
    }

    private static async Task<ProtocolFrame> ReadAsync(ProtocolChannel channel, CancellationToken token)
        => await channel.Reader.ReadFrameAsync(token) ?? throw new ProtocolException("Unexpected end of document exchange.");
}
