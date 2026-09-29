using System;
using System.IO;
using System.IO.Pipelines;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Connections.InMemory;
using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Security;
using Shouldly;
using Xunit;

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
        var own = (IBlobDatabase)await engine.CreateDatabaseAsync("own", token);
        var other = (IBlobDatabase)await engine.CreateDatabaseAsync("other", token);
        var remote = (IBlobDatabase)await otherEngine.CreateDatabaseAsync("own", token);
        var ownFiles = await own.CreateContainerAsync("files", token);
        var otherFiles = await other.CreateContainerAsync("files", token);
        var remoteFiles = await remote.CreateContainerAsync("files", token);
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
        server.Context.Sessions.Single().DatabaseSession!.Database.Name.ShouldBe(own.Name);
        remoteServer.Context.Sessions.ShouldBeEmpty();

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
        server.Context.Sessions.ShouldBeEmpty();
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
        server.Context.Sessions.ShouldBeEmpty();
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
        server.Context.Sessions.ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [Database] - Blob server: Shutdown drains completed uploads and rolls back stalled uploads")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Stop_ActiveUpload_ShouldDrainThenAbortAtDeadline(bool finish)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        CancellationToken token = deadline.Token;
        await using var engine = BlobDatabaseEngine.Create(new());
        var database = (IBlobDatabase)await engine.CreateDatabaseAsync("objects", token);
        var container = await database.CreateContainerAsync("files", token);
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
        server.Context.Sessions.ShouldBeEmpty();
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
        server.Context.Sessions.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Database] - Blob server: Listener failure still drains and aborts accepted uploads")]
    public async Task Stop_FailedAcceptLoop_ShouldCleanUpActiveUploadsBeforeRethrowing()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        CancellationToken token = deadline.Token;
        await using var engine = BlobDatabaseEngine.Create(new());
        var database = (IBlobDatabase)await engine.CreateDatabaseAsync("objects", token);
        var container = await database.CreateContainerAsync("files", token);
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
        server.Context.Sessions.ShouldBeEmpty();
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

    private static Task WriteAsync(ProtocolChannel channel, BlobProtocolMessageType type, ReadOnlyMemory<byte> payload, CancellationToken token)
        => WriteAsync(channel, (ProtocolMessageType)type, payload, token);

    private static async Task WriteAsync(ProtocolChannel channel, ProtocolMessageType type, ReadOnlyMemory<byte> payload, CancellationToken token)
    {
        await channel.Writer.WriteFrameAsync(new(type, payload), token);
        await channel.Writer.FlushAsync(token);
    }

    private static async Task<ProtocolFrame> ReadAsync(ProtocolChannel channel, CancellationToken token)
        => (await channel.Reader.ReadFrameAsync(token)).ShouldNotBeNull();

    private static async Task WriteBlobAsync(IBlobContainer container, string name, byte[] bytes, CancellationToken token)
    {
        await using Stream stream = await container.OpenWriteAsync(name, cancellationToken: token);
        await stream.WriteAsync(bytes, token);
    }

    private static async Task<byte[]> ReadBlobAsync(IBlobContainer container, string name, CancellationToken token)
    {
        await using Stream stream = await container.OpenReadAsync(name, token);
        using var result = new MemoryStream();
        await stream.CopyToAsync(result, token);
        return result.ToArray();
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

    private sealed class RejectAuthenticator : IDatabaseAuthenticator
    {
        internal string? Database { get; private set; }
        internal string? Principal { get; private set; }
        internal byte[]? Evidence { get; private set; }
        public ValueTask<bool> AuthenticateAsync(string database, string principal, ReadOnlyMemory<byte> evidence, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Database = database;
            Principal = principal;
            Evidence = evidence.ToArray();
            return ValueTask.FromResult(false);
        }
    }
}
