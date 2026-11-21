using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Amqp;

namespace Assimalign.Cohesion.Amqp.Connections.Internal;

/// <summary>
/// The establishment, framing and session settings an AMQP listener or factory reads from its options
/// once, when it is created, so later changes to the options object cannot reach connections in flight.
/// </summary>
internal sealed class AmqpConnectionSettings
{
    /// <summary>
    /// The close timeout used when the handshake timeout is disabled: how long a connection that sent
    /// <c>close</c> waits for the peer's before it releases its carrier anyway.
    /// </summary>
    private static readonly TimeSpan _defaultCloseTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets the longest timeout a cancellation timer accepts: <see cref="System.Threading.CancellationTokenSource.CancelAfter(TimeSpan)"/>
    /// refuses a delay above <see cref="uint.MaxValue"/> - 1 milliseconds (about 49.7 days).
    /// </summary>
    public static TimeSpan MaxTimeout { get; } = TimeSpan.FromMilliseconds(uint.MaxValue - 1d);

    /// <summary>
    /// The shortest period, in milliseconds, of a connection's idle timer: a shorter one would let a peer
    /// that declares a tiny idle timeout spin the timer.
    /// </summary>
    public const long MinTimerPeriod = 50;

    /// <summary>
    /// The shortest idle timeout, in milliseconds, a connection keeps for its peer. A connection sends an
    /// empty frame once it has written nothing for half the peer's idle timeout, and its timer runs no
    /// more often than every <see cref="MinTimerPeriod"/>, so the longest gap between its frames is half
    /// the peer's timeout plus one timer period: within the timeout only from twice the period up. A peer
    /// that declares less is refused, as AMQP 1.0 Part 2, section 2.4.5 allows.
    /// </summary>
    public const uint MinRemoteIdleTimeout = 2 * (uint)MinTimerPeriod;

    /// <summary>
    /// Gets the shortest idle timeout, other than zero, a listener or factory accepts: twice
    /// <see cref="MinRemoteIdleTimeout"/>, so the half a connection declares in its <c>open</c> is one a
    /// connection of this library keeps.
    /// </summary>
    public static TimeSpan MinIdleTimeout { get; } = TimeSpan.FromMilliseconds(2 * MinRemoteIdleTimeout);

    /// <summary>
    /// Gets the container identifier every connection sends in its <c>open</c>.
    /// </summary>
    public required string ContainerId { get; init; }

    /// <summary>
    /// Gets the host name a client names in its <c>open</c> and SASL <c>init</c>, or <see langword="null"/>
    /// to name the endpoint's host.
    /// </summary>
    public string? HostName { get; init; }

    /// <summary>
    /// Gets the largest frame, in bytes, a connection accepts, and the most it sends.
    /// </summary>
    public required uint MaxFrameSize { get; init; }

    /// <summary>
    /// Gets the highest channel number a connection accepts.
    /// </summary>
    public required ushort ChannelMax { get; init; }

    /// <summary>
    /// Gets how long the peer may stay silent before a connection closes; <see cref="TimeSpan.Zero"/>
    /// disables the limit, and any other value is at least <see cref="MinIdleTimeout"/>. A connection
    /// declares half of it in its <c>open</c>, as AMQP 1.0 Part 2, section 2.4.5 recommends, so a peer
    /// that keeps to the declared value never trips it.
    /// </summary>
    public required TimeSpan IdleTimeout { get; init; }

    /// <summary>
    /// Gets how long establishment may take; a non-positive value disables the limit.
    /// </summary>
    public required TimeSpan HandshakeTimeout { get; init; }

    /// <summary>
    /// Gets how long a connection that sent <c>close</c> waits for the peer's: the handshake timeout, or
    /// 30 seconds when that is disabled.
    /// </summary>
    public TimeSpan CloseTimeout => HandshakeTimeout > TimeSpan.Zero ? HandshakeTimeout : _defaultCloseTimeout;

    /// <summary>
    /// Gets the SASL mechanisms a server offers, in order of preference; empty when it does not
    /// authenticate.
    /// </summary>
    public IReadOnlyList<IAmqpSaslServerMechanism> ServerMechanisms { get; init; } = [];

    /// <summary>
    /// Gets the SASL mechanism a client authenticates with, or <see langword="null"/> when it does not.
    /// </summary>
    public IAmqpSaslClientMechanism? ClientMechanism { get; init; }

    /// <summary>
    /// Gets the settings of sessions the peer begins, and of local sessions begun without options.
    /// </summary>
    public required AmqpSessionSettings SessionSettings { get; init; }

    /// <summary>
    /// Gets the capabilities a connection's <c>open</c> offers.
    /// </summary>
    public IReadOnlyList<AmqpSymbol>? OfferedCapabilities { get; init; }

    /// <summary>
    /// Gets the capabilities a connection's <c>open</c> desires.
    /// </summary>
    public IReadOnlyList<AmqpSymbol>? DesiredCapabilities { get; init; }

    /// <summary>
    /// Gets the properties a connection's <c>open</c> carries.
    /// </summary>
    public IReadOnlyDictionary<AmqpSymbol, object?>? Properties { get; init; }

    /// <summary>
    /// Gets the links every connection of a listener that allows link stealing holds, which those connections
    /// share, or <see langword="null"/> when links are not stolen.
    /// </summary>
    public AmqpLinkRegistry? LinkRegistry { get; init; }

    /// <summary>
    /// Creates the container identifier of a listener or factory that was given none.
    /// </summary>
    public static string CreateContainerId()
    {
        return string.Concat("cohesion-", Guid.NewGuid().ToString("N"));
    }

    /// <summary>
    /// Creates the <c>open</c> a connection sends.
    /// </summary>
    /// <param name="hostName">The host name to name, or <see langword="null"/>.</param>
    public AmqpOpenPerformative CreateOpen(string? hostName)
    {
        return new AmqpOpenPerformative
        {
            ContainerId = ContainerId,
            HostName = hostName,
            MaxFrameSize = MaxFrameSize,
            ChannelMax = ChannelMax,
            IdleTimeout = IdleTimeout > TimeSpan.Zero ? (uint)Math.Clamp(IdleTimeout.TotalMilliseconds / 2, 1, uint.MaxValue) : null,
            OfferedCapabilities = OfferedCapabilities,
            DesiredCapabilities = DesiredCapabilities,
            Properties = Properties
        };
    }
}
