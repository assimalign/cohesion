using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Amqp;

/// <summary>
/// Represents a mapped AMQP session (AMQP 1.0 Part 2, section 2.5): a pair of channels that sequence the
/// transfers of the links attached to it and apply its transfer windows.
/// </summary>
/// <remarks>
/// Either peer attaches links on a session: <see cref="AttachSenderAsync(AmqpSenderOptions, CancellationToken)"/>
/// and <see cref="AttachReceiverAsync(AmqpReceiverOptions, CancellationToken)"/> attach one locally, and
/// <see cref="AcceptLinkAsync(CancellationToken)"/> returns the next link the remote peer is attaching,
/// for the application to accept or refuse. <see cref="IAsyncDisposable.DisposeAsync"/> ends the session
/// without an error.
/// </remarks>
public interface IAmqpSession : IAsyncDisposable
{
    /// <summary>
    /// Gets the connection the session belongs to.
    /// </summary>
    IAmqpConnection Connection { get; }

    /// <summary>
    /// Gets the local channel number of the session.
    /// </summary>
    ushort Channel { get; }

    /// <summary>
    /// Gets the extension capabilities the peer offered in its <c>begin</c>, or <see langword="null"/>.
    /// </summary>
    IReadOnlyList<AmqpSymbol>? RemoteOfferedCapabilities { get; }

    /// <summary>
    /// Gets the extension capabilities the peer desired in its <c>begin</c>, or <see langword="null"/>.
    /// </summary>
    IReadOnlyList<AmqpSymbol>? RemoteDesiredCapabilities { get; }

    /// <summary>
    /// Gets the session properties the peer sent in its <c>begin</c>, or <see langword="null"/>.
    /// </summary>
    IReadOnlyDictionary<AmqpSymbol, object?>? RemoteProperties { get; }

    /// <summary>
    /// Gets a token that is canceled once the session has ended, by either peer or because its connection closed.
    /// </summary>
    CancellationToken SessionEnded { get; }

    /// <summary>
    /// Attaches a sending link: sends <c>attach</c> and waits for the peer's answering <c>attach</c>.
    /// </summary>
    /// <param name="options">The link's name, termini, settlement modes and properties.</param>
    /// <param name="cancellationToken">A token to cancel the wait for the peer's answer.</param>
    /// <returns>The attached sender.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="AmqpException">Thrown with <see cref="AmqpErrorCode.LinkDetached"/> when the peer refuses the link, or with <see cref="AmqpErrorCode.SessionEnded"/> when the session ends first.</exception>
    /// <exception cref="InvalidOperationException">Thrown when every handle the peer accepts is in use.</exception>
    /// <exception cref="OperationCanceledException">Thrown when the wait is canceled. The link is detached once the peer answers.</exception>
    ValueTask<IAmqpSender> AttachSenderAsync(AmqpSenderOptions options, CancellationToken cancellationToken = default);

    /// <summary>
    /// Attaches a receiving link: sends <c>attach</c>, waits for the peer's answering <c>attach</c>, and
    /// grants the link its prefetch credit.
    /// </summary>
    /// <param name="options">The link's name, termini, settlement modes, credit and properties.</param>
    /// <param name="cancellationToken">A token to cancel the wait for the peer's answer.</param>
    /// <returns>The attached receiver.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="AmqpException">Thrown with <see cref="AmqpErrorCode.LinkDetached"/> when the peer refuses the link, or with <see cref="AmqpErrorCode.SessionEnded"/> when the session ends first.</exception>
    /// <exception cref="InvalidOperationException">Thrown when every handle the peer accepts is in use.</exception>
    /// <exception cref="OperationCanceledException">Thrown when the wait is canceled. The link is detached once the peer answers.</exception>
    ValueTask<IAmqpReceiver> AttachReceiverAsync(AmqpReceiverOptions options, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the next link the remote peer is attaching. The peer waits for an answer until the
    /// application accepts or refuses the request.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the wait.</param>
    /// <returns>The next attach request.</returns>
    /// <exception cref="AmqpException">Thrown with <see cref="AmqpErrorCode.SessionEnded"/> once the session has ended and no request is waiting.</exception>
    /// <exception cref="OperationCanceledException">Thrown when the wait is canceled.</exception>
    ValueTask<IAmqpLinkRequest> AcceptLinkAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Ends the session: sends <c>end</c> and waits for the peer's <c>end</c>. Every link on the session
    /// detaches.
    /// </summary>
    /// <param name="error">The error to report to the peer, or <see langword="null"/> for an orderly end.</param>
    /// <param name="cancellationToken">A token that ends the wait for the peer's <c>end</c>.</param>
    /// <returns>A task that completes when the session has ended.</returns>
    ValueTask EndAsync(AmqpError? error = null, CancellationToken cancellationToken = default);
}
