using System;
using System.Threading;

namespace Assimalign.Cohesion.Http.Connections.Internal;

/// <summary>
/// The connection-level deadline of an HTTP/2 connection (#1085): the keep-alive deadline while the
/// connection carries no stream, and the request-headers deadline while a field block is arriving. One
/// <see cref="CancellationTokenSource"/>, linked to the frame pump's token, bounds every inbound frame
/// read; the deadline it carries moves as the connection's state changes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Keep-alive.</b> The connection is idle from the moment it is accepted until its first stream
/// opens, and again whenever its last stream leaves the stream table and the last exchange that kept a
/// slot after its stream was reset has ended. While idle, the deadline is
/// <see cref="HttpConnectionListenerLimits.KeepAliveTimeout"/> after the later of the connection's
/// acceptance and the end of its last exchange (<see cref="OnExchangeLeft"/>). A stream that never
/// became an exchange — one refused, reset for a malformed head, or rejected by a request-parse
/// interceptor — makes the connection busy while it lasts but does not restart the deadline, so a peer
/// cannot keep an idle connection open with such streams. Frames the peer sends meanwhile (PING,
/// SETTINGS, WINDOW_UPDATE) do not move it either: an idle connection is reclaimed however chatty its
/// peer is. The connection preface and the client's first SETTINGS frame arrive under the same deadline.
/// </para>
/// <para>
/// <b>Request headers.</b> A field block — a request head or a trailer section, a HEADERS frame and its
/// CONTINUATION frames — holds the whole connection until it ends (RFC 9113 §6.10), so it is bounded by
/// <see cref="HttpConnectionListenerLimits.RequestHeadersTimeout"/> from the moment the HEADERS frame's
/// header arrives until END_HEADERS.
/// </para>
/// <para>
/// <b>Which deadline fired.</b> The pump distinguishes a deadline from a shutdown through
/// <see cref="TimedOut"/>, and the deadline that fired through <see cref="RequestHeadersTimedOut"/>. A
/// deadline can fire while the pump processes a frame rather than while it reads one: the frame's
/// HPACK decode, the request-parse interceptors and the writes they cause all run between reads. The
/// next <see cref="Token"/> read settles it. When the deadline the connection now carries has elapsed,
/// it is the one that fired, and the read fails with it. When it has not — the field block ended in
/// time, or the connection became busy — the cancelled source is replaced by a fresh one and the read
/// goes ahead. A read the deadline cancels while it waits may have consumed part of a frame, so the
/// connection always ends after one; while a read waits only the pump could change which deadline is
/// carried, so the one carried then is the one that fired.
/// </para>
/// <para>
/// The idle state is changed under the connection's synchronization root, from the frame pump and from
/// the threads that end exchanges; the header-block state and the source only by the pump. Both meet
/// under this object's own lock, which is always taken after the connection's.
/// </para>
/// </remarks>
internal sealed class Http2ConnectionTimeout : IDisposable
{
    private readonly Lock _gate = new();
    private readonly CancellationToken _pumpToken;
    private readonly TimeProvider _timeProvider;
    private readonly long _keepAliveTicks;
    private readonly long _requestHeadersTicks;

    // Guarded by _gate.
    private CancellationTokenSource _source;
    private bool _busy;
    private long _idleSince;
    private bool _headerBlockOpen;
    private long _headerBlockStart;
    private Deadline _armed;
    private long _armedAt;
    // The deadline a read found had cancelled the source while the pump processed a frame, once the
    // Token getter settled that it still applies. None until then.
    private Deadline _fired;
    private bool _disposed;

    /// <summary>
    /// Initializes the deadline and arms the keep-alive deadline: the connection is idle until its first
    /// stream opens.
    /// </summary>
    /// <param name="keepAliveTimeout">How long the connection may carry no stream, or <see cref="Timeout.InfiniteTimeSpan"/>.</param>
    /// <param name="requestHeadersTimeout">How long a field block may take to arrive, or <see cref="Timeout.InfiniteTimeSpan"/>.</param>
    /// <param name="timeProvider">The monotonic clock the deadlines are measured on.</param>
    /// <param name="pumpToken">The frame pump's token, which the deadline's token is linked to.</param>
    public Http2ConnectionTimeout(TimeSpan keepAliveTimeout, TimeSpan requestHeadersTimeout, TimeProvider timeProvider, CancellationToken pumpToken)
    {
        _timeProvider = timeProvider;
        _pumpToken = pumpToken;
        _keepAliveTicks = ToTicks(keepAliveTimeout, timeProvider);
        _requestHeadersTicks = ToTicks(requestHeadersTimeout, timeProvider);
        _source = CancellationTokenSource.CreateLinkedTokenSource(pumpToken);
        _idleSince = timeProvider.GetTimestamp();

        lock (_gate)
        {
            RearmLocked();
        }
    }

    /// <summary>
    /// Gets the token the next inbound read on the connection passes. Cancelled when the deadline elapses
    /// or the pump stops. Read by the pump before each read: a source a deadline cancelled while the pump
    /// was processing a frame is kept only when that deadline still applies, and replaced otherwise.
    /// </summary>
    public CancellationToken Token
    {
        get
        {
            lock (_gate)
            {
                if (!_disposed
                    && _fired == Deadline.None
                    && _source.IsCancellationRequested
                    && !_pumpToken.IsCancellationRequested)
                {
                    if (HasArmedDeadlineElapsedLocked())
                    {
                        _fired = _armed;
                    }
                    else
                    {
                        // The deadline fired for a state the connection has left: the field block ended in
                        // time, or a stream made the connection busy. Nothing read under it, so the
                        // connection reads on under a fresh source carrying the deadline that applies now.
                        CancellationTokenSource stale = _source;
                        _source = CancellationTokenSource.CreateLinkedTokenSource(_pumpToken);
                        _armed = Deadline.None;
                        _armedAt = 0;
                        RearmLocked();
                        stale.Dispose();
                    }
                }

                return _source.Token;
            }
        }
    }

    /// <summary>
    /// Gets a value indicating whether the deadline, rather than the pump's own token, cancelled the read.
    /// </summary>
    public bool TimedOut
    {
        get
        {
            lock (_gate)
            {
                return _source.IsCancellationRequested && !_pumpToken.IsCancellationRequested;
            }
        }
    }

    /// <summary>
    /// Gets a value indicating whether the deadline that cancelled the read was the request-headers
    /// deadline rather than the keep-alive one. Read by the pump once <see cref="TimedOut"/> is
    /// <see langword="true"/>.
    /// </summary>
    public bool RequestHeadersTimedOut
    {
        get
        {
            lock (_gate)
            {
                Deadline fired = _fired != Deadline.None ? _fired : _armed;
                return fired == Deadline.RequestHeaders;
            }
        }
    }

    /// <summary>
    /// Records whether the connection carries a stream or a running exchange. Called under the
    /// connection's synchronization root whenever its stream table or its retired exchange slots change.
    /// Becoming idle arms the keep-alive deadline from the later of the connection's acceptance and the
    /// end of its last exchange; becoming busy clears it.
    /// </summary>
    /// <param name="busy">Whether the connection carries a stream or a running exchange.</param>
    public void SetBusy(bool busy)
    {
        lock (_gate)
        {
            if (_busy == busy)
            {
                return;
            }

            _busy = busy;
            RearmLocked();
        }
    }

    /// <summary>
    /// Records that an exchange the host was handed has left the connection's stream count: its stream
    /// left the table once the exchange finished, or the exchange that kept a retired slot ended. The
    /// keep-alive deadline is measured from here when the connection next becomes idle. Called under the
    /// connection's synchronization root, before the idle state is updated.
    /// </summary>
    public void OnExchangeLeft()
    {
        lock (_gate)
        {
            _idleSince = _timeProvider.GetTimestamp();
            RearmLocked();
        }
    }

    /// <summary>
    /// Records that a field block began to arrive — the header of a HEADERS frame was read — and arms the
    /// request-headers deadline. Has no effect while a block is already open.
    /// </summary>
    public void OnHeaderBlockStarted()
    {
        lock (_gate)
        {
            if (_headerBlockOpen)
            {
                return;
            }

            _headerBlockOpen = true;
            _headerBlockStart = _timeProvider.GetTimestamp();
            RearmLocked();
        }
    }

    /// <summary>
    /// Records that the field block ended (END_HEADERS, or the frame failed), returning the connection to
    /// the keep-alive deadline when it is idle — measured from the end of its last exchange, not from now.
    /// Has no effect while no block is open.
    /// </summary>
    public void OnHeaderBlockEnded()
    {
        lock (_gate)
        {
            if (!_headerBlockOpen)
            {
                return;
            }

            _headerBlockOpen = false;
            RearmLocked();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        CancellationTokenSource source;

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            source = _source;
        }

        source.Dispose();
    }

    private bool HasArmedDeadlineElapsedLocked()
    {
        if (_armed == Deadline.None)
        {
            return false;
        }

        long budget = _armed == Deadline.RequestHeaders ? _requestHeadersTicks : _keepAliveTicks;
        return _timeProvider.GetTimestamp() - _armedAt >= budget;
    }

    private void RearmLocked()
    {
        if (_disposed)
        {
            return;
        }

        Deadline deadline;
        long armedAt;

        if (_headerBlockOpen)
        {
            deadline = _requestHeadersTicks > 0 ? Deadline.RequestHeaders : Deadline.None;
            armedAt = _headerBlockStart;
        }
        else if (!_busy)
        {
            deadline = _keepAliveTicks > 0 ? Deadline.KeepAlive : Deadline.None;
            armedAt = _idleSince;
        }
        else
        {
            deadline = Deadline.None;
            armedAt = 0;
        }

        if (deadline == Deadline.None)
        {
            armedAt = 0;
        }

        if (deadline == _armed && armedAt == _armedAt)
        {
            return;
        }

        _armed = deadline;
        _armedAt = armedAt;

        if (deadline == Deadline.None)
        {
            _source.CancelAfter(Timeout.InfiniteTimeSpan);
            return;
        }

        long budget = deadline == Deadline.RequestHeaders ? _requestHeadersTicks : _keepAliveTicks;
        long remaining = armedAt + budget - _timeProvider.GetTimestamp();

        // CancelAfter accepts at most uint.MaxValue - 1 milliseconds; a longer deadline is effectively
        // none, and an elapsed one fires at once.
        double milliseconds = Math.Max(0, remaining) * 1000.0 / _timeProvider.TimestampFrequency;
        _source.CancelAfter(milliseconds >= uint.MaxValue - 1
            ? Timeout.InfiniteTimeSpan
            : TimeSpan.FromMilliseconds(Math.Ceiling(milliseconds)));
    }

    private static long ToTicks(TimeSpan timeout, TimeProvider timeProvider)
    {
        if (timeout == Timeout.InfiniteTimeSpan)
        {
            return 0;
        }

        double ticks = timeout.TotalSeconds * timeProvider.TimestampFrequency;
        return ticks >= long.MaxValue / 2 ? long.MaxValue / 2 : Math.Max(1, (long)ticks);
    }

    /// <summary>
    /// Which deadline the source currently carries.
    /// </summary>
    private enum Deadline
    {
        None,
        KeepAlive,
        RequestHeaders,
    }
}
