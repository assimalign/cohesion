using System;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Connections.InMemory;
using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Protocol;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Documents.Tests;

public sealed class DocumentProtocolTests
{
    [Fact(DisplayName = "Cohesion Test [Database.Documents] - Protocol: nested query results cross an in-memory connection")]
    public async Task Execute_NestedDocuments_ShouldPreserveShapeOverInMemory()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        await using var engine = DocumentDatabaseEngine.Create(new());
        var database = (IDocumentDatabase)await engine.CreateDatabaseAsync("documents", token);
        var collection = await database.CreateCollectionAsync("items", cancellationToken: token);
        await using var session = await database.CreateSessionAsync(token);
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
            await using var results = (QueryResultSet)await session.ExecuteAsync(DocumentQueryRequest.FromOql(message.Statement), token);
            long count = 0;
            await foreach (var item in results.GetRowsAsync(token))
            {
                await WriteAsync(server, (ProtocolMessageType)DocumentProtocolMessageType.Document,
                    new DocumentProtocolResultMessage(item.GetBytes(0)).Encode(), token);
                count++;
            }
            await WriteAsync(server, (ProtocolMessageType)DocumentProtocolMessageType.Complete,
                new DocumentProtocolCompleteMessage(count).Encode(), token);
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
