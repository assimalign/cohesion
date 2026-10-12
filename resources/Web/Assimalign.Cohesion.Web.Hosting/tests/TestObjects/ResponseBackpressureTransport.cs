using System;
using System.IO.Pipelines;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Connections.InMemory;

namespace Assimalign.Cohesion.Web.Hosting.Tests.TestObjects;

/// <summary>
/// Applies response backpressure to a real HTTP listener after its handshake and first response bytes.
/// The in-memory driver itself has unbounded pipes, so a stalled flush models exhausted peer credit.
/// </summary>
internal sealed class ResponseBackpressureTransport : IAsyncDisposable
{
    private BlockingWriter? _writer;

    public ResponseBackpressureTransport()
    {
        StreamListener = new DecoratingListener(this);
        MultiplexedListener = new DecoratingMultiplexedListener(this);
    }

    public InMemoryConnectionListener Connections { get; } = new();

    public InMemoryMultiplexedConnectionListener MultiplexedConnections { get; } = new();

    public ConnectionListener StreamListener { get; }

    public MultiplexedConnectionListener MultiplexedListener { get; }

    public void StallResponse() => _writer!.Stall();

    public async ValueTask DisposeAsync()
    {
        await Connections.DisposeAsync();
        await MultiplexedConnections.DisposeAsync();
    }

    private Connection Decorate(Connection connection)
    {
        BlockingWriter writer = new(connection.Output);
        _writer = writer;
        return new ResponseConnection(connection, writer);
    }

    private sealed class BlockingWriter : PipeWriter
    {
        private readonly PipeWriter _inner;
        private bool _stalled;

        public BlockingWriter(PipeWriter inner) => _inner = inner;

        public void Stall() => Volatile.Write(ref _stalled, true);

        public override void Advance(int bytes) => _inner.Advance(bytes);

        public override Memory<byte> GetMemory(int sizeHint = 0) => _inner.GetMemory(sizeHint);

        public override Span<byte> GetSpan(int sizeHint = 0) => _inner.GetSpan(sizeHint);

        public override void CancelPendingFlush() => _inner.CancelPendingFlush();

        public override void Complete(Exception? exception = null) => _inner.Complete(exception);

        public override ValueTask CompleteAsync(Exception? exception = null) => _inner.CompleteAsync(exception);

        public override async ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default)
        {
            if (Volatile.Read(ref _stalled))
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            }

            return await _inner.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class ResponseConnection : Connection
    {
        private readonly Connection _inner;
        private readonly PipeWriter _writer;

        public ResponseConnection(Connection inner, PipeWriter writer)
        {
            _inner = inner;
            _writer = writer;
        }

        protected override IConnectionFeatureCollection InnerFeatures => _inner.Features;

        public override ConnectionId Id => _inner.Id;
        public override EndPoint? LocalEndPoint => _inner.LocalEndPoint;
        public override EndPoint? RemoteEndPoint => _inner.RemoteEndPoint;
        public override PipeReader Input => _inner.Input;
        public override PipeWriter Output => _writer;
        public override ConnectionDirection Direction => _inner.Direction;
        public override ConnectionCapabilities Capabilities => _inner.Capabilities;
        public override ConnectionState State => _inner.State;
        public override CancellationToken ConnectionClosed => _inner.ConnectionClosed;
        public override void Abort(Exception? reason = null) => _inner.Abort(reason);
        public override ValueTask DisposeAsync() => _inner.DisposeAsync();
    }

    private sealed class DecoratingListener : ConnectionListener
    {
        private readonly ResponseBackpressureTransport _owner;

        public DecoratingListener(ResponseBackpressureTransport owner) => _owner = owner;

        public override EndPoint EndPoint => _owner.Connections.EndPoint;
        public override ConnectionCapabilities Capabilities => _owner.Connections.Capabilities;
        public override ValueTask BindAsync(CancellationToken cancellationToken = default)
            => _owner.Connections.BindAsync(cancellationToken);
        public override async ValueTask<Connection> AcceptAsync(CancellationToken cancellationToken = default)
            => _owner.Decorate(await _owner.Connections.AcceptAsync(cancellationToken).ConfigureAwait(false));
        public override ValueTask DisposeAsync() => _owner.Connections.DisposeAsync();
    }

    private sealed class DecoratingMultiplexedListener : MultiplexedConnectionListener
    {
        private readonly ResponseBackpressureTransport _owner;

        public DecoratingMultiplexedListener(ResponseBackpressureTransport owner) => _owner = owner;

        public override EndPoint EndPoint => _owner.MultiplexedConnections.EndPoint;
        public override ConnectionCapabilities Capabilities => _owner.MultiplexedConnections.Capabilities;
        public override ValueTask BindAsync(CancellationToken cancellationToken = default)
            => _owner.MultiplexedConnections.BindAsync(cancellationToken);
        public override async ValueTask<MultiplexedConnection> AcceptAsync(CancellationToken cancellationToken = default)
            => new ResponseMultiplexedConnection(_owner,
                await _owner.MultiplexedConnections.AcceptAsync(cancellationToken).ConfigureAwait(false));
        public override ValueTask DisposeAsync() => _owner.MultiplexedConnections.DisposeAsync();
    }

    private sealed class ResponseMultiplexedConnection : MultiplexedConnection
    {
        private readonly ResponseBackpressureTransport _owner;
        private readonly MultiplexedConnection _inner;

        public ResponseMultiplexedConnection(ResponseBackpressureTransport owner, MultiplexedConnection inner)
        {
            _owner = owner;
            _inner = inner;
        }

        protected override IConnectionFeatureCollection InnerFeatures => _inner.Features;

        public override ConnectionId Id => _inner.Id;
        public override EndPoint? LocalEndPoint => _inner.LocalEndPoint;
        public override EndPoint? RemoteEndPoint => _inner.RemoteEndPoint;
        public override ConnectionCapabilities Capabilities => _inner.Capabilities;
        public override ConnectionState State => _inner.State;
        public override CancellationToken ConnectionClosed => _inner.ConnectionClosed;
        public override void Abort(Exception? reason = null) => _inner.Abort(reason);
        public override ValueTask DisposeAsync() => _inner.DisposeAsync();
        public override ValueTask<Connection> OpenStreamAsync(
            ConnectionDirection direction = ConnectionDirection.Bidirectional,
            CancellationToken cancellationToken = default) => _inner.OpenStreamAsync(direction, cancellationToken);

        public override async ValueTask<Connection> AcceptStreamAsync(CancellationToken cancellationToken = default)
        {
            Connection stream = await _inner.AcceptStreamAsync(cancellationToken).ConfigureAwait(false);
            return stream.Direction == ConnectionDirection.Bidirectional ? _owner.Decorate(stream) : stream;
        }
    }
}
