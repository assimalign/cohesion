using System;
using System.Threading;

namespace Assimalign.Cohesion.Http.Connections.Internal.Http1;

/// <summary>
/// Drives the two-phase timeout that reclaims idle keep-alive and slow-header (Slowloris)
/// HTTP/1.1 connections. A single <see cref="CancellationTokenSource"/>, linked to the ambient
/// connection token, is armed with the keep-alive timeout while the transport waits for the next
/// request to begin, re-armed with the request-headers timeout once the first request byte
/// arrives, and disarmed once the header section has been fully received (so the body read is not
/// bounded by the header timeout).
/// </summary>
/// <remarks>
/// <para>
/// Phase transitions are signalled by the message reader: <see cref="OnRequestLineStarted"/> when
/// the first byte of the request line is read, and <see cref="OnHeadReceived"/> after the blank
/// line terminating the header section. A timer expiry cancels <see cref="Token"/>; the connection
/// loop distinguishes that from a genuine shutdown via <see cref="TimedOut"/>.
/// </para>
/// <para>
/// A timeout of <see cref="Timeout.InfiniteTimeSpan"/> disables that phase's deadline.
/// </para>
/// </remarks>
internal sealed class Http1ReadTimeout : IDisposable
{
    private readonly CancellationToken _connectionToken;
    private readonly CancellationTokenSource _cts;
    private readonly TimeSpan _requestHeadersTimeout;

    /// <summary>
    /// Initializes the controller and arms the keep-alive (idle-wait) deadline.
    /// </summary>
    /// <param name="connectionToken">The ambient connection/shutdown token to link to.</param>
    /// <param name="keepAliveTimeout">The idle-wait budget before the next request begins.</param>
    /// <param name="requestHeadersTimeout">The budget to complete the header section once bytes flow.</param>
    public Http1ReadTimeout(CancellationToken connectionToken, TimeSpan keepAliveTimeout, TimeSpan requestHeadersTimeout)
    {
        _connectionToken = connectionToken;
        _requestHeadersTimeout = requestHeadersTimeout;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(connectionToken);
        Arm(keepAliveTimeout);
    }

    /// <summary>
    /// Gets the token to pass to every read on the connection stream. Cancelled when the current
    /// phase's deadline elapses (or when the ambient connection token is cancelled).
    /// </summary>
    public CancellationToken Token => _cts.Token;

    /// <summary>
    /// Gets a value indicating whether the transport has entered the header-reading phase (the
    /// first request byte has been observed). Distinguishes a slow-header timeout from an idle
    /// keep-alive timeout so the connection loop can decide whether to emit a 408 response.
    /// </summary>
    public bool IsHeadersPhase { get; private set; }

    /// <summary>
    /// Gets a value indicating whether the timeout &#8212; rather than the ambient connection
    /// token &#8212; is what fired. True only when this controller's deadline elapsed and the
    /// connection is not being shut down.
    /// </summary>
    public bool TimedOut => _cts.IsCancellationRequested && !_connectionToken.IsCancellationRequested;

    /// <summary>
    /// Transitions from the keep-alive idle wait to the request-headers deadline. Invoked by the
    /// reader when the first byte of the request line is read. Idempotent.
    /// </summary>
    public void OnRequestLineStarted()
    {
        if (IsHeadersPhase)
        {
            return;
        }

        IsHeadersPhase = true;
        Arm(_requestHeadersTimeout);
    }

    /// <summary>
    /// Disarms the head-read deadline once the full header section has been received, so the
    /// subsequent body read is bounded only by the ambient connection token.
    /// </summary>
    public void OnHeadReceived()
    {
        Disarm();
    }

    private void Arm(TimeSpan timeout)
    {
        // CancelAfter(InfiniteTimeSpan) disables the timer; a positive timeout (re)schedules it.
        _cts.CancelAfter(timeout == Timeout.InfiniteTimeSpan ? Timeout.InfiniteTimeSpan : timeout);
    }

    private void Disarm()
    {
        _cts.CancelAfter(Timeout.InfiniteTimeSpan);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _cts.Dispose();
    }
}
