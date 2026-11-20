using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Amqp.Connections.Internal;

/// <summary>
/// Establishes an AMQP connection over an accepted or connected carrier: on a multiplexed carrier it
/// acquires the AMQP stream, then it exchanges the protocol headers when the settings ask for it, all
/// within the handshake timeout.
/// </summary>
/// <remarks>
/// <see cref="AmqpConnectionListener"/> composes it onto its carrier listener with
/// <c>listener.Use(layer, maxConcurrentUpgrades)</c>: each establishment runs on its own task, a bounded
/// number at a time, and one that fails or times out ends only its carrier and is reported through the
/// <c>Assimalign.Cohesion.Connections</c> event source. <see cref="AmqpConnectionFactory"/> composes it
/// onto its carrier factory with <c>factory.Use(layer)</c>, which disposes the carrier when establishment
/// fails. Either way the carrier stays the caller's on failure, as <see cref="IConnectionLayer"/> requires.
/// </remarks>
[global::System.Diagnostics.DebuggerDisplay("AmqpConnectionLayer")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
internal sealed class AmqpConnectionLayer : IConnectionLayer
{
    // A stalled peer can make every handshake time out, so the message is read once, not per failure.
    private static readonly string _handshakeTimedOutFormat = ErrorMessages.HandshakeTimedOut;

    private readonly AmqpConnectionSettings _settings;

    public AmqpConnectionLayer(AmqpConnectionSettings settings)
    {
        _settings = settings;
    }

    // An AMQP connection is its carrier's byte stream: establishing it changes no capability.
    public ConnectionCapabilities Describe(ConnectionCapabilities capabilities)
    {
        return capabilities;
    }

    public ValueTask<IDuplexConnection> UpgradeAsync(IDuplexConnection connection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);

        if (connection is not AmqpMultiplexedCarrier && !_settings.AutoNegotiateProtocolHeader)
        {
            // Nothing to wait for: the connection is ready as soon as it is wrapped.
            return ValueTask.FromResult<IDuplexConnection>(new AmqpCarrierConnection(connection, _settings));
        }

        return EstablishAsync(connection, cancellationToken);
    }

    private async ValueTask<IDuplexConnection> EstablishAsync(IDuplexConnection connection, CancellationToken cancellationToken)
    {
        using CancellationTokenSource handshake = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        if (_settings.HandshakeTimeout > TimeSpan.Zero)
        {
            handshake.CancelAfter(_settings.HandshakeTimeout);
        }

        try
        {
            if (connection is AmqpMultiplexedCarrier multiplexed)
            {
                await multiplexed.AcquireStreamAsync(handshake.Token).ConfigureAwait(false);
            }

            AmqpCarrierConnection amqp = new(connection, _settings);

            if (_settings.AutoNegotiateProtocolHeader)
            {
                await amqp.NegotiateAsync(handshake.Token).ConfigureAwait(false);
            }

            return amqp;
        }
        catch (OperationCanceledException exception) when (handshake.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // The timeout fired, not the caller: report it as a timeout, so neither a factory caller nor
            // the listener's failure event mistakes it for a cancellation someone asked for.
            throw new TimeoutException(
                string.Format(CultureInfo.CurrentCulture, _handshakeTimedOutFormat, _settings.HandshakeTimeout),
                exception);
        }
    }

    private sealed class DebugView
    {
        public DebugView(AmqpConnectionLayer value)
        {
        }
    }
}
