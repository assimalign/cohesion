using System;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Transports;

namespace Assimalign.Cohesion.Amqp.Transports.Internal;

internal sealed class AmqpTransportConnection : AmqpConnection
{
    private readonly ITransportConnection _connection;
    private readonly TransportKind _transportKind;
    private readonly AmqpTransportOptions _options;
    private AmqpTransportConnectionContext? _context;

    public AmqpTransportConnection(
        ITransportConnection connection,
        TransportId transportId,
        TransportKind transportKind,
        AmqpTransportOptions options)
        : base(connection, transportId)
    {
        _connection = connection;
        _transportKind = transportKind;
        _options = options;
    }

    internal Action? OnDispose { get; set; }

    public override AmqpConnectionContext Open()
    {
        return OpenAsync().ConfigureAwait(false).GetAwaiter().GetResult();
    }

    public override async ValueTask<AmqpConnectionContext> OpenAsync(CancellationToken cancellationToken = default)
    {
        if (_context is not null)
        {
            return _context;
        }

        ITransportConnectionContext transportContext = await OpenCarrierContextAsync(cancellationToken).ConfigureAwait(false);
        AmqpTransportConnectionContext context = new(transportContext, _options);

        if (_options.AutoNegotiateProtocolHeader)
        {
            await context.NegotiateAsync(cancellationToken).ConfigureAwait(false);
        }

        _context = context;

        return context;
    }

    public override async ValueTask DisposeAsync()
    {
        await _connection.DisposeAsync().ConfigureAwait(false);
        OnDispose?.Invoke();
    }

    private async ValueTask<ITransportConnectionContext> OpenCarrierContextAsync(CancellationToken cancellationToken)
    {
        return _connection switch
        {
            ISingleStreamTransportConnection singleStreamConnection =>
                await singleStreamConnection.OpenAsync(cancellationToken).ConfigureAwait(false),

            IMultiplexTransportConnection multiplexConnection when _transportKind == TransportKind.Client =>
                await multiplexConnection.OpenOutboundAsync(cancellationToken).ConfigureAwait(false),

            IMultiplexTransportConnection multiplexConnection =>
                await multiplexConnection.OpenInboundAsync(cancellationToken).ConfigureAwait(false),

            _ => throw new InvalidOperationException("The AMQP transport requires a carrier connection that can provide an ordered duplex stream.")
        };
    }
}
