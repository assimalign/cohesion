using System;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Amqp.Connections.Internal;

/// <summary>
/// An AMQP connection over a single-stream carrier: the live <see cref="IDuplexConnection"/>
/// itself is the carrier of the AMQP byte stream, so acquisition has no open step.
/// </summary>
[global::System.Diagnostics.DebuggerDisplay("AmqpSingleStreamTransportConnection {Id,nq}")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
internal sealed class AmqpSingleStreamTransportConnection : AmqpTransportConnection
{
    private readonly IDuplexConnection _connection;

    public AmqpSingleStreamTransportConnection(IDuplexConnection connection, AmqpTransportOptions options)
        : base(options)
    {
        _connection = connection;
    }

    public override ConnectionId Id => _connection.Id;

    public override ConnectionState State => _connection.State;

    public override CancellationToken ConnectionClosed => _connection.ConnectionClosed;

    public override void Abort(Exception? reason = null)
    {
        _connection.Abort(reason);
    }

    protected override ValueTask<IDuplexConnection> AcquireCarrierAsync(CancellationToken cancellationToken)
    {
        return ValueTask.FromResult(_connection);
    }

    protected override ValueTask DisposeCarrierAsync()
    {
        return _connection.DisposeAsync();
    }

    private sealed class DebugView
    {
        private readonly AmqpSingleStreamTransportConnection _target;

        public DebugView(AmqpSingleStreamTransportConnection value)
        {
            _target = value;
        }

        public ConnectionId Id => _target.Id;
        public ConnectionState State => _target.State;
        public CancellationToken ConnectionClosed => _target.ConnectionClosed;
    }
}
