using System;
using System.IO.Pipelines;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Amqp.Connections.Internal;

/// <summary>
/// Presents a multiplexed carrier as the one duplex connection AMQP runs over. The AMQP byte stream is a
/// single bidirectional stream of the <see cref="IMultiplexedConnection"/>: the client side opens it and
/// the server side accepts it while <see cref="AmqpConnectionLayer"/> establishes the connection.
/// </summary>
/// <remarks>
/// <para>
/// Only the establishment step sees an instance before its stream is acquired, and its pipe is unusable
/// until then. Its identity, endpoints and features are the multiplexed connection's, and its
/// capabilities are those of one of its streams (<see cref="ConnectionCapabilities.IsMultiplexed"/> is
/// <see langword="false"/>).
/// </para>
/// <para>
/// Its lifetime ends when either the stream or the multiplexed connection ends, so a peer that resets
/// the AMQP stream on a connection it keeps open still ends the AMQP connection. Disposing it disposes
/// the stream and then the multiplexed connection; aborting it aborts the multiplexed connection, which
/// ends every stream.
/// </para>
/// </remarks>
[global::System.Diagnostics.DebuggerDisplay("AmqpMultiplexedCarrier {Id,nq}")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
internal sealed class AmqpMultiplexedCarrier : DuplexConnection
{
    private readonly IMultiplexedConnection _connection;
    private readonly bool _opensStream;
    private readonly CancellationTokenSource _closed;
    private readonly CancellationToken _closedToken;
    private IMultiplexedConnectionStream? _stream;
    private CancellationTokenRegistration _streamClosedRegistration;
    private int _isDisposed;

    public AmqpMultiplexedCarrier(IMultiplexedConnection connection, bool opensStream)
    {
        _connection = connection;
        _opensStream = opensStream;
        _closed = CancellationTokenSource.CreateLinkedTokenSource(connection.ConnectionClosed);

        // Read the token once: it stays valid after the source is disposed, the property does not.
        _closedToken = _closed.Token;
    }

    public override ConnectionId Id => _connection.Id;

    public override EndPoint? LocalEndPoint => _connection.LocalEndPoint;

    public override EndPoint? RemoteEndPoint => _connection.RemoteEndPoint;

    public override ConnectionCapabilities Capabilities => _connection.Capabilities with { IsMultiplexed = false };

    public override ConnectionState State
    {
        get
        {
            ConnectionState state = _connection.State;
            IMultiplexedConnectionStream? stream = Volatile.Read(ref _stream);

            // While the multiplexed connection is open, the AMQP connection lives and ends with its stream.
            return stream is not null && state == ConnectionState.Open ? stream.State : state;
        }
    }

    public override CancellationToken ConnectionClosed => _closedToken;

    public override PipeReader Input => AcquiredStream.Input;

    public override PipeWriter Output => AcquiredStream.Output;

    protected override IConnectionFeatureCollection? InnerFeatures => _connection.Features;

    private IMultiplexedConnectionStream AcquiredStream
        => Volatile.Read(ref _stream) ?? throw new InvalidOperationException(ErrorMessages.CarrierStreamNotAcquired);

    /// <summary>
    /// Opens (client side) or accepts (server side) the bidirectional stream that carries the AMQP byte stream.
    /// </summary>
    /// <param name="cancellationToken">The establishment's token, which carries its handshake timeout.</param>
    /// <exception cref="AmqpProtocolException">Thrown when the stream the peer opened is not bidirectional.</exception>
    public async ValueTask AcquireStreamAsync(CancellationToken cancellationToken)
    {
        IMultiplexedConnectionStream stream = _opensStream
            ? await _connection.OpenStreamAsync(ConnectionDirection.Bidirectional, cancellationToken).ConfigureAwait(false)
            : await _connection.AcceptStreamAsync(cancellationToken).ConfigureAwait(false);

        // Held before it is checked, so a refused stream is released with the carrier.
        Volatile.Write(ref _stream, stream);

        _streamClosedRegistration = stream.ConnectionClosed.UnsafeRegister(
            static state => ((AmqpMultiplexedCarrier)state!).OnStreamClosed(),
            this);

        if (stream.Direction != ConnectionDirection.Bidirectional)
        {
            // AMQP needs both directions; a unidirectional first stream cannot carry the connection.
            throw new AmqpProtocolException(ErrorMessages.CarrierStreamNotBidirectional);
        }
    }

    public override void Abort(Exception? reason = null)
    {
        _connection.Abort(reason);
    }

    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) != 0)
        {
            return;
        }

        try
        {
            IMultiplexedConnectionStream? stream = Volatile.Read(ref _stream);

            if (stream is not null)
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            try
            {
                await _connection.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                // Unregister before the source goes away, so a late stream callback cannot cancel a
                // disposed source. Disposal ends the lifetime even if a carrier did not signal it.
                _streamClosedRegistration.Dispose();
                _closed.Cancel();
                _closed.Dispose();
            }
        }
    }

    private void OnStreamClosed()
    {
        _closed.Cancel();
    }

    private sealed class DebugView
    {
        private readonly AmqpMultiplexedCarrier _target;

        public DebugView(AmqpMultiplexedCarrier value)
        {
            _target = value;
        }

        public ConnectionId Id => _target.Id;
        public EndPoint? LocalEndPoint => _target.LocalEndPoint;
        public EndPoint? RemoteEndPoint => _target.RemoteEndPoint;
        public ConnectionCapabilities Capabilities => _target.Capabilities;
        public ConnectionState State => _target.State;
        public CancellationToken ConnectionClosed => _target.ConnectionClosed;
    }
}
