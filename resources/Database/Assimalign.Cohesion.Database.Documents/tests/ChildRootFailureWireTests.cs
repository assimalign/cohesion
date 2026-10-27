using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Connections.InMemory;
using Assimalign.Cohesion.Database.Documents.Protocol;
using Assimalign.Cohesion.Database.Indexing;
using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Storage;

namespace Assimalign.Cohesion.Database.Documents.Tests;

public sealed class ChildRootFailureWireTests
{
    [Fact(DisplayName = "Cohesion Test [Database.Documents] - Boundary: damaged non-root index pages fail embedded and wire queries without closing the session")]
    public async Task Execute_DamagedIndexChild_ShouldCodeCorruptionAndKeepSession()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var token = timeout.Token;
        await using var listener = new InMemoryConnectionListener();
        await using var engine = await DocumentDatabaseEngine.CreateBuilder("index-damage").AddDatabase("test")
            .AddServer(options => options.Listener = listener).BuildAsync(token);
        var database = await engine.OpenDatabaseAsync("test", token);
        await using var session = await database.CreateSessionAsync(token);
        await session.CreateCollectionAsync("items", token);
        string values = string.Join(',', Enumerable.Range(0, 2000).Select(i => $"('{i}', {{'score':{i}}})"));
        await session.ExecuteAsync("INSERT INTO items (KEY, VALUE) VALUES " + values, cancellationToken: token);
        await session.ExecuteAsync("CREATE INDEX by_score ON items (score)", cancellationToken: token);
        long root;
        using (var iterator = database.DataStorage.GetUnitIterator(1))
        {
            iterator.MoveNext().ShouldBeTrue();
            root = BinaryPrimitives.ReadInt64LittleEndian(iterator.Current.Data.Span[26..]);
        }
        long child;
        using (var page = database.DataStorage.PageManager.GetPage(root))
        {
            page.Page.AsBodySpan()[3].ShouldBe((byte)2);
            child = BinaryPrimitives.ReadInt64LittleEndian(page.Page.AsBodySpan()[24..]);
        }
        child.ShouldNotBe(root);
        using (var bracket = database.DataStorage.BeginTransaction())
        {
            using (var page = database.DataStorage.OpenPageForWrite(bracket, child))
            {
                page.Page.AsBodySpan()[3] = 9;
                page.MarkDirty();
            }
            bracket.Commit();
        }
        var server = engine.Servers.Single();
        await server.StartAsync(token);
        await using var transport = await listener.CreateFactory().ConnectAsync(listener.EndPoint, token);
        await using var channel = new ProtocolChannel(transport.AsStream(), DocumentProtocol.Family, leaveOpen: true);
        await HandshakeAsync(channel, token);
        Guid sessionId = server.Sessions.ShouldHaveSingleItem().Id;
        const string query = "SELECT d.score FROM items d WHERE d.score = 1";

        // Act
        var embedded = await Should.ThrowAsync<DatabaseCorruptionException>(() => session.ExecuteAsync(query, cancellationToken: token).AsTask());
        await SendAsync(channel, (ProtocolMessageType)DocumentProtocolMessageType.Execute,
            new DocumentProtocolExecuteMessage(query, "{}"u8.ToArray()).Encode(), token);
        var frame = (await channel.Reader.ReadFrameAsync(token)).ShouldNotBeNull();

        // Assert
        embedded.PageId.ShouldBe((PageId)child);
        embedded.InnerException.ShouldBeOfType<IndexCorruptionException>().PageId.ShouldBe(child);
        embedded.Diagnostics.Select(d => d.Code).ShouldBe(["CK06003", "CK06102", "CD06105"]);
        frame.Type.ShouldBe((ProtocolMessageType)DocumentProtocolMessageType.Failure);
        var failure = DocumentProtocolFailureMessage.Decode(frame.Payload.Span);
        failure.Code.ShouldBe(ProtocolErrorCode.ExecutionFailure);
        failure.Diagnostics.Select(d => d.Code).ShouldBe(embedded.Diagnostics.Select(d => d.Code));
        failure.Message.ShouldStartWith("CK06003: ", Case.Sensitive);
        await SendAsync(channel, (ProtocolMessageType)DocumentProtocolMessageType.Execute,
            new DocumentProtocolExecuteMessage("CREATE COLLECTION reusable", "{}"u8.ToArray()).Encode(), token);
        (await channel.Reader.ReadFrameAsync(token)).ShouldNotBeNull().Type.ShouldBe((ProtocolMessageType)DocumentProtocolMessageType.Complete);
        server.Sessions.ShouldHaveSingleItem().Id.ShouldBe(sessionId);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Documents] - Begin: storage failures are coded in process and on the wire")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BeginTransaction_StorageFailure_ShouldCodeAndKeepWireSession(bool rawIo)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        await using var listener = new InMemoryConnectionListener();
        await using var engine = await DocumentDatabaseEngine.CreateBuilder("begin-failure").AddDatabase("test")
            .AddServer(options => options.Listener = listener).BuildAsync(token);
        var database = await engine.OpenDatabaseAsync("test", token);
        await using var session = await database.CreateSessionAsync(token);
        var server = engine.Servers.Single();
        await server.StartAsync(token);
        await using var transport = await listener.CreateFactory().ConnectAsync(listener.EndPoint, token);
        await using var channel = new ProtocolChannel(transport.AsStream(), DocumentProtocol.Family, leaveOpen: true);
        await HandshakeAsync(channel, token);
        int attempts = 0;
        Exception cause = rawIo ? new IOException("Injected begin device failure.") : new StorageIOException("Injected begin storage failure.");
        database.BeforeTransactionBegin = () => { Interlocked.Increment(ref attempts); throw cause; };

        // Act
        var embedded = await Should.ThrowAsync<DatabaseException>(() => session.BeginTransactionAsync(cancellationToken: token).AsTask());
        await SendAsync(channel, (ProtocolMessageType)DocumentProtocolMessageType.Command,
            new DocumentProtocolCommandMessage(DocumentProtocolOperation.CreateCollection, "failed").Encode(), token);
        var failure = DocumentProtocolFailureMessage.Decode((await channel.Reader.ReadFrameAsync(token)).ShouldNotBeNull().Payload.Span);
        database.BeforeTransactionBegin = null;

        // Assert
        attempts.ShouldBe(2);
        embedded.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD06104");
        embedded.InnerException.ShouldBeSameAs(cause);
        failure.Code.ShouldBe(ProtocolErrorCode.ExecutionFailure);
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CD06104");
        await SendAsync(channel, (ProtocolMessageType)DocumentProtocolMessageType.Execute,
            new DocumentProtocolExecuteMessage("CREATE COLLECTION after", "{}"u8.ToArray()).Encode(), token);
        (await channel.Reader.ReadFrameAsync(token)).ShouldNotBeNull().Type.ShouldBe((ProtocolMessageType)DocumentProtocolMessageType.Complete);
        (await session.GetCollectionAsync("after", token)).Name.ShouldBe("after");
    }

    private static async Task HandshakeAsync(ProtocolChannel channel, CancellationToken token)
    {
        await SendAsync(channel, ProtocolMessageType.Startup, new ProtocolStartupMessage(ProtocolVersion.Current, "test", "tester").Encode(), token);
        (await channel.Reader.ReadFrameAsync(token)).ShouldNotBeNull().Type.ShouldBe(ProtocolMessageType.Authenticate);
        await SendAsync(channel, ProtocolMessageType.AuthenticateResponse, [], token);
        (await channel.Reader.ReadFrameAsync(token)).ShouldNotBeNull().Type.ShouldBe(ProtocolMessageType.Ready);
    }

    private static async Task SendAsync(ProtocolChannel channel, ProtocolMessageType type, byte[] payload, CancellationToken token)
    {
        await channel.Writer.WriteFrameAsync(new(type, payload), token);
        await channel.Writer.FlushAsync(token);
    }
}
