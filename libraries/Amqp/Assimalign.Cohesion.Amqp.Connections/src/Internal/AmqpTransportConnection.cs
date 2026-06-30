using System;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Amqp.Connections.Internal;

/// <summary>
/// Shared base for AMQP connections layered over a carrier: caches the opened context,
/// negotiates the protocol header when configured, and signals the owning transport on disposal.
/// Carrier acquisition and teardown are modeled explicitly per carrier kind by the derived types.
/// </summary>
internal abstract class AmqpTransportConnection : AmqpConnection
{
    private readonly AmqpTransportOptions _options;
    private AmqpTransportConnectionContext? _context;

    protected AmqpTransportConnection(AmqpTransportOptions options)
    {
        _options = options;
    }

    internal Action? OnDispose { get; set; }

    public sealed override AmqpConnectionContext Open()
    {
        return OpenAsync().ConfigureAwait(false).GetAwaiter().GetResult();
    }

    public sealed override async ValueTask<AmqpConnectionContext> OpenAsync(CancellationToken cancellationToken = default)
    {
        if (_context is not null)
        {
            return _context;
        }

        IConnection carrier = await AcquireCarrierAsync(cancellationToken).ConfigureAwait(false);
        AmqpTransportConnectionContext context = new(carrier, _options);

        if (_options.AutoNegotiateProtocolHeader)
        {
            await context.NegotiateAsync(cancellationToken).ConfigureAwait(false);
        }

        _context = context;

        return context;
    }

    public sealed override async ValueTask DisposeAsync()
    {
        await DisposeCarrierAsync().ConfigureAwait(false);
        OnDispose?.Invoke();
    }

    /// <summary>
    /// Acquires the carrier connection that backs the AMQP connection context. A single-stream
    /// carrier is already live; a multiplexed carrier opens or accepts its AMQP stream here.
    /// </summary>
    protected abstract ValueTask<IConnection> AcquireCarrierAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Disposes the carrier resources owned by this AMQP connection.
    /// </summary>
    protected abstract ValueTask DisposeCarrierAsync();
}
