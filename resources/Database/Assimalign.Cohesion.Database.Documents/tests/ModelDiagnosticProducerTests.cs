using System;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Connections.InMemory;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Types;
using Assimalign.Cohesion.Database.Documents.Internal;
using Assimalign.Cohesion.Database.Documents.Protocol;
using Assimalign.Cohesion.Database.Protocol;

namespace Assimalign.Cohesion.Database.Documents.Tests;

public sealed class ModelDiagnosticProducerTests
{
    [Fact(DisplayName = "Cohesion Test [Documents] - Diagnostics: overlapping operations retain their refusal and code")]
    public async Task BeginOperation_OverlappingOperation_ShouldCarryCodeAsync()
    {
        await using var engine = DocumentDatabaseEngine.Create("diagnostics", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var operation = await database.BeginOperationAsync(session, CancellationToken.None);
        try
        {
            var error = await Should.ThrowAsync<DatabaseException>(async () => await database.BeginOperationAsync(session, CancellationToken.None));
            Refusal(error, "CK07003", "Dispose the active document operation before starting another operation on this session.");
        }
        finally { await operation.CompleteAsync(); }
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - Diagnostics: a completed operation retains its refusal and code")]
    public async Task EnsureActive_CompletedOperation_ShouldCarryCodeAsync()
    {
        await using var engine = DocumentDatabaseEngine.Create("diagnostics", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var operation = await database.BeginOperationAsync(session, CancellationToken.None);
        await operation.CompleteAsync();
        Refusal(Should.Throw<DatabaseException>(operation.EnsureActive), "CK04002", "The document operation's transaction is no longer active.");
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - Diagnostics: unsupported isolation retains its refusal and code")]
    public async Task BeginTransaction_UnsupportedIsolation_ShouldCarryCodeAsync()
    {
        await using var engine = DocumentDatabaseEngine.Create("diagnostics", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.BeginTransactionAsync(IsolationLevel.Serializable, CancellationToken.None));
        Refusal(error, "CK04001", "The document engine supports Snapshot and ReadCommitted isolation.");
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - Diagnostics: a missing collection uses the existing collection identity")]
    public async Task GetCollection_MissingCollection_ShouldCarryCodeAsync()
    {
        await using var engine = DocumentDatabaseEngine.Create("diagnostics", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        Refusal(await Should.ThrowAsync<DatabaseException>(async () => await session.GetCollectionAsync("missing", CancellationToken.None)),
            "CD01002", "Collection 'missing' does not exist.");
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - Diagnostics: a catalog conflict keeps its retryable exception type")]
    public async Task Put_ReplacedCollectionInSnapshot_ShouldCarryConflictCodeAsync()
    {
        await using var engine = DocumentDatabaseEngine.Create("diagnostics", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var setup = await database.CreateSessionAsync(CancellationToken.None);
        await setup.CreateCollectionAsync("items", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.BeginTransactionAsync(IsolationLevel.Snapshot, CancellationToken.None);
        var collection = await session.GetCollectionAsync("items", CancellationToken.None);
        await setup.DropCollectionAsync("items", CancellationToken.None);
        await setup.CreateCollectionAsync("items", CancellationToken.None);
        var error = await Should.ThrowAsync<DatabaseTransactionAbortedException>(async () =>
            await collection.PutAsync(session, "one", "{}"u8.ToArray(), cancellationToken: CancellationToken.None));
        error.ShouldBeOfType<DatabaseTransactionAbortedException>();
        Diagnostic(error, "CD04102", "The document catalog changed since this transaction's snapshot. Retry the transaction.");
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - Diagnostics: a conditional version mismatch retains its refusal")]
    public async Task Put_ExpectedVersionMismatch_ShouldCarryCodeAsync()
    {
        await using var engine = DocumentDatabaseEngine.Create("diagnostics", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var collection = await session.CreateCollectionAsync("items", CancellationToken.None);
        await collection.PutAsync(session, "one", "{}"u8.ToArray(), cancellationToken: CancellationToken.None);
        var error = await Should.ThrowAsync<DatabaseException>(async () =>
            await collection.PutAsync(session, "one", "{}"u8.ToArray(), new DocumentVersion(9), CancellationToken.None));
        Refusal(error, "CD04103", "Document 'one' does not match expected version '9'.");
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - Diagnostics: a stale collection handle retains its refusal")]
    public async Task Get_CollectionReplaced_ShouldCarryCodeAsync()
    {
        await using var engine = DocumentDatabaseEngine.Create("diagnostics", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var collection = await session.CreateCollectionAsync("items", CancellationToken.None);
        await session.DropCollectionAsync("items", CancellationToken.None);
        await session.CreateCollectionAsync("items", CancellationToken.None);
        Refusal(await Should.ThrowAsync<DatabaseException>(async () => await collection.GetAsync(session, "one", CancellationToken.None)),
            "CD09001", "Collection 'items' is no longer available in this transaction.");
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - Diagnostics: a collection refuses a foreign bound session")]
    public async Task Get_DifferentSession_ShouldCarryCodeAsync()
    {
        await using var engine = DocumentDatabaseEngine.Create("diagnostics", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var first = await database.CreateSessionAsync(CancellationToken.None);
        await using var second = await database.CreateSessionAsync(CancellationToken.None);
        var collection = await first.CreateCollectionAsync("items", CancellationToken.None);
        Refusal(await Should.ThrowAsync<DatabaseException>(async () => await collection.GetAsync(second, "one", CancellationToken.None)),
            "CD07001", "The document collection and session must belong to the same bound database and session.");
    }

    [Theory(DisplayName = "Cohesion Test [Documents] - Diagnostics: both result buffer limits share the result-size identity")]
    [InlineData(false)]
    [InlineData(true)]
    public void Write_ResultBufferLimit_ShouldCarryCode(bool scratchCapacity)
    {
        var buffer = new DocumentJsonBuffer();
        var error = Should.Throw<DatabaseException>(() =>
        {
            if (scratchCapacity) { _ = buffer.GetMemory(int.MaxValue); }
            else { buffer.Advance((int)ProtocolFrameHeader.MaxPayloadLength + 1); }
        });
        Refusal(error, "CD05016", "A document result exceeds the maximum frame size.");
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - Diagnostics: row type and null refusals carry shared identities")]
    public void Read_InvalidRowAccessor_ShouldCarryCode()
    {
        var row = new DocumentQueryResult.DocumentQueryRow([1, null], default);
        Refusal(Should.Throw<DatabaseException>(() => row.GetBytes(0)), "CK02001", "Field 0 is not JSON or a string.");
        Refusal(Should.Throw<DatabaseException>(() => row.GetInt64(1)), "CK02002", "Field 1 is null.");
    }

    [Fact(DisplayName = "Cohesion Test [Documents] - Diagnostics: duplicate wire parameter keys have one stable prefix")]
    public async Task Execute_DuplicateWireParameters_ShouldCarryCodeAsync()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30)); // Hang guard for the wire peer.
        var token = timeout.Token;
        await using var listener = new InMemoryConnectionListener();
        await using var engine = DocumentDatabaseEngine.Create("diagnostics", new());
        await engine.CreateDatabaseAsync("db", token);
        await using var server = DocumentDatabaseServer.Create(engine, new() { Listener = listener });
        await server.StartAsync(token);
        await using var peer = await listener.CreateFactory().ConnectAsync(listener.EndPoint, token);
        await using var channel = new ProtocolChannel(peer.AsStream(), DocumentProtocol.Family, leaveOpen: true);
        await WriteAsync(ProtocolMessageType.Startup, new ProtocolStartupMessage(ProtocolVersion.Current, "db", "tester").Encode());
        (await channel.Reader.ReadFrameAsync(token)).ShouldNotBeNull().Type.ShouldBe(ProtocolMessageType.Authenticate);
        await WriteAsync(ProtocolMessageType.AuthenticateResponse, []);
        (await channel.Reader.ReadFrameAsync(token)).ShouldNotBeNull().Type.ShouldBe(ProtocolMessageType.Ready);
        // Act: valid OQL isolates parameter validation after parsing identifies statement controls.
        await WriteAsync((ProtocolMessageType)DocumentProtocolMessageType.Execute,
            new DocumentProtocolExecuteMessage("SELECT 1 FROM items", "{\"x\":1,\"x\":2}"u8.ToArray()).Encode());

        // Assert
        var frame = (await channel.Reader.ReadFrameAsync(token)).ShouldNotBeNull();
        frame.Type.ShouldBe((ProtocolMessageType)DocumentProtocolMessageType.Failure);
        var failure = DocumentProtocolFailureMessage.Decode(frame.Payload.Span);
        failure.Code.ShouldBe(ProtocolErrorCode.ExecutionFailure);
        failure.Message.ShouldBe("CD01009: Duplicate document parameter name.");
        var diagnostic = failure.Diagnostics.ShouldHaveSingleItem();
        diagnostic.Code.ShouldBe("CD01009");
        diagnostic.Message.ShouldBe("Duplicate document parameter name.");
        diagnostic.Start.ShouldBeNull();
        diagnostic.End.ShouldBeNull();
        await WriteAsync(ProtocolMessageType.Terminate, []);

        async Task WriteAsync(ProtocolMessageType type, byte[] payload)
        {
            await channel.Writer.WriteFrameAsync(new(type, payload), token);
            await channel.Writer.FlushAsync(token);
        }
    }

    private static void Refusal(DatabaseException error, string code, string message)
    {
        error.ShouldBeOfType<DatabaseException>();
        Diagnostic(error, code, message);
    }

    private static void Diagnostic(DatabaseException error, string code, string message)
    {
        error.Message.ShouldBe(message);
        var diagnostic = error.Diagnostics.ShouldHaveSingleItem();
        diagnostic.Code.ShouldBe(code);
        diagnostic.Message.ShouldBe(message);
        diagnostic.Severity.ShouldBe(DiagnosticSeverity.Error);
        diagnostic.Start.ShouldBeNull();
        diagnostic.End.ShouldBeNull();
    }
}
