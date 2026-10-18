using System;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http.Connections.Internal;

/// <summary>
/// Drives the two-phase timeout that reclaims idle keep-alive and slow-header (Slowloris)
/// HTTP/1.1 connections. A <see cref="CancellationTokenSource"/>, linked to the ambient
/// connection token, is created only when a pipe read waits, armed with the keep-alive timeout for the next
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
/// <para>
/// A graceful close ends the idle wait early through <see cref="CancelIdleWait"/>. The phase is an
/// interlocked latch, so that call and <see cref="OnRequestLineStarted"/> cannot both win: either the
/// wait ends as an idle reclaim, or the request whose first octet arrived is read to completion under
/// its request-headers deadline.
/// </para>
/// </remarks>
internal sealed class Http1ReadTimeout : IDisposable
{
    // The _phase values: waiting for the next request to begin, reading its head, or an idle wait a
    // graceful close ended.
    private const int idlePhase = 0;
    private const int headersPhase = 1;
    private const int idleCancelledPhase = 2;

    // Serializes creating the token source with a graceful close cancelling it.
    private readonly object _sync = new();
    private readonly CancellationToken _connectionToken;
    private CancellationTokenSource? _cts;
    private readonly TimeSpan _requestHeadersTimeout;
    private TimeSpan _timeout;
    private long _phaseStarted;
    private bool _headReceived;
    private bool _deadlineElapsed;

    private int _phase;

    /// <summary>
    /// Initializes the controller and starts the keep-alive (idle-wait) deadline.
    /// </summary>
    /// <param name="connectionToken">The ambient connection/shutdown token to link to.</param>
    /// <param name="keepAliveTimeout">The idle-wait budget before the next request begins.</param>
    /// <param name="requestHeadersTimeout">The budget to complete the header section once bytes flow.</param>
    public Http1ReadTimeout(CancellationToken connectionToken, TimeSpan keepAliveTimeout, TimeSpan requestHeadersTimeout)
    {
        _connectionToken = connectionToken;
        _requestHeadersTimeout = requestHeadersTimeout;
        _timeout = keepAliveTimeout;
        _phaseStarted = Stopwatch.GetTimestamp();
    }

    /// <summary>
    /// Gets the token for a pending head read. Cancelled when the current
    /// phase's deadline elapses (or when the ambient connection token is cancelled).
    /// </summary>
    public CancellationToken Token
    {
        get
        {
            // Only a pending read (or cancellation) needs a source. A graceful close can race its
            // creation, so publish and cancel under the same monitor. No lock on buffered reads.
            lock (_sync)
            {
                if (_cts is null)
                {
                    _cts = CancellationTokenSource.CreateLinkedTokenSource(_connectionToken);
                    if (Volatile.Read(ref _phase) == idleCancelledPhase || _deadlineElapsed)
                    {
                        _cts.Cancel();
                    }
                    else if (!_headReceived)
                    {
                        _cts.CancelAfter(GetRemainingTimeout());
                    }
                }
                return _cts.Token;
            }
        }
    }

    /// <summary>
    /// Reads buffered input without creating a timer. Elapsed time still counts from the start of
    /// the current phase; a pending read arms only the remaining allowance, never a fresh deadline.
    /// </summary>
    internal ValueTask<ReadResult> ReadAsync(PipeReader input)
    {
        _connectionToken.ThrowIfCancellationRequested();
        if (_cts is { } source)
        {
            return input.ReadAsync(source.Token);
        }
        if (Volatile.Read(ref _phase) == idleCancelledPhase || GetRemainingTimeout() == TimeSpan.Zero)
        {
            _deadlineElapsed = true;
            return ValueTask.FromCanceled<ReadResult>(Token);
        }
        return input.TryRead(out ReadResult result) ? new ValueTask<ReadResult>(result) : input.ReadAsync(Token);
    }

    /// <summary>
    /// Gets a value indicating whether the transport has entered the header-reading phase (the
    /// first request byte has been observed). Distinguishes a slow-header timeout from an idle
    /// keep-alive timeout so the connection loop can decide whether to emit a 408 response.
    /// </summary>
    public bool IsHeadersPhase => Volatile.Read(ref _phase) == headersPhase;

    /// <summary>
    /// Gets a value indicating whether the timeout &#8212; rather than the ambient connection
    /// token &#8212; is what fired. True only when this controller's deadline elapsed and the
    /// connection is not being shut down.
    /// </summary>
    public bool TimedOut => !_connectionToken.IsCancellationRequested
        && (_deadlineElapsed || Volatile.Read(ref _phase) == idleCancelledPhase || _cts?.IsCancellationRequested == true);

    /// <summary>
    /// Transitions from the keep-alive idle wait to the request-headers deadline. Invoked by the
    /// reader when the first byte of the request line is read. Idempotent.
    /// </summary>
    public void OnRequestLineStarted()
    {
        // Only from the idle wait: a repeated call is a no-op, and a wait a graceful close already ended
        // stays ended (the read observes the cancellation and the connection is reclaimed).
        if (Interlocked.CompareExchange(ref _phase, headersPhase, idlePhase) == idlePhase)
        {
            _phaseStarted = Stopwatch.GetTimestamp();
            _timeout = _requestHeadersTimeout;
            _cts?.CancelAfter(_requestHeadersTimeout);
        }
    }

    /// <summary>
    /// Ends the keep-alive idle wait now, because the connection is closing gracefully and will take
    /// no further request. Does nothing once the next request's head has started to arrive: that
    /// request is read and answered under the request-headers deadline. The cancelled read reports
    /// <see cref="TimedOut"/> outside <see cref="IsHeadersPhase"/>, the idle-reclaim outcome, so no
    /// response is written.
    /// </summary>
    public void CancelIdleWait()
    {
        if (Interlocked.CompareExchange(ref _phase, idleCancelledPhase, idlePhase) == idlePhase)
        {
            lock (_sync)
            {
                _cts?.Cancel();
            }
        }
    }

    /// <summary>
    /// Disarms the head-read deadline once the full header section has been received, so the
    /// subsequent body read is bounded only by the ambient connection token.
    /// </summary>
    public void OnHeadReceived()
    {
        _headReceived = true;
        _cts?.CancelAfter(Timeout.InfiniteTimeSpan);
    }

    private TimeSpan GetRemainingTimeout()
    {
        if (_timeout == Timeout.InfiniteTimeSpan)
        {
            return Timeout.InfiniteTimeSpan;
        }
        TimeSpan remaining = _timeout - Stopwatch.GetElapsedTime(_phaseStarted);
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _cts?.Dispose();
    }
}
