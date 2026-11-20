using System;

namespace Assimalign.Cohesion.Amqp.Connections.Internal;

/// <summary>
/// The establishment and framing settings an AMQP listener or factory reads from its options once, when
/// it is created, so later changes to the options object cannot reach connections already in flight.
/// </summary>
internal sealed class AmqpConnectionSettings
{
    public AmqpConnectionSettings(
        AmqpProtocolHeader initialProtocolHeader,
        bool autoNegotiateProtocolHeader,
        uint maxFrameSize,
        TimeSpan handshakeTimeout)
    {
        InitialProtocolHeader = initialProtocolHeader;
        AutoNegotiateProtocolHeader = autoNegotiateProtocolHeader;
        MaxFrameSize = maxFrameSize;
        HandshakeTimeout = handshakeTimeout;
    }

    /// <summary>
    /// Gets the longest handshake timeout a cancellation timer accepts: <see cref="System.Threading.CancellationTokenSource.CancelAfter(TimeSpan)"/>
    /// refuses a delay above <see cref="uint.MaxValue"/> - 1 milliseconds (about 49.7 days).
    /// </summary>
    public static TimeSpan MaxHandshakeTimeout { get; } = TimeSpan.FromMilliseconds(uint.MaxValue - 1d);

    /// <summary>
    /// Gets the protocol header the connection sends for its first protocol phase.
    /// </summary>
    public AmqpProtocolHeader InitialProtocolHeader { get; }

    /// <summary>
    /// Gets a value indicating whether establishment exchanges the protocol headers before the connection is handed out.
    /// </summary>
    public bool AutoNegotiateProtocolHeader { get; }

    /// <summary>
    /// Gets the largest frame the connection sends, in bytes.
    /// </summary>
    public uint MaxFrameSize { get; }

    /// <summary>
    /// Gets how long establishment may take; a non-positive value disables the limit.
    /// </summary>
    public TimeSpan HandshakeTimeout { get; }
}
