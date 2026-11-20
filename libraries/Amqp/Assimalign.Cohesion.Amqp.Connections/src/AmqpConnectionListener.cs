using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Amqp.Connections.Internal;
using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Amqp.Connections;

/// <summary>
/// Accepts AMQP connections over the carrier connections a lower-level listener accepts.
/// </summary>
/// <remarks>
/// <para>
/// The carrier is consumed by capability: its listener must produce reliable, ordered byte streams
/// (<see cref="ConnectionDelivery.Stream"/> with <see cref="ConnectionCapabilities.IsReliable"/> and
/// <see cref="ConnectionCapabilities.IsOrdered"/>). Over a single-stream carrier each accepted
/// <see cref="IDuplexConnection"/> carries one AMQP connection. Over a multiplexed carrier each accepted
/// <see cref="IMultiplexedConnection"/> carries one AMQP connection, on the first stream its peer opens.
/// </para>
/// <para>
/// <see cref="ConnectionListener{TConnection}.AcceptAsync(CancellationToken)"/> returns established
/// connections: a multiplexed carrier's AMQP stream has been accepted and, with
/// <see cref="AmqpConnectionListenerOptions.AutoNegotiateProtocolHeader"/>, the protocol headers have been
/// exchanged. Establishment runs on the Connections layered listener: each connection on its own task, at
/// most <see cref="AmqpConnectionListenerOptions.MaxConcurrentHandshakes"/> at a time, each within
/// <see cref="AmqpConnectionListenerOptions.HandshakeTimeout"/>, and connections are returned in the order
/// their establishment completes. A connection whose establishment fails or times out is disposed and
/// reported through the <c>Assimalign.Cohesion.Connections</c> event source, and the listener keeps
/// accepting; an exception from <c>AcceptAsync</c> means the listener itself can produce no more
/// connections.
/// </para>
/// <para>
/// The listener owns its carrier listener: <see cref="BindAsync(CancellationToken)"/> binds it and
/// <see cref="DisposeAsync"/> disposes it, together with any connection still being established. Disposal
/// does not close connections already handed out; their owners do.
/// </para>
/// </remarks>
[global::System.Diagnostics.DebuggerDisplay("AmqpConnectionListener {Id,nq}")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
public sealed class AmqpConnectionListener : ConnectionListener<IAmqpConnection>, IAmqpConnectionListener
{
    private readonly IDuplexConnectionListener _listener;
    private int _isDisposed;

    private AmqpConnectionListener(IDuplexConnectionListener listener)
    {
        _listener = listener;
    }

    /// <summary>
    /// Gets the carrier listener's identifier: the AMQP listener represents the same bound endpoint.
    /// </summary>
    public override ListenerId Id => _listener.Id;

    /// <inheritdoc />
    public EndPoint EndPoint => _listener.EndPoint;

    /// <inheritdoc />
    /// <remarks>
    /// The carrier listener's capabilities; over a multiplexed carrier, those of one of its streams, so
    /// <see cref="ConnectionCapabilities.IsMultiplexed"/> is <see langword="false"/>.
    /// </remarks>
    public ConnectionCapabilities Capabilities => _listener.Capabilities;

    /// <summary>
    /// Creates an AMQP connection listener over a single-stream carrier listener.
    /// </summary>
    /// <param name="listener">The carrier listener. The AMQP listener takes ownership of it.</param>
    /// <param name="options">The establishment options, read once here; <see langword="null"/> uses the defaults.</param>
    /// <returns>The AMQP connection listener.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="listener"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown when the carrier does not deliver a reliable, ordered byte stream.</exception>
    public static AmqpConnectionListener Create(IDuplexConnectionListener listener, AmqpConnectionListenerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(listener);

        listener.Capabilities.ThrowIfNotAmqpCarrier(nameof(listener));

        return Compose(listener, options);
    }

    /// <summary>
    /// Creates an AMQP connection listener over a multiplexed carrier listener.
    /// </summary>
    /// <param name="listener">The multiplexed carrier listener. The AMQP listener takes ownership of it.</param>
    /// <param name="options">The establishment options, read once here; <see langword="null"/> uses the defaults.</param>
    /// <returns>The AMQP connection listener.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="listener"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">Thrown when the carrier does not deliver a reliable, ordered byte stream.</exception>
    public static AmqpConnectionListener Create(IMultiplexedConnectionListener listener, AmqpConnectionListenerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(listener);

        listener.Capabilities.ThrowIfNotAmqpCarrier(nameof(listener));

        return Compose(new AmqpMultiplexedCarrierListener(listener), options);
    }

    /// <summary>
    /// Binds the carrier listener to its configured local endpoint.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the bind operation.</param>
    /// <returns>A task that completes when the carrier listener is ready to accept connections.</returns>
    /// <exception cref="OperationCanceledException">Thrown when the bind operation is canceled.</exception>
    /// <exception cref="ObjectDisposedException">Thrown when the listener has been disposed.</exception>
    public override ValueTask BindAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _isDisposed) != 0, this);

        return _listener.BindAsync(cancellationToken);
    }

    /// <summary>
    /// Releases the carrier listener and every connection still being established. Connections already
    /// handed out stay open.
    /// </summary>
    /// <returns>A task that completes when the carrier listener and pending connections have been released.</returns>
    public override ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) != 0)
        {
            return ValueTask.CompletedTask;
        }

        return _listener.DisposeAsync();
    }

    /// <inheritdoc />
    /// <exception cref="ObjectDisposedException">Thrown when the listener has been disposed.</exception>
    protected override async ValueTask<IAmqpConnection> AcceptCoreAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _isDisposed) != 0, this);

        // The establishment layer produced every connection the layered listener returns.
        return (IAmqpConnection)await _listener.AcceptAsync(cancellationToken).ConfigureAwait(false);
    }

    private static AmqpConnectionListener Compose(IDuplexConnectionListener carrier, AmqpConnectionListenerOptions? options)
    {
        options ??= new AmqpConnectionListenerOptions();

        AmqpConnectionSettings settings = new(
            options.InitialProtocolHeader,
            options.AutoNegotiateProtocolHeader,
            options.MaxFrameSize,
            options.HandshakeTimeout);

        return new AmqpConnectionListener(carrier.Use(new AmqpConnectionLayer(settings), options.MaxConcurrentHandshakes));
    }

    private sealed class DebugView
    {
        private readonly AmqpConnectionListener _target;

        public DebugView(AmqpConnectionListener value)
        {
            _target = value;
        }

        public ListenerId Id => _target.Id;
        public EndPoint EndPoint => _target.EndPoint;
        public ConnectionCapabilities Capabilities => _target.Capabilities;
        public IReadOnlyCollection<IAmqpConnection> Connections => _target.Connections;
    }
}
