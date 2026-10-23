using System;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Amqp.Connections.Internal;

/// <summary>
/// An AMQP connection over a multiplexed carrier: the AMQP byte stream is a single
/// bidirectional stream of the <see cref="IMultiplexedConnection"/> — opened by the
/// client side and accepted by the server side when the connection context is opened.
/// </summary>
[global::System.Diagnostics.DebuggerDisplay("AmqpMultiplexedTransportConnection {Id,nq}")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
internal sealed class AmqpMultiplexedTransportConnection : AmqpTransportConnection
{
    private readonly IMultiplexedConnection _connection;
    private readonly bool _opensCarrierStream;
    private IDuplexConnection? _stream;

    public AmqpMultiplexedTransportConnection(IMultiplexedConnection connection, bool opensCarrierStream, AmqpTransportOptions options)
        : base(options)
    {
        _connection = connection;
        _opensCarrierStream = opensCarrierStream;
    }

    public override ConnectionId Id => _connection.Id;

    public override ConnectionState State => _connection.State;

    public override CancellationToken ConnectionClosed => _connection.ConnectionClosed;

    public override void Abort(Exception? reason = null)
    {
        _connection.Abort(reason);
    }

    protected override async ValueTask<IDuplexConnection> AcquireCarrierAsync(CancellationToken cancellationToken)
    {
        _stream = _opensCarrierStream
            ? await _connection.OpenStreamAsync(ConnectionDirection.Bidirectional, cancellationToken).ConfigureAwait(false)
            : await _connection.AcceptStreamAsync(cancellationToken).ConfigureAwait(false);

        return _stream;
    }

    protected override async ValueTask DisposeCarrierAsync()
    {
        if (_stream is not null)
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
        }

        await _connection.DisposeAsync().ConfigureAwait(false);
    }

    private sealed class DebugView
    {
        private readonly AmqpMultiplexedTransportConnection _target;

        public DebugView(AmqpMultiplexedTransportConnection value)
        {
            _target = value;
        }

        public ConnectionId Id => _target.Id;
        public ConnectionState State => _target.State;
        public CancellationToken ConnectionClosed => _target.ConnectionClosed;
    }
}
