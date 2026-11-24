using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Amqp;

/// <summary>
/// Represents an open AMQP connection (AMQP 1.0 Part 2, section 2.4): the peers have exchanged protocol
/// headers, completed any SASL exchange and exchanged <c>open</c> frames, and the connection carries
/// sessions.
/// </summary>
/// <remarks>
/// <para>
/// A connection is open when its listener or factory returns it. Either peer begins sessions on it:
/// <see cref="BeginSessionAsync(AmqpSessionOptions?, CancellationToken)"/> begins one locally, and
/// <see cref="AcceptSessionAsync(CancellationToken)"/> returns the next session the remote peer began,
/// which the connection has already answered.
/// </para>
/// <para>
/// As an <see cref="IConnection"/> it shares its carrier's identity, endpoints, capabilities and features,
/// so the facts a lower layer registered, such as TLS (<c>connection.Tls</c>), stay visible.
/// <see cref="IConnection.ConnectionClosed"/> is canceled once the connection has closed, by either peer
/// or because its carrier ended. <see cref="IAsyncDisposable.DisposeAsync"/> closes the connection
/// without an error, and <see cref="IConnection.Abort(Exception?)"/> ends it at once, without the
/// <c>close</c> exchange.
/// </para>
/// </remarks>
public interface IAmqpConnection : IConnection
{
    /// <summary>
    /// Gets the local container identifier, which the connection sent in its <c>open</c>.
    /// </summary>
    string ContainerId { get; }

    /// <summary>
    /// Gets the remote container identifier, from the peer's <c>open</c>.
    /// </summary>
    string RemoteContainerId { get; }

    /// <summary>
    /// Gets the host name the peer's <c>open</c> named, or <see langword="null"/>. A client names the
    /// virtual host it wants to reach; a server may use it to select one.
    /// </summary>
    string? RemoteHostName { get; }

    /// <summary>
    /// Gets the largest frame, in bytes, the connection sends: the smaller of the local limit and the
    /// limit in the peer's <c>open</c>.
    /// </summary>
    uint MaxFrameSize { get; }

    /// <summary>
    /// Gets the highest channel number the connection uses: the smaller of the local limit and the limit
    /// in the peer's <c>open</c>.
    /// </summary>
    ushort ChannelMax { get; }

    /// <summary>
    /// Gets the idle timeout the peer's <c>open</c> declared, or <see cref="TimeSpan.Zero"/> when it
    /// declared none. The connection sends an empty frame whenever it has sent nothing for half of it.
    /// </summary>
    TimeSpan RemoteIdleTimeout { get; }

    /// <summary>
    /// Gets the extension capabilities the peer offered in its <c>open</c>, or <see langword="null"/>.
    /// </summary>
    IReadOnlyList<AmqpSymbol>? RemoteOfferedCapabilities { get; }

    /// <summary>
    /// Gets the extension capabilities the peer desired in its <c>open</c>, or <see langword="null"/>.
    /// </summary>
    IReadOnlyList<AmqpSymbol>? RemoteDesiredCapabilities { get; }

    /// <summary>
    /// Gets the connection properties the peer sent in its <c>open</c>, or <see langword="null"/>.
    /// </summary>
    IReadOnlyDictionary<AmqpSymbol, object?>? RemoteProperties { get; }

    /// <summary>
    /// Begins a session: allocates a channel, sends <c>begin</c>, and waits for the peer's answering
    /// <c>begin</c>.
    /// </summary>
    /// <param name="options">The session's flow-control limits and properties; <see langword="null"/> uses the defaults.</param>
    /// <param name="cancellationToken">A token to cancel the wait for the peer's answer.</param>
    /// <returns>The mapped session.</returns>
    /// <exception cref="AmqpException">Thrown with <see cref="AmqpErrorCode.ConnectionClosed"/> when the connection is closed, or with <see cref="AmqpErrorCode.SessionEnded"/> when the peer ends the session instead of answering.</exception>
    /// <exception cref="InvalidOperationException">Thrown when every channel up to <see cref="ChannelMax"/> is in use.</exception>
    /// <exception cref="OperationCanceledException">Thrown when the wait is canceled. The session is ended once the peer answers.</exception>
    ValueTask<IAmqpSession> BeginSessionAsync(AmqpSessionOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the next session the remote peer began. The connection answered its <c>begin</c> when it
    /// arrived, using the session options of the listener or factory.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the wait.</param>
    /// <returns>The next remotely begun session.</returns>
    /// <exception cref="AmqpException">Thrown with <see cref="AmqpErrorCode.ConnectionClosed"/> once the connection is closed and no begun session is waiting.</exception>
    /// <exception cref="OperationCanceledException">Thrown when the wait is canceled.</exception>
    ValueTask<IAmqpSession> AcceptSessionAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Closes the connection: sends <c>close</c>, waits for the peer's <c>close</c>, and releases the
    /// carrier. Every session ends.
    /// </summary>
    /// <param name="error">The error to report to the peer, or <see langword="null"/> for an orderly close.</param>
    /// <param name="cancellationToken">A token that ends the wait for the peer's <c>close</c>; the carrier is released either way.</param>
    /// <returns>A task that completes when the connection is closed.</returns>
    ValueTask CloseAsync(AmqpError? error = null, CancellationToken cancellationToken = default);
}
