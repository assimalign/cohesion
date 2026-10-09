using System;
using System.IO;
using System.IO.Pipelines;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Connections.InMemory;
using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Security;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Blob.Tests;

public sealed class BlobDatabaseServerTests
{
    [Fact(DisplayName = "Cohesion Test [Database] - Blob server: Every operation stays in its handshake database and server")]
    public async Task Operations_HandshakeScope_ShouldNeverReachOtherDatabasesOrServers()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        CancellationToken token = deadline.Token;
        await using var engine = BlobDatabaseEngine.Create(new());
        await using var otherEngine = BlobDatabaseEngine.Create(new());
        var own = await engine.CreateDatabaseAsync("own", token);
        var other = await engine.CreateDatabaseAsync("other", token);
        var remote = await otherEngine.CreateDatabaseAsync("own", token);
        var ownFiles = await AutocommitContainer.CreateAsync(own, "files", token);
        var otherFiles = await AutocommitContainer.CreateAsync(other, "files", token);
        var remoteFiles = await AutocommitContainer.CreateAsync(remote, "files", token);
        await WriteBlobAsync(otherFiles, "item", "other"u8.ToArray(), token);
        await WriteBlobAsync(remoteFiles, "item", "remote"u8.ToArray(), token);
        var listener = new InMemoryConnectionListener();
        var remoteListener = new InMemoryConnectionListener();
        await using var server = BlobDatabaseServer.Create(engine, new() { Listener = listener });
        await using var remoteServer = BlobDatabaseServer.Create(otherEngine, new() { Listener = remoteListener });
        await server.StartAsync(token);
        await remoteServer.StartAsync(token);
        await using IConnection connection = await listener.CreateFactory().ConnectAsync(listener.EndPoint, token);
        await using var channel = new ProtocolChannel(connection.AsStream(), BlobProtocol.Family);
        await HandshakeAsync(channel, "own", token);
        server.Sessions.Single().DatabaseSession!.Database.Name.ShouldBe(own.Name);
        remoteServer.Sessions.ShouldBeEmpty();

        await WriteAsync(channel, BlobProtocolMessageType.Write, new BlobWriteMessage("files", "item").Encode(), token);
        await BlobProtocolTransfer.SendAsync(channel, new MemoryStream("own"u8.ToArray()), new(3, "text/plain"), token);
        BlobTransferCompleteMessage.Decode((await ReadAsync(channel, token)).Payload.Span).Length.ShouldBe(3);
        await WriteAsync(channel, BlobProtocolMessageType.Read, new BlobReadMessage("files", "item").Encode(), token);
        using var downloaded = new MemoryStream();
        await BlobProtocolTransfer.ReceiveAsync(channel, downloaded, token);
        downloaded.ToArray().ShouldBe("own"u8.ToArray());
        await WriteAsync(channel, BlobProtocolMessageType.GetProperties, new BlobGetPropertiesMessage("files", "item").Encode(), token);
        BlobPropertiesMessage.Decode((await ReadAsync(channel, token)).Payload.Span).Properties.Length.ShouldBe(3);
        BlobOperationCompleteMessage.Decode((await ReadAsync(channel, token)).Payload.Span).Count.ShouldBe(1);
        await WriteAsync(channel, BlobProtocolMessageType.List, new BlobListMessage("files", "it").Encode(), token);
        BlobPropertiesMessage.Decode((await ReadAsync(channel, token)).Payload.Span).Properties.Name.ShouldBe("item");
        BlobOperationCompleteMessage.Decode((await ReadAsync(channel, token)).Payload.Span).Count.ShouldBe(1);
        await WriteAsync(channel, BlobProtocolMessageType.Delete, new BlobDeleteMessage("files", "item").Encode(), token);
        BlobOperationCompleteMessage.Decode((await ReadAsync(channel, token)).Payload.Span).Count.ShouldBe(1);
        (await ownFiles.GetPropertiesAsync("item", token)).ShouldBeNull();
        await WriteAsync(channel, BlobProtocolMessageType.Read, new BlobReadMessage("other/files", "item").Encode(), token);
        ProtocolErrorMessage.Decode((await ReadAsync(channel, token)).Payload.Span).Code.ShouldBe(ProtocolErrorCode.ExecutionFailure);
        (await ReadBlobAsync(otherFiles, "item", token)).ShouldBe("other"u8.ToArray());
        (await ReadBlobAsync(remoteFiles, "item", token)).ShouldBe("remote"u8.ToArray());
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Blob server: Authentication sees the database, principal and evidence")]
    public async Task Handshake_RejectedAuthentication_ShouldNeverOpenSession()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        CancellationToken token = deadline.Token;
        await using var engine = BlobDatabaseEngine.Create(new());
        await engine.CreateDatabaseAsync("objects", token);
        var listener = new InMemoryConnectionListener();
        var authenticator = new RejectAuthenticator();
        await using var server = BlobDatabaseServer.Create(engine, new() { Listener = listener, Authenticator = authenticator });
        await server.StartAsync(token);
        await using IConnection connection = await listener.CreateFactory().ConnectAsync(listener.EndPoint, token);
        await using var channel = new ProtocolChannel(connection.AsStream(), BlobProtocol.Family);
        await WriteAsync(channel, ProtocolMessageType.Startup, new ProtocolStartupMessage(ProtocolVersion.Current, "objects", "alice").Encode(), token);
        (await ReadAsync(channel, token)).Type.ShouldBe(ProtocolMessageType.Authenticate);
        await WriteAsync(channel, ProtocolMessageType.AuthenticateResponse, "proof"u8.ToArray(), token);
        ProtocolErrorMessage.Decode((await ReadAsync(channel, token)).Payload.Span).Code.ShouldBe(ProtocolErrorCode.AuthenticationFailed);
        authenticator.Database.ShouldBe("objects");
        authenticator.Principal.ShouldBe("alice");
        authenticator.Evidence.ShouldBe("proof"u8.ToArray());
        await server.StopAsync(token);
        server.Sessions.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Blob server: Session limits and idle eviction release slots")]
    public async Task Sessions_MaximumAndIdleTimeout_ShouldRejectAndEvict()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        CancellationToken token = deadline.Token;
        await using var engine = BlobDatabaseEngine.Create(new());
        await engine.CreateDatabaseAsync("objects", token);
        var listener = new InMemoryConnectionListener();
        await using var server = BlobDatabaseServer.Create(engine, new()
        {
            Listener = listener, MaxSessions = 1, IdleTimeout = TimeSpan.FromMilliseconds(300)
        });
        await server.StartAsync(token);
        await using IConnection first = await listener.CreateFactory().ConnectAsync(listener.EndPoint, token);
        await using var channel = new ProtocolChannel(first.AsStream(), BlobProtocol.Family);
        await HandshakeAsync(channel, "objects", token);
        await using IConnection second = await listener.CreateFactory().ConnectAsync(listener.EndPoint, token);
        await using var rejected = new ProtocolChannel(second.AsStream(), BlobProtocol.Family);
        ProtocolErrorMessage.Decode((await ReadAsync(rejected, token)).Payload.Span).Code.ShouldBe(ProtocolErrorCode.Unavailable);
        ProtocolErrorMessage idle = ProtocolErrorMessage.Decode((await ReadAsync(channel, token)).Payload.Span);
        idle.Code.ShouldBe(ProtocolErrorCode.Unavailable);
        idle.Message.ShouldContain("idle timeout");
        await server.StopAsync(token);
        server.Sessions.ShouldBeEmpty();
        engine.State.ShouldBe(EngineState.Running);
        await Should.ThrowAsync<ObjectDisposedException>(() => server.StartAsync(token));
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Blob server: Authentication timeout closes unauthenticated connections")]
    public async Task Handshake_Timeout_ShouldCloseConnection()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var engine = BlobDatabaseEngine.Create(new());
        var listener = new InMemoryConnectionListener();
        await using var server = BlobDatabaseServer.Create(engine, new()
        {
            Listener = listener, AuthenticationTimeout = TimeSpan.FromMilliseconds(100)
        });
        await server.StartAsync(deadline.Token);
        await using IConnection connection = await listener.CreateFactory().ConnectAsync(listener.EndPoint, deadline.Token);
        await using var channel = new ProtocolChannel(connection.AsStream(), BlobProtocol.Family);
        (await channel.Reader.ReadFrameAsync(deadline.Token)).ShouldBeNull();
        await server.StopAsync(deadline.Token);
        server.Sessions.ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [Database] - Blob server: Shutdown drains completed uploads and rolls back stalled uploads")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Stop_ActiveUpload_ShouldDrainThenAbortAtDeadline(bool finish)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        CancellationToken token = deadline.Token;
        await using var engine = BlobDatabaseEngine.Create(new());
        var database = await engine.CreateDatabaseAsync("objects", token);
        var container = await AutocommitContainer.CreateAsync(database, "files", token);
        await WriteBlobAsync(container, "item", "previous"u8.ToArray(), token);
        var listener = new InMemoryConnectionListener();
        await using var server = BlobDatabaseServer.Create(engine, new()
        {
            Listener = listener, ShutdownDrainTimeout = finish ? TimeSpan.FromSeconds(5) : TimeSpan.FromMilliseconds(100)
        });
        await server.StartAsync(token);
        await using IConnection connection = await listener.CreateFactory().ConnectAsync(listener.EndPoint, token);
        await using var channel = new ProtocolChannel(connection.AsStream(), BlobProtocol.Family);
        await HandshakeAsync(channel, "objects", token);
        await WriteAsync(channel, BlobProtocolMessageType.Write, new BlobWriteMessage("files", "item").Encode(), token);
        await WriteAsync(channel, BlobProtocolMessageType.TransferStart, new BlobTransferStartMessage(3).Encode(), token);
        await WriteAsync(channel, BlobProtocolMessageType.Chunk, "new"u8.ToArray(), token);
        BlobChunkAcknowledgementMessage.Decode((await ReadAsync(channel, token)).Payload.Span).Length.ShouldBe(3);
        Task stop = server.StopAsync(token);
        if (finish)
        {
            stop.IsCompleted.ShouldBeFalse();
            await WriteAsync(channel, BlobProtocolMessageType.TransferComplete, new BlobTransferCompleteMessage(3).Encode(), token);
            (await ReadAsync(channel, token)).Type.ShouldBe((ProtocolMessageType)BlobProtocolMessageType.TransferComplete);
        }
        await stop.WaitAsync(token);
        server.Sessions.ShouldBeEmpty();
        (await ReadBlobAsync(container, "item", token)).ShouldBe(finish ? "new"u8.ToArray() : "previous"u8.ToArray());
        engine.State.ShouldBe(EngineState.Running);
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Blob server: Disposed engines cannot start accepting sessions")]
    public async Task Start_DisposedEngine_ShouldReject()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var engine = BlobDatabaseEngine.Create(new());
        await engine.DisposeAsync();
        var listener = new InMemoryConnectionListener();
        await using var server = BlobDatabaseServer.Create(engine, new() { Listener = listener });
        var error = await Should.ThrowAsync<DatabaseException>(() => server.StartAsync(deadline.Token));
        error.Message.ShouldContain("Disposed");
    }

    /// <summary>
    /// A start the server refuses because its engine failed as a whole (here
    /// <see cref="EngineState.Faulted"/> by a worker whose every pass fails before it reaches a
    /// database, <see cref="DatabaseEngine.HasEngineWideFailure"/>; owner decision 42 of 2026-10-07)
    /// is terminal under the root <see cref="DatabaseServer"/> lifecycle (concrete-types plan, row 9,
    /// owner decision 31): the start core disposes the listener before the refusal propagates, a
    /// later start throws <see cref="ObjectDisposedException"/>, and the disposal finds nothing left
    /// to release. Before the base, the refused start left the server inert, so a later start could
    /// retry and a later stop disposed the listener.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database] - Blob server: a start refused for an engine failed as a whole disposes the listener and is terminal")]
    public async Task StartAsync_EngineFaulted_ShouldDisposeTheListenerAndStayStopped()
    {
        // Arrange: a worker whose every pass fails as a whole holds a fault of its own, so the
        // engine reports Faulted for every database.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        CancellationToken token = deadline.Token;
        var builder = BlobDatabaseEngine.CreateBuilder("blob-engine");
        builder.AddWorker(engine => new FailingWorker(engine.Name + "/failing"));
        await using var engine = builder.Build();
        while (engine.State != EngineState.Faulted)
        {
            await Task.Delay(10, token);
        }

        var listener = new CountingListener();
        var server = BlobDatabaseServer.Create(engine, new() { Listener = listener });

        // Act
        var refusal = await Should.ThrowAsync<DatabaseException>(() => server.StartAsync(token));
        int disposalsAfterTheRefusal = listener.Disposals;
        var retry = await Should.ThrowAsync<ObjectDisposedException>(() => server.StartAsync(token));
        await server.DisposeAsync();

        // Assert
        engine.HasEngineWideFailure.ShouldBeTrue();
        refusal.Message.ShouldBe("The Blob engine is Faulted and cannot accept sessions.");
        disposalsAfterTheRefusal.ShouldBe(1);
        retry.ShouldNotBeNull();
        listener.Binds.ShouldBe(0);
        listener.Disposals.ShouldBe(1);
        server.Sessions.ShouldBeEmpty();
    }

    /// <summary>
    /// A worker failing on one database refuses that database alone (owner decision 42 of
    /// 2026-10-07). The engine is <see cref="EngineState.Faulted"/> with the failing worker, yet
    /// the server starts, accepts and serves the healthy database over the wire; the failing
    /// database's handshake, and an exchange on its session opened before the failure, are refused
    /// with <see cref="ProtocolErrorCode.Unavailable"/> and <c>COHDBB003</c>; once a pass finishes
    /// the failing work the database is served again. Before the decision the server refused every
    /// start, connection, handshake and exchange while the engine was not
    /// <see cref="EngineState.Running"/>, so one failing database made the whole server unavailable.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Blob] - Blob server: a worker failing on one database refuses that database alone, and serves the others")]
    public async Task Server_WorkerFailingOnOneDatabase_ShouldRefuseOnlyThatDatabase()
    {
        // Arrange: a registered checkpoint worker the test drives pass by pass fails database
        // "failing" while its failure is set; the engine's give-up is out of reach.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        CancellationToken token = deadline.Token;
        DatabaseFailingWorker? registered = null;
        var builder = BlobDatabaseEngine.CreateBuilder("blob-engine");
        builder.WorkerFailureMinimumPasses = int.MaxValue;
        builder.AddWorker(built => registered = new DatabaseFailingWorker(built.Name + "/probe", "failing"));
        await using var engine = builder.Build();
        var worker = registered.ShouldNotBeNull();
        var failing = await engine.CreateDatabaseAsync("failing", token);
        var healthy = await engine.CreateDatabaseAsync("healthy", token);
        await AutocommitContainer.CreateAsync(failing, "files", token);
        await AutocommitContainer.CreateAsync(healthy, "files", token);
        var listener = new InMemoryConnectionListener();
        await using var server = BlobDatabaseServer.Create(engine, new() { Listener = listener });

        // Act: the worker fails on "failing"; the server starts and serves "healthy".
        worker.Failure = new IOException("Injected checkpoint failure");
        worker.RunIteration(token);
        var faulted = (engine.State, Failing: engine.HasFailingWorker("failing"), Healthy: engine.HasFailingWorker("healthy"), EngineWide: engine.HasEngineWideFailure);
        await server.StartAsync(token);
        await using IConnection healthyConnection = await listener.CreateFactory().ConnectAsync(listener.EndPoint, token);
        await using var healthyChannel = new ProtocolChannel(healthyConnection.AsStream(), BlobProtocol.Family);
        await HandshakeAsync(healthyChannel, "healthy", token);
        await WriteAsync(healthyChannel, BlobProtocolMessageType.Write, new BlobWriteMessage("files", "item").Encode(), token);
        await BlobProtocolTransfer.SendAsync(healthyChannel, new MemoryStream("healthy"u8.ToArray()), new(7, "text/plain"), token);
        var written = BlobTransferCompleteMessage.Decode((await ReadAsync(healthyChannel, token)).Payload.Span);

        // The failing database's handshake is refused.
        await using IConnection refusedConnection = await listener.CreateFactory().ConnectAsync(listener.EndPoint, token);
        await using var refusedChannel = new ProtocolChannel(refusedConnection.AsStream(), BlobProtocol.Family);
        var handshakeRefusal = await HandshakeRefusedAsync(refusedChannel, "failing", token);

        // A pass finishes the failing work: the database is served again.
        worker.Failure = null;
        worker.RunIteration(token);
        var recovered = (engine.State, Failing: engine.HasFailingWorker("failing"));
        await using IConnection failingConnection = await listener.CreateFactory().ConnectAsync(listener.EndPoint, token);
        await using var failingChannel = new ProtocolChannel(failingConnection.AsStream(), BlobProtocol.Family);
        await HandshakeAsync(failingChannel, "failing", token);
        await WriteAsync(failingChannel, BlobProtocolMessageType.List, new BlobListMessage("files", "").Encode(), token);
        var listed = BlobOperationCompleteMessage.Decode((await ReadAsync(failingChannel, token)).Payload.Span);

        // The worker fails again: the open session of "failing" is refused at its next exchange,
        // while the session of "healthy" is still served.
        worker.Failure = new IOException("Injected checkpoint failure");
        worker.RunIteration(token);
        await WriteAsync(failingChannel, BlobProtocolMessageType.List, new BlobListMessage("files", "").Encode(), token);
        var exchangeFrame = await ReadAsync(failingChannel, token);
        var closedAfterTheRefusal = await failingChannel.Reader.ReadFrameAsync(token);
        await WriteAsync(healthyChannel, BlobProtocolMessageType.GetProperties, new BlobGetPropertiesMessage("files", "item").Encode(), token);
        var properties = BlobPropertiesMessage.Decode((await ReadAsync(healthyChannel, token)).Payload.Span);
        var served = BlobOperationCompleteMessage.Decode((await ReadAsync(healthyChannel, token)).Payload.Span);

        // Assert
        faulted.ShouldBe((EngineState.Faulted, true, false, false));
        worker.Fault.ShouldNotBeNull().Message.ShouldBe("Injected checkpoint failure");
        written.Length.ShouldBe(7);
        handshakeRefusal.Code.ShouldBe(ProtocolErrorCode.Unavailable);
        handshakeRefusal.Message.ShouldStartWith("COHDBB003", Case.Sensitive);
        handshakeRefusal.Message.ShouldContain("Database 'failing'");
        recovered.ShouldBe((EngineState.Running, false));
        listed.Count.ShouldBe(0);
        exchangeFrame.Type.ShouldBe(ProtocolMessageType.Error);
        var exchangeRefusal = ProtocolErrorMessage.Decode(exchangeFrame.Payload.Span);
        exchangeRefusal.Code.ShouldBe(ProtocolErrorCode.Unavailable);
        exchangeRefusal.Message.ShouldStartWith("COHDBB003", Case.Sensitive);
        closedAfterTheRefusal.ShouldBeNull();
        properties.Properties.Length.ShouldBe(7);
        served.Count.ShouldBe(1);
        engine.State.ShouldBe(EngineState.Faulted);
        engine.OfflineDatabases.ShouldBeEmpty();
    }

    /// <summary>
    /// The offline refusal wins over the worker-failure refusal (owner decision 42 review). A
    /// database a failure of its own storage takes offline, not the engine's give-up, keeps a
    /// worker's record of it until that worker's next pass, which for the version purge can be a
    /// whole maintenance interval away; meanwhile its handshakes and the exchanges of its open
    /// sessions are refused with <c>COHDBB002</c>, not <c>COHDBB003</c>, because the database is
    /// offline, not merely failing.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Blob] - Blob server: an offline database is refused as offline while a worker's record of it lingers")]
    public async Task Server_FailingDatabaseGoesOffline_ShouldRefuseItAsOffline()
    {
        // Arrange: a session of the database is open; the probe worker's give-up is out of reach.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        CancellationToken token = deadline.Token;
        DatabaseFailingWorker? registered = null;
        var builder = BlobDatabaseEngine.CreateBuilder("blob-engine");
        builder.WorkerFailureMinimumPasses = int.MaxValue;
        builder.AddWorker(built => registered = new DatabaseFailingWorker(built.Name + "/probe", "failing"));
        await using var engine = builder.Build();
        var worker = registered.ShouldNotBeNull();
        var failing = await engine.CreateDatabaseAsync("failing", token);
        await AutocommitContainer.CreateAsync(failing, "files", token);
        var listener = new InMemoryConnectionListener();
        await using var server = BlobDatabaseServer.Create(engine, new() { Listener = listener });
        await server.StartAsync(token);
        await using IConnection openConnection = await listener.CreateFactory().ConnectAsync(listener.EndPoint, token);
        await using var openChannel = new ProtocolChannel(openConnection.AsStream(), BlobProtocol.Family);
        await HandshakeAsync(openChannel, "failing", token);

        // Act: the worker fails on the database, then the database's storage goes offline outside
        // the engine's give-up, so nothing ends the worker's record of it.
        worker.Failure = new IOException("Injected checkpoint failure");
        worker.RunIteration(token);
        bool takenOffline = failing.DataStorage.TakeOffline(
            StorageOfflineCause.CheckpointFailures, "the test took the storage offline", new IOException("Injected device failure"));
        bool lingering = engine.HasFailingWorker("failing");
        await using IConnection refusedConnection = await listener.CreateFactory().ConnectAsync(listener.EndPoint, token);
        await using var refusedChannel = new ProtocolChannel(refusedConnection.AsStream(), BlobProtocol.Family);
        var handshakeRefusal = await HandshakeRefusedAsync(refusedChannel, "failing", token);
        await WriteAsync(openChannel, BlobProtocolMessageType.List, new BlobListMessage("files", "").Encode(), token);
        var exchangeFrame = await ReadAsync(openChannel, token);

        // Assert
        takenOffline.ShouldBeTrue();
        lingering.ShouldBeTrue();
        handshakeRefusal.Code.ShouldBe(ProtocolErrorCode.Unavailable);
        handshakeRefusal.Message.ShouldStartWith("COHDBB002", Case.Sensitive);
        exchangeFrame.Type.ShouldBe(ProtocolMessageType.Error);
        var exchangeRefusal = ProtocolErrorMessage.Decode(exchangeFrame.Payload.Span);
        exchangeRefusal.Code.ShouldBe(ProtocolErrorCode.Unavailable);
        exchangeRefusal.Message.ShouldStartWith("COHDBB002", Case.Sensitive);
    }

    /// <summary>
    /// An exchange refused because a worker is failing on its database is terminal like every Blob
    /// wire failure, so it ends a host-opened transaction too (owner decision 42 review, #1225): the
    /// server aborts the transaction with the refusal before it writes the error, so the host's
    /// COMMIT fails with <c>COHDBB001</c> naming <c>COHDBB003</c> whichever of the commit and the
    /// connection's teardown runs first, and the transaction's earlier wire delete is undone.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Blob] - Blob server: a worker-failure refusal ends the host's transaction, and COMMIT fails with COHDBB001 naming COHDBB003")]
    public async Task Server_ExchangeRefusedForAFailingWorker_ShouldAbortTheHostTransactionFirst()
    {
        // Arrange: a host transaction on the connection's engine session deletes a blob over the wire.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        CancellationToken token = deadline.Token;
        DatabaseFailingWorker? registered = null;
        var builder = BlobDatabaseEngine.CreateBuilder("blob-engine");
        builder.WorkerFailureMinimumPasses = int.MaxValue;
        builder.AddWorker(built => registered = new DatabaseFailingWorker(built.Name + "/probe", "failing"));
        await using var engine = builder.Build();
        var worker = registered.ShouldNotBeNull();
        var failing = await engine.CreateDatabaseAsync("failing", token);
        var files = await AutocommitContainer.CreateAsync(failing, "files", token);
        await WriteBlobAsync(files, "keep", "original"u8.ToArray(), token);
        var listener = new InMemoryConnectionListener();
        await using var server = BlobDatabaseServer.Create(engine, new() { Listener = listener });
        await server.StartAsync(token);
        await using IConnection connection = await listener.CreateFactory().ConnectAsync(listener.EndPoint, token);
        await using var channel = new ProtocolChannel(connection.AsStream(), BlobProtocol.Family);
        await HandshakeAsync(channel, "failing", token);
        var transaction = await server.Sessions.ShouldHaveSingleItem().DatabaseSession.ShouldNotBeNull().BeginTransactionAsync(token);
        await WriteAsync(channel, BlobProtocolMessageType.Delete, new BlobDeleteMessage("files", "keep").Encode(), token);
        var deleted = BlobOperationCompleteMessage.Decode((await ReadAsync(channel, token)).Payload.Span);

        // Act: the worker fails on the database, and the next exchange is refused.
        worker.Failure = new IOException("Injected checkpoint failure");
        worker.RunIteration(token);
        await WriteAsync(channel, BlobProtocolMessageType.List, new BlobListMessage("files", "").Encode(), token);
        var refusalFrame = await ReadAsync(channel, token);
        var commit = await Should.ThrowAsync<DatabaseException>(async () => await transaction.CommitAsync(token));

        // Assert
        deleted.Count.ShouldBe(1);
        refusalFrame.Type.ShouldBe(ProtocolMessageType.Error);
        var refusal = ProtocolErrorMessage.Decode(refusalFrame.Payload.Span);
        refusal.Code.ShouldBe(ProtocolErrorCode.Unavailable);
        refusal.Message.ShouldStartWith("COHDBB003", Case.Sensitive);
        commit.Message.ShouldStartWith("COHDBB001", Case.Sensitive);
        commit.Message.ShouldContain(refusal.Message, Case.Sensitive);
        transaction.State.ShouldBe(TransactionState.RolledBack);
        (await ReadBlobAsync(files, "keep", token)).ShouldBe("original"u8.ToArray());
    }

    /// <summary>
    /// An index-maintenance worker's failure never takes its database offline (its work costs space,
    /// not durability), so the server does not refuse the database for it (owner decision 42
    /// review): a refusal would last for as long as the work kept failing. The engine still reports
    /// <see cref="EngineState.Faulted"/>.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Blob] - Blob server: an index-maintenance worker failing on a database does not refuse it")]
    public async Task Server_IndexMaintenanceWorkerFailing_ShouldServeTheDatabase()
    {
        // Arrange
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        CancellationToken token = deadline.Token;
        DatabaseFailingWorker? registered = null;
        var builder = BlobDatabaseEngine.CreateBuilder("blob-engine");
        builder.AddWorker(built => registered = new DatabaseFailingWorker(built.Name + "/maintenance", "objects", DatabaseEngineWorkerKind.IndexMaintenance));
        await using var engine = builder.Build();
        var worker = registered.ShouldNotBeNull();
        var database = await engine.CreateDatabaseAsync("objects", token);
        await AutocommitContainer.CreateAsync(database, "files", token);
        var listener = new InMemoryConnectionListener();
        await using var server = BlobDatabaseServer.Create(engine, new() { Listener = listener });

        // Act
        worker.Failure = new IOException("Injected index maintenance failure");
        worker.RunIteration(token);
        var faulted = (engine.State, Failing: engine.HasFailingWorker("objects"));
        await server.StartAsync(token);
        await using IConnection connection = await listener.CreateFactory().ConnectAsync(listener.EndPoint, token);
        await using var channel = new ProtocolChannel(connection.AsStream(), BlobProtocol.Family);
        await HandshakeAsync(channel, "objects", token);
        await WriteAsync(channel, BlobProtocolMessageType.List, new BlobListMessage("files", "").Encode(), token);
        var listed = BlobOperationCompleteMessage.Decode((await ReadAsync(channel, token)).Payload.Span);

        // Assert
        faulted.ShouldBe((EngineState.Faulted, false));
        worker.Fault.ShouldNotBeNull().Message.ShouldBe("Injected index maintenance failure");
        listed.Count.ShouldBe(0);
    }

    /// <summary>
    /// Disposing a session's database (option B of the concrete-types plan, §6.6: the session's
    /// <see cref="BlobDatabaseSession.Database"/> is the unbound database) closes that database
    /// alone. Its workers skip it, so the engine stays <see cref="EngineState.Running"/> and its
    /// server still starts and serves the engine's other databases; once the close ends the engine
    /// forgets it, so the next open, in process or by a handshake over the wire, opens it again with
    /// its blobs (owner decision 33, #1289). Before the workers skipped a closed
    /// database, the version-purge worker failed on its disposed coordinator every pass, the engine
    /// reported <see cref="EngineState.Faulted"/> for good, and the server refused every start,
    /// connection and handshake.
    /// </summary>
    [Fact(DisplayName = "Cohesion Test [Database.Blob] - Blob server: a database closed through a session leaves the engine running and its server serving")]
    public async Task DisposeAsync_SessionDatabase_ShouldLeaveTheEngineRunningAndItsServerServing()
    {
        // Arrange: workers that pass every 20 ms, and a closed database with a written blob.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        CancellationToken token = deadline.Token;
        var interval = TimeSpan.FromMilliseconds(20);
        await using var engine = BlobDatabaseEngine.Create(new()
        {
            CheckpointInterval = interval, PageWriteBackInterval = interval, MaintenanceInterval = interval
        });
        var closed = await engine.CreateDatabaseAsync("closed", token);
        var closedFiles = await AutocommitContainer.CreateAsync(closed, "files", token);
        await WriteBlobAsync(closedFiles, "item", "closed"u8.ToArray(), token);
        var open = await engine.CreateDatabaseAsync("open", token);
        await AutocommitContainer.CreateAsync(open, "files", token);
        await using var session = await closed.CreateSessionAsync(token);

        // Act: close the database through the session, let the workers pass over it many times,
        // run one more pass of each, then start a server and write over it to the other database.
        await session.Database.DisposeAsync();
        await Task.Delay(TimeSpan.FromMilliseconds(500), token);
        var passes = engine.Workers.Select(worker => worker.RunIteration(token)).ToArray();
        var listener = new InMemoryConnectionListener();
        await using var server = BlobDatabaseServer.Create(engine, new() { Listener = listener });
        await server.StartAsync(token);
        await using IConnection connection = await listener.CreateFactory().ConnectAsync(listener.EndPoint, token);
        await using var channel = new ProtocolChannel(connection.AsStream(), BlobProtocol.Family);
        await HandshakeAsync(channel, "open", token);
        await WriteAsync(channel, BlobProtocolMessageType.Write, new BlobWriteMessage("files", "item").Encode(), token);
        await BlobProtocolTransfer.SendAsync(channel, new MemoryStream("open"u8.ToArray()), new(4, "text/plain"), token);
        var written = BlobTransferCompleteMessage.Decode((await ReadAsync(channel, token)).Payload.Span);

        // Assert
        passes.ShouldAllBe(passed => passed);
        engine.Workers.ShouldAllBe(worker => worker.FailureCount == 0 && worker.Fault == null);
        engine.State.ShouldBe(EngineState.Running);
        engine.OfflineDatabases.ShouldBeEmpty();
        server.Sessions.Single().DatabaseSession!.Database.Name.ShouldBe(open.Name);
        written.Length.ShouldBe(4);
        (await ReadBlobAsync(await AutocommitContainer.GetAsync(open, "files", token), "item", token)).ShouldBe("open"u8.ToArray());

        // Assert: the closed database opens again, over the wire and in process, with its blob.
        await using IConnection reconnection = await listener.CreateFactory().ConnectAsync(listener.EndPoint, token);
        await using var rechannel = new ProtocolChannel(reconnection.AsStream(), BlobProtocol.Family);
        await HandshakeAsync(rechannel, "closed", token);
        engine.TryGetDatabase("closed", out var reopened).ShouldBeTrue();
        reopened.ShouldNotBeSameAs(closed);
        (await ReadBlobAsync(await AutocommitContainer.GetAsync(reopened, "files", token), "item", token)).ShouldBe("closed"u8.ToArray());
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Blob server: Shutdown owns blocked capacity rejections")]
    public async Task Stop_BlockedRejection_ShouldAbortAndDisposeEveryAcceptedConnection()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        CancellationToken token = deadline.Token;
        await using var engine = BlobDatabaseEngine.Create(new());
        await engine.CreateDatabaseAsync("objects", token);
        var inner = new InMemoryConnectionListener();
        var listener = new BlockingRejectionListener(inner);
        await using var server = BlobDatabaseServer.Create(engine, new()
        {
            Listener = listener, MaxSessions = 1, ShutdownDrainTimeout = TimeSpan.FromMilliseconds(100)
        });
        await server.StartAsync(token);
        await using IConnection first = await inner.CreateFactory().ConnectAsync(inner.EndPoint, token);
        await using var channel = new ProtocolChannel(first.AsStream(), BlobProtocol.Family);
        await HandshakeAsync(channel, "objects", token);
        await using IConnection second = await inner.CreateFactory().ConnectAsync(inner.EndPoint, token);
        await listener.Blocked.Task.WaitAsync(token);
        await server.StopAsync(token).WaitAsync(token);
        listener.Rejected!.WasAborted.ShouldBeTrue();
        listener.Rejected.WasDisposed.ShouldBeTrue();
        server.Sessions.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Blob server: Listener failure still drains and aborts accepted uploads")]
    public async Task Stop_FailedAcceptLoop_ShouldCleanUpActiveUploadsBeforeRethrowing()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        CancellationToken token = deadline.Token;
        await using var engine = BlobDatabaseEngine.Create(new());
        var database = await engine.CreateDatabaseAsync("objects", token);
        var container = await AutocommitContainer.CreateAsync(database, "files", token);
        var inner = new InMemoryConnectionListener();
        var listener = new FaultingAcceptListener(inner);
        await using var server = BlobDatabaseServer.Create(engine, new()
        {
            Listener = listener, ShutdownDrainTimeout = TimeSpan.FromMilliseconds(100)
        });
        await server.StartAsync(token);
        await using IConnection connection = await inner.CreateFactory().ConnectAsync(inner.EndPoint, token);
        await using var channel = new ProtocolChannel(connection.AsStream(), BlobProtocol.Family);
        await HandshakeAsync(channel, "objects", token);
        await WriteAsync(channel, BlobProtocolMessageType.Write, new BlobWriteMessage("files", "partial").Encode(), token);
        await WriteAsync(channel, BlobProtocolMessageType.TransferStart, new BlobTransferStartMessage(-1).Encode(), token);
        await WriteAsync(channel, BlobProtocolMessageType.Chunk, "unfinished"u8.ToArray(), token);
        (await ReadAsync(channel, token)).Type.ShouldBe((ProtocolMessageType)BlobProtocolMessageType.ChunkAcknowledgement);
        var error = await Should.ThrowAsync<IOException>(() => server.StopAsync(token).WaitAsync(token));
        error.Message.ShouldBe("Injected accept failure.");
        server.Sessions.ShouldBeEmpty();
        listener.WasDisposed.ShouldBeTrue();
        (await container.GetPropertiesAsync("partial", token)).ShouldBeNull();
        await server.StopAsync(token);
    }

    private static async Task HandshakeAsync(ProtocolChannel channel, string database, CancellationToken token)
    {
        await WriteAsync(channel, ProtocolMessageType.Startup, new ProtocolStartupMessage(ProtocolVersion.Current, database, "tester").Encode(), token);
        (await ReadAsync(channel, token)).Type.ShouldBe(ProtocolMessageType.Authenticate);
        await WriteAsync(channel, ProtocolMessageType.AuthenticateResponse, ReadOnlyMemory<byte>.Empty, token);
        (await ReadAsync(channel, token)).Type.ShouldBe(ProtocolMessageType.Ready);
    }

    // A handshake the server refuses after authentication: the refusal's error message.
    private static async Task<ProtocolErrorMessage> HandshakeRefusedAsync(ProtocolChannel channel, string database, CancellationToken token)
    {
        await WriteAsync(channel, ProtocolMessageType.Startup, new ProtocolStartupMessage(ProtocolVersion.Current, database, "tester").Encode(), token);
        (await ReadAsync(channel, token)).Type.ShouldBe(ProtocolMessageType.Authenticate);
        await WriteAsync(channel, ProtocolMessageType.AuthenticateResponse, ReadOnlyMemory<byte>.Empty, token);
        var frame = await ReadAsync(channel, token);
        frame.Type.ShouldBe(ProtocolMessageType.Error);
        return ProtocolErrorMessage.Decode(frame.Payload.Span);
    }

    private static Task WriteAsync(ProtocolChannel channel, BlobProtocolMessageType type, ReadOnlyMemory<byte> payload, CancellationToken token)
        => WriteAsync(channel, (ProtocolMessageType)type, payload, token);

    private static async Task WriteAsync(ProtocolChannel channel, ProtocolMessageType type, ReadOnlyMemory<byte> payload, CancellationToken token)
    {
        await channel.Writer.WriteFrameAsync(new(type, payload), token);
        await channel.Writer.FlushAsync(token);
    }

    private static async Task<ProtocolFrame> ReadAsync(ProtocolChannel channel, CancellationToken token)
        => (await channel.Reader.ReadFrameAsync(token)).ShouldNotBeNull();

    private static async Task WriteBlobAsync(BlobContainer container, string name, byte[] bytes, CancellationToken token)
    {
        await using Stream stream = await container.OpenWriteAsync(name, cancellationToken: token);
        await stream.WriteAsync(bytes, token);
    }

    private static async Task WriteBlobAsync(AutocommitContainer container, string name, byte[] bytes, CancellationToken token)
    {
        await using Stream stream = await container.OpenWriteAsync(name, cancellationToken: token);
        await stream.WriteAsync(bytes, token);
    }

    private static async Task<byte[]> ReadBlobAsync(BlobContainer container, string name, CancellationToken token)
    {
        await using Stream stream = await container.OpenReadAsync(name, token);
        using var result = new MemoryStream();
        await stream.CopyToAsync(result, token);
        return result.ToArray();
    }

    private static async Task<byte[]> ReadBlobAsync(AutocommitContainer container, string name, CancellationToken token)
    {
        await using Stream stream = await container.OpenReadAsync(name, token);
        using var result = new MemoryStream();
        await stream.CopyToAsync(result, token);
        return result.ToArray();
    }

    /// <summary>A worker whose every pass fails, so it holds a fault and its engine reports Faulted.</summary>
    private sealed class FailingWorker : DatabaseEngineWorker
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="FailingWorker"/> class.
        /// </summary>
        /// <param name="name">The worker's name, unique within its engine.</param>
        public FailingWorker(string name)
            : base(name, DatabaseEngineWorkerKind.IndexMaintenance, TimeSpan.FromMilliseconds(10))
        {
        }

        protected override void RunIterationCore(CancellationToken cancellationToken)
            => throw new InvalidOperationException("The worker's pass failed.");
    }

    /// <summary>
    /// A worker the test drives pass by pass, a checkpoint worker unless the test names another
    /// kind: each pass reports <see cref="Failure"/> for one database while it is set, with no
    /// backoff, and finishes that database's work otherwise. Its one-hour interval keeps the
    /// engine's pump from running a pass the test did not ask for.
    /// </summary>
    private sealed class DatabaseFailingWorker : DatabaseEngineWorker
    {
        private readonly string _database;
        private Exception? _failure;

        /// <summary>
        /// Initializes a new instance of the <see cref="DatabaseFailingWorker"/> class.
        /// </summary>
        /// <param name="name">The worker's name, unique within its engine.</param>
        /// <param name="database">The database whose work fails while a failure is set.</param>
        /// <param name="kind">The worker's role.</param>
        public DatabaseFailingWorker(string name, string database, DatabaseEngineWorkerKind kind = DatabaseEngineWorkerKind.Checkpoint)
            : base(name, kind, TimeSpan.FromHours(1))
        {
            _database = database;
        }

        public Exception? Failure
        {
            get => Volatile.Read(ref _failure);
            set => Volatile.Write(ref _failure, value);
        }

        protected override void RunIterationCore(CancellationToken cancellationToken)
        {
            if (BeginDatabase(_database) && Failure is { } failure)
            {
                ReportFailure(_database, failure, TimeSpan.Zero);
            }
        }
    }

    /// <summary>A listener over an in-memory one that counts its binds and disposals.</summary>
    private sealed class CountingListener : IConnectionListener
    {
        private readonly InMemoryConnectionListener _inner = new();
        private int _binds;
        private int _disposals;

        internal int Binds => Volatile.Read(ref _binds);
        internal int Disposals => Volatile.Read(ref _disposals);
        public EndPoint EndPoint => _inner.EndPoint;
        public ConnectionCapabilities Capabilities => _inner.Capabilities;

        public ValueTask BindAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _binds);
            return _inner.BindAsync(cancellationToken);
        }

        public async ValueTask<IConnection> AcceptAsync(CancellationToken cancellationToken = default) => await _inner.AcceptAsync(cancellationToken);

        public async ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposals);
            await _inner.DisposeAsync();
        }
    }

    private sealed class FaultingAcceptListener : IConnectionListener
    {
        private readonly InMemoryConnectionListener _inner;
        private bool _accepted;
        /// <summary>
        /// Initializes a new instance of the <see cref="FaultingAcceptListener"/> class.
        /// </summary>
        /// <param name="inner">The listener that accepts the first connection before accepts start failing.</param>
        public FaultingAcceptListener(InMemoryConnectionListener inner)
        {
            _inner = inner;
        }
        internal bool WasDisposed { get; private set; }
        public EndPoint EndPoint => _inner.EndPoint;
        public ConnectionCapabilities Capabilities => _inner.Capabilities;
        public ValueTask BindAsync(CancellationToken cancellationToken = default) => _inner.BindAsync(cancellationToken);
        public async ValueTask<IConnection> AcceptAsync(CancellationToken cancellationToken = default)
        {
            if (_accepted) { throw new IOException("Injected accept failure."); }
            _accepted = true;
            return await _inner.AcceptAsync(cancellationToken);
        }
        public async ValueTask DisposeAsync() { WasDisposed = true; await _inner.DisposeAsync(); }
    }

    private sealed class BlockingRejectionListener : IConnectionListener
    {
        private readonly InMemoryConnectionListener _inner;
        private int _accepted;
        /// <summary>
        /// Initializes a new instance of the <see cref="BlockingRejectionListener"/> class.
        /// </summary>
        /// <param name="inner">The listener whose accepted connections are passed through or wrapped as blocked rejections.</param>
        public BlockingRejectionListener(InMemoryConnectionListener inner)
        {
            _inner = inner;
        }
        internal TaskCompletionSource Blocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal BlockedConnection? Rejected { get; private set; }
        public EndPoint EndPoint => _inner.EndPoint;
        public ConnectionCapabilities Capabilities => _inner.Capabilities;
        public ValueTask BindAsync(CancellationToken cancellationToken = default) => _inner.BindAsync(cancellationToken);
        public async ValueTask<IConnection> AcceptAsync(CancellationToken cancellationToken = default)
        {
            IConnection connection = await _inner.AcceptAsync(cancellationToken);
            return ++_accepted == 1 ? connection : Rejected = new BlockedConnection(connection, Blocked);
        }
        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }

    private sealed class BlockedConnection : IConnection
    {
        private readonly IConnection _inner;
        /// <summary>
        /// Initializes a new instance of the <see cref="BlockedConnection"/> class.
        /// </summary>
        /// <param name="inner">The connection being wrapped.</param>
        /// <param name="blocked">The completion source signaled when a flush on the output blocks.</param>
        public BlockedConnection(IConnection inner, TaskCompletionSource blocked)
        {
            _inner = inner;
            Output = new BlockedWriter(inner.Output, blocked);
        }
        internal bool WasAborted { get; private set; }
        internal bool WasDisposed { get; private set; }
        public ConnectionId Id => _inner.Id;
        public EndPoint? LocalEndPoint => _inner.LocalEndPoint;
        public EndPoint? RemoteEndPoint => _inner.RemoteEndPoint;
        public ConnectionDirection Direction => _inner.Direction;
        public ConnectionCapabilities Capabilities => _inner.Capabilities;
        public ConnectionState State => _inner.State;
        public CancellationToken ConnectionClosed => _inner.ConnectionClosed;
        public PipeReader Input => _inner.Input;
        public PipeWriter Output { get; }
        public void Abort(Exception? reason = null) { WasAborted = true; _inner.Abort(reason); }
        public async ValueTask DisposeAsync() { WasDisposed = true; await _inner.DisposeAsync(); }
    }

    private sealed class BlockedWriter : PipeWriter
    {
        private readonly PipeWriter _inner;
        private readonly TaskCompletionSource _blocked;
        /// <summary>
        /// Initializes a new instance of the <see cref="BlockedWriter"/> class.
        /// </summary>
        /// <param name="inner">The writer that receives every operation except flushes.</param>
        /// <param name="blocked">The completion source signaled when a flush blocks.</param>
        public BlockedWriter(PipeWriter inner, TaskCompletionSource blocked)
        {
            _inner = inner;
            _blocked = blocked;
        }
        public override void Advance(int bytes) => _inner.Advance(bytes);
        public override void CancelPendingFlush() => _inner.CancelPendingFlush();
        public override void Complete(Exception? exception = null) => _inner.Complete(exception);
        public override Memory<byte> GetMemory(int sizeHint = 0) => _inner.GetMemory(sizeHint);
        public override Span<byte> GetSpan(int sizeHint = 0) => _inner.GetSpan(sizeHint);
        public override async ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default)
        {
            _blocked.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return default;
        }
    }

    private sealed class RejectAuthenticator : DatabaseAuthenticator
    {
        internal string? Database { get; private set; }
        internal string? Principal { get; private set; }
        internal byte[]? Evidence { get; private set; }
        // The base's AuthenticateAsync observes a canceled token before this core runs.
        protected override ValueTask<bool> AuthenticateCoreAsync(string database, string principal, ReadOnlyMemory<byte> evidence, CancellationToken cancellationToken)
        {
            Database = database;
            Principal = principal;
            Evidence = evidence.ToArray();
            return ValueTask.FromResult(false);
        }
    }
}
