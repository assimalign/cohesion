using System;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections.InMemory;
using Assimalign.Cohesion.Database.Blob.Protocol;
using Assimalign.Cohesion.Database.Protocol;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Types;
using Assimalign.Cohesion.Database.Blob.Internal;

namespace Assimalign.Cohesion.Database.Blob.Tests;

public sealed class ModelDiagnosticProducerTests
{
    [Fact(DisplayName = "Cohesion Test [Blob] - Diagnostics: overlapping operations retain their refusal and code")]
    public async Task BeginOperation_OverlappingOperation_ShouldCarryCodeAsync()
    {
        await using var engine = BlobDatabaseEngine.Create("diagnostics", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var operation = await database.BeginOperationAsync(session, CancellationToken.None);
        try
        {
            var error = await Should.ThrowAsync<DatabaseException>(async () => await database.BeginOperationAsync(session, CancellationToken.None));
            Refusal(error, "CK07003", "Dispose the active blob stream before starting another operation on this session.");
        }
        finally { await operation.CompleteAsync(); }
    }

    [Fact(DisplayName = "Cohesion Test [Blob] - Diagnostics: a completed operation retains its refusal and code")]
    public async Task EnsureActive_CompletedOperation_ShouldCarryCodeAsync()
    {
        await using var engine = BlobDatabaseEngine.Create("diagnostics", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var operation = await database.BeginOperationAsync(session, CancellationToken.None);
        await operation.CompleteAsync();
        Refusal(Should.Throw<DatabaseException>(operation.EnsureActive), "CK04002", "The blob operation's transaction is no longer active.");
    }

    [Fact(DisplayName = "Cohesion Test [Blob] - Diagnostics: unsupported isolation retains its refusal and code")]
    public async Task BeginTransaction_UnsupportedIsolation_ShouldCarryCodeAsync()
    {
        await using var engine = BlobDatabaseEngine.Create("diagnostics", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.BeginTransactionAsync(IsolationLevel.Serializable, CancellationToken.None));
        Refusal(error, "CK04001", "The blob engine supports Snapshot and ReadCommitted isolation.");
    }

    [Theory(DisplayName = "Cohesion Test [Blob] - Diagnostics: container existence failures have distinct stable identities")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Container_ExistenceFailure_ShouldCarryCodeAsync(bool duplicate)
    {
        await using var engine = BlobDatabaseEngine.Create("diagnostics", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        if (duplicate) { await session.CreateContainerAsync("items", CancellationToken.None); }
        var error = await Should.ThrowAsync<DatabaseException>(async () =>
        {
            if (duplicate) { await session.CreateContainerAsync("items", CancellationToken.None); }
            else { await session.GetContainerAsync("items", CancellationToken.None); }
        });
        Refusal(error, duplicate ? "CB03001" : "CB01001", duplicate ? "Container 'items' already exists." : "Container 'items' does not exist.");
    }

    [Theory(DisplayName = "Cohesion Test [Blob] - Diagnostics: blob existence failures have distinct stable identities")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Blob_ExistenceFailure_ShouldCarryCodeAsync(bool duplicate)
    {
        await using var engine = BlobDatabaseEngine.Create("diagnostics", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var container = await session.CreateContainerAsync("items", CancellationToken.None);
        if (duplicate) { await using var stored = await container.OpenWriteAsync("one", cancellationToken: CancellationToken.None); }
        var error = await Should.ThrowAsync<DatabaseException>(async () =>
        {
            if (duplicate) { await using var stored = await container.OpenWriteAsync("one", new() { Overwrite = false }, CancellationToken.None); }
            else { await using var stored = await container.OpenReadAsync("one", CancellationToken.None); }
        });
        Refusal(error, duplicate ? "CB03002" : "CB01002", duplicate ? "Blob 'one' already exists." : "Blob 'one' does not exist.");
    }

    [Fact(DisplayName = "Cohesion Test [Blob] - Diagnostics: a stale container handle retains its refusal")]
    public async Task Read_ReplacedContainer_ShouldCarryCodeAsync()
    {
        await using var engine = BlobDatabaseEngine.Create("diagnostics", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var container = await session.CreateContainerAsync("items", CancellationToken.None);
        await session.DropContainerAsync("items", CancellationToken.None);
        await session.CreateContainerAsync("items", CancellationToken.None);
        Refusal(await Should.ThrowAsync<DatabaseException>(async () => await container.OpenReadAsync("one", CancellationToken.None)),
            "CB09001", "Container 'items' is no longer available in this transaction.");
    }

    [Fact(DisplayName = "Cohesion Test [Blob] - Diagnostics: wire errors format the model identity once")]
    public async Task Read_MissingBlobWire_ShouldPrefixCodeOnceAsync()
    {
        await using var harness = await BlobServerHarness.StartAsync();
        await using var client = await harness.DialAsync();
        await client.HandshakeAsync();
        await client.SendAsync(BlobProtocolMessageType.Read, new BlobReadMessage(BlobServerHarness.ContainerName, "missing").Encode());
        var error = ProtocolErrorMessage.Decode((await client.ExpectAsync(ProtocolMessageType.Error)).Payload.Span);
        error.Code.ShouldBe(ProtocolErrorCode.ExecutionFailure);
        error.Message.ShouldBe("CB01002: Blob 'missing' does not exist.");
    }

    [Fact(DisplayName = "Cohesion Test [Blob] - Diagnostics: snapshot conflicts retain their concrete exception and message")]
    public async Task Write_ChangedCatalog_ShouldCarryConflictCodeAsync()
    {
        await using var engine = BlobDatabaseEngine.Create("diagnostics", new());
        var database = await engine.CreateDatabaseAsync("db", CancellationToken.None);
        await using var setup = await database.CreateSessionAsync(CancellationToken.None);
        await setup.CreateContainerAsync("items", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await using var transaction = await session.BeginTransactionAsync(IsolationLevel.Snapshot, CancellationToken.None);
        var container = await session.GetContainerAsync("items", CancellationToken.None);
        await setup.DropContainerAsync("items", CancellationToken.None);
        await setup.CreateContainerAsync("items", CancellationToken.None);
        var error = await Should.ThrowAsync<DatabaseTransactionAbortedException>(async () =>
            await container.OpenWriteAsync("one", cancellationToken: CancellationToken.None));
        error.ShouldBeOfType<DatabaseTransactionAbortedException>();
        Diagnostic(error, "CB04002", "The blob catalog changed since this transaction's snapshot. Retry the transaction.");
    }

    [Fact(DisplayName = "Cohesion Test [Blob] - Diagnostics: server start refusals retain engine state and identity")]
    public async Task Start_DisposedEngine_ShouldCarryCodeAsync()
    {
        var engine = BlobDatabaseEngine.Create("diagnostics", new());
        await engine.DisposeAsync();
        await using var listener = new InMemoryConnectionListener();
        await using var server = BlobDatabaseServer.Create(engine, new() { Listener = listener });
        Refusal(await Should.ThrowAsync<DatabaseException>(() => server.StartAsync(CancellationToken.None)),
            "CB09002", "The Blob engine is Disposed and cannot accept sessions.");
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
