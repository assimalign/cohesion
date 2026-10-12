using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Quic;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Connections;

namespace Assimalign.Cohesion.Http.Connections.Internal;

/// <summary>
/// The lazy, forward-only HTTP/3 request-body stream. The request is dispatched as soon as its HEADERS
/// frame decodes; this stream then decodes the rest of the request stream on demand (RFC 9114 §4.1) —
/// DATA payloads copied straight into the reader's buffer, a trailing HEADERS frame surfaced as the
/// request's trailers, frames of unknown or reserved type skipped (RFC 9114 §9) — while enforcing the
/// per-request body-size cap (413) and the Content-Length rule (RFC 9114 §4.1.2).
/// </summary>
/// <remarks>
/// <para>
/// <b>Backpressure.</b> Nothing is read until the application reads. Octets it has not asked for stay in
/// the QUIC stream's receive buffer, so the peer is paced by QUIC flow control (RFC 9000 §4); beyond what
/// the application consumed, the server holds at most one read buffer of the stream's input pipe.
/// </para>
/// <para>
/// <b>Body-size cap.</b> The cap freezes at the first read, exactly as on HTTP/1.1: the shared
/// interceptor pipeline hands this stream the request's parse context
/// (<see cref="IHttpLazyRequestBody"/>) instead of freezing the knob after the head hooks, so request
/// hooks, middleware, and endpoints may raise or lower it until the body is first read. A
/// Content-Length over the frozen cap is rejected before a DATA octet is read; otherwise each DATA frame
/// is checked when its header arrives, before any of its octets are delivered. A rejection is recorded
/// (<see cref="RejectedStatusCode"/>) and thrown as <see cref="Http3LimitExceededException"/>; the
/// send path answers 413 when the response head has not been committed and then stops reading, and
/// resets the stream with <c>H3_REQUEST_CANCELLED</c> when a streamed response head has been (#1084).
/// </para>
/// <para>
/// <b>Trailer-section size.</b> A trailer section that decodes past
/// <c>SETTINGS_MAX_FIELD_SECTION_SIZE</c> (RFC 9114 §4.2.2) is rejected the same way, answered 431,
/// while the response head is uncommitted; after that it resets the stream with
/// <c>H3_MESSAGE_ERROR</c>.
/// </para>
/// <para>
/// <b>Minimum data rate (#1085).</b> With a <see cref="HttpConnectionListenerLimits.MinRequestBodyDataRate"/>,
/// or the rate the request set for itself before the first read (#1383, frozen then like the cap),
/// a <see cref="MinDataRateGate"/> started at the first read bounds every read by what is left of the
/// peer's allowance and charges the time the read waited; DATA octets delivered extend it, as on
/// HTTP/1.1. A read the allowance runs out on stops the request stream with
/// <c>STOP_SENDING(H3_NO_ERROR)</c> — RFC 9114 §4.1's signal that the server needs no more of the
/// request and will answer it — and is rejected with <c>408</c> (RFC 9110 §15.5.9), recorded like an
/// over-cap body, so the send path answers <c>408</c> while the response head is uncommitted. A CONNECT
/// tunnel's DATA is not a request body and may idle, so it is not held to the rate.
/// </para>
/// <para>
/// <b><c>Expect: 100-continue</c> (#1385).</b> A request that declared the expectation, and whose stream
/// had not ended with its head, has its content solicited at the first read, as HTTP/1.1 does: once the
/// cap and the rate are resolved, <c>100 Continue</c> goes out as an interim HEADERS frame on the request
/// stream, unless the final response has started, so a handler that answers without reading the body never
/// invites it. The <c>100</c> and the final head take the request stream's writer in turn
/// (<see cref="Http3Context.TryBeginInterimAsync"/>), and the write runs inside the read's error handling.
/// Once the exchange exists, the write is held to the response's minimum data rate (#1384): a peer that
/// grants it no stream credit has the request stream reset with <c>H3_REQUEST_CANCELLED</c>, and the read
/// throws the exchange's cancellation.
/// </para>
/// <para>
/// <b>Errors.</b> A malformed request detected in the body (a Content-Length the DATA frames contradict,
/// a malformed trailer section) is an <c>H3_MESSAGE_ERROR</c> stream error (RFC 9114 §4.1.2); an
/// oversized trailer section an <c>H3_FRAME_ERROR</c> stream error; either resets the request stream
/// (<see cref="IsReset"/>). A truncated frame or an invalid frame sequence is a connection error
/// (RFC 9114 §7.1 / §4.1) signalled to the connection context, which aborts the connection and with it
/// this stream. The first failure is recorded and rethrown by every later read. All failures surface as
/// <see cref="IOException"/> subtypes, so an application observes them as ordinary body-read failures.
/// A rejection, a stream error and a connection error found in the body are the client's fault, latched
/// as <see cref="ClientFaultStatusCode"/> before the stream is answered, reset or aborted, which the
/// exchange control reports (#1378).
/// </para>
/// <para>
/// <b>A client that abandons its request.</b> A peer <c>RESET_STREAM</c> a read meets, or a
/// <c>STOP_SENDING</c> the <c>100 Continue</c>'s write meets, is how a client cancels a request
/// (RFC 9114 §4.1.1), not a body that failed. Once the exchange exists, the read fires its
/// <see cref="HttpContext.RequestCancelled"/> and throws an <see cref="OperationCanceledException"/>,
/// as an HTTP/2 <c>RST_STREAM</c> does, so a host classifies the exchange as cancelled rather than as an
/// application fault (#1378). Before dispatch, a request hook's read reports it as a stream failure.
/// Loss of the whole connection after dispatch has the same cancellation contract (#1430); pending
/// and later reads carry the exchange's exact token, including reads inside trailers or QPACK waits.
/// An already reported client-body fault retains its original exception and status.
/// </para>
/// <para>
/// <b>Ownership.</b> The stream does not own the request stream; disposal only bars further reads. In
/// particular it never completes the input pipe: completing it stops the request stream's receiving
/// direction, which is the send path's decision once the response is done, not the application's. The
/// send path refuses the rest of an unread request before the response's FIN unless its FIN has already
/// arrived (<see cref="RefuseRemainder"/>), and releases the input once the complete response is on the
/// wire (<see cref="StopReading"/>). The FIN ends only the response direction (#1330), so the request
/// stream stays readable until then — after an extended CONNECT tunnel's server side has ended, too.
/// </para>
/// <para>
/// <b>CONNECT.</b> For a CONNECT request the DATA frames carry tunnel octets rather than a message body
/// (RFC 9110 §9.3.6, RFC 9114 §4.4), so neither the body-size cap nor the Content-Length rule applies,
/// and a HEADERS frame after the request head is an invalid frame sequence.
/// </para>
/// </remarks>
internal sealed class Http3RequestBodyStream : Stream, IHttpLazyRequestBody
{
    private readonly Http3ConnectionContext _connection;
    private readonly Http3RequestStreamReader _reader;
    private readonly IMultiplexedStream _streamConnection;
    // Resolve only when a read or end probe needs it, so a known FIN creates no feature collection.
    private IMultiplexedStreamAbort? _directionalAbort;
    private bool _directionalAbortResolved;
    private readonly long _streamId;
    private readonly HttpTrailerCollection _trailers;
    private readonly long? _declaredContentLength;
    private readonly bool _isTunnel;
    private readonly long? _fallbackCap;
    private readonly int _maxHeadersFrameSize;
    private readonly HttpMinDataRate? _fallbackRate;
    private readonly TimeProvider _timeProvider;
    private readonly Lock _gate = new();

    private HttpExchangeInterceptorRequestContext? _interception;
    // The exchange this body belongs to, once it exists: an oversized trailer section asks it whether
    // the response has started. Null while a request hook reads the body before dispatch.
    private Http3Context? _owner;
    // Whether the first read still has to solicit the content with 100 Continue (#1385). Read-path only.
    private bool _solicitContinue;
    private CancellationToken _requestAborted;
    private bool _started;
    private long? _cap;
    private long _received;
    // #1085 — the minimum data-rate gate, started at the first read of a body (never a tunnel's) when a
    // rate is configured. Read-path only, like the frame progress below.
    private MinDataRateGate? _rateGate;
    // The rate the gate enforces: the request's own (#1383) when a parse context is attached, else the
    // listener's; resolved, and the context's knob frozen, at the first read.
    private HttpMinDataRate? _rate;

    // Frame progress lives in fields, not locals, so a read cancelled mid-frame resumes exactly where it
    // stopped: _dataRemaining counts the current DATA frame's undelivered octets, _skipRemaining an
    // ignored frame's undiscarded octets.
    private long _dataRemaining;
    private long _skipRemaining;
    private bool _trailersReceived;
    private bool _endDelivered;
    private ExceptionDispatchInfo? _failure;
    // The client fault's status (#1378, ClientFaultStatusCode), 0 while there is none. Written once by the
    // read path, before the stream is answered or reset; read by the exchange control from the handler.
    private int _clientFaultStatusCode;

    // Guarded by _gate. _reading marks an in-flight application read; _probing the transport's end-of-stream
    // probe (RefuseRemainder), the only other reader of the input pipe; _closed bars any further application
    // read (the remainder was refused, reading was stopped, or the stream was reset); _stopSent records that
    // the remainder was refused with STOP_SENDING(H3_NO_ERROR); _stopRequested asks for the input pipe to be
    // completed as soon as no read or probe is in flight; and _inputReleased records that it has been — by
    // this stream, or by the reset that aborted it.
    private bool _reading;
    private bool _probing;
    private bool _closed;
    private bool _stopSent;
    private bool _stopRequested;
    private bool _inputReleased;
    private bool _disposed;

    /// <summary>
    /// Initializes the request body for one request stream, positioned just after its HEADERS frame.
    /// </summary>
    /// <param name="connection">The owning connection context (trailer decoding, stream and connection errors).</param>
    /// <param name="reader">The request stream's frame reader, positioned after the request head.</param>
    /// <param name="streamConnection">The request stream.</param>
    /// <param name="streamId">The request stream's wire ID (keys QPACK decoder instructions).</param>
    /// <param name="resetState">The reset claim shared with head processing and response cleanup.</param>
    /// <param name="trailers">The supported, initially empty trailer collection surfaced on the request.</param>
    /// <param name="declaredContentLength">The request's Content-Length, or <see langword="null"/> when absent.</param>
    /// <param name="isTunnel">Whether the request is a CONNECT, whose DATA frames carry tunnel octets.</param>
    /// <param name="fallbackCap">The registration's body-size cap, used when no parse context is attached.</param>
    /// <param name="maxHeadersFrameSize">
    /// The largest encoded trailer HEADERS payload, in octets, the server buffers
    /// (<see cref="Http3ConnectionListenerOptions.Http3Limits.MaxRequestHeadersFrameSize"/>). The decoded size is
    /// bounded separately, by <see cref="Http3QPackOptions.MaxFieldSectionSize"/>.
    /// </param>
    /// <param name="requestAborted">
    /// The token that cancels reads before the exchange exists (connection teardown during the request
    /// head); replaced by the exchange's <see cref="HttpContext.RequestCancelled"/> once attached.
    /// </param>
    /// <param name="minDataRate">
    /// The listener's minimum rate the body must arrive at once read (#1085), or <see langword="null"/> for
    /// none, used when no parse context is attached; with one, the context's knob, frozen at the first
    /// read, applies instead (#1383). Not applied to a tunnel.
    /// </param>
    /// <param name="timeProvider">The monotonic clock the rate is measured on; the system clock when <see langword="null"/>.</param>
    /// <param name="solicitContinue">
    /// Whether the request declared <c>Expect: 100-continue</c> and its stream had not ended with its head,
    /// so the first read solicits the content with <c>100 Continue</c> (RFC 9110 §10.1.1, #1385).
    /// </param>
    public Http3RequestBodyStream(
        Http3ConnectionContext connection,
        Http3RequestStreamReader reader,
        IMultiplexedStream streamConnection,
        long streamId,
        Http3RequestResetState resetState,
        HttpTrailerCollection trailers,
        long? declaredContentLength,
        bool isTunnel,
        long? fallbackCap,
        int maxHeadersFrameSize,
        CancellationToken requestAborted,
        HttpMinDataRate? minDataRate = null,
        TimeProvider? timeProvider = null,
        bool solicitContinue = false)
    {
        _solicitContinue = solicitContinue;
        _connection = connection;
        _reader = reader;
        _streamConnection = streamConnection;
        _streamId = streamId;
        ResetState = resetState;
        _trailers = trailers;
        _declaredContentLength = declaredContentLength;
        _isTunnel = isTunnel;
        _fallbackCap = fallbackCap;
        _maxHeadersFrameSize = maxHeadersFrameSize;
        _requestAborted = requestAborted;
        _fallbackRate = minDataRate;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Gets the status the exchange is answered with because the request body was rejected — 413 when it
    /// exceeded the body-size cap, 408 when it arrived below the minimum data rate (#1085), 431 when its
    /// trailer section exceeded <c>SETTINGS_MAX_FIELD_SECTION_SIZE</c> before the response started — or
    /// <see langword="null"/> when it was not rejected.
    /// </summary>
    public HttpStatusCode? RejectedStatusCode { get; private set; }

    /// <summary>
    /// Gets the status the request body was rejected with because the client was at fault (#1378), or
    /// <see langword="null"/> while it was not: the <see cref="RejectedStatusCode"/> the send path answers
    /// with, or, for a body the transport found malformed — content that contradicts its Content-Length, a
    /// malformed trailer section — and so reset the stream for (RFC 9114 §4.1.2), the <c>400</c> HTTP/1.1
    /// answers a malformed body with; a trailer section over <c>SETTINGS_MAX_FIELD_SECTION_SIZE</c> found
    /// after the response started reports <c>431</c>. Set before the stream is reset, and not changed after.
    /// </summary>
    /// <remarks>
    /// A connection error found in the body — an invalid frame sequence, a prohibited frame type, a
    /// truncated frame, a trailer section that corrupts the QPACK state — is reported as <c>400</c> too: the
    /// octets are the client's whatever the error's scope. Only the exchange whose read found it reports it;
    /// every other exchange on the connection observes the loss of the connection.
    /// </remarks>
    public HttpStatusCode? ClientFaultStatusCode =>
        Volatile.Read(ref _clientFaultStatusCode) is var statusCode and not 0 ? new HttpStatusCode(statusCode) : null;

    /// <summary>
    /// Gets a value indicating whether the transport has reset this request stream — a stream error found
    /// while reading the body, a connection error found there (which aborts every stream of the
    /// connection), a cancelled exchange, or a request that was never dispatched — so no response can be
    /// written on it.
    /// </summary>
    public bool IsReset { get; private set; }

    /// <summary>The reset claim shared before and after the exchange is constructed.</summary>
    internal Http3RequestResetState ResetState { get; }

    /// <summary>
    /// Gets a value indicating whether the request stream ended cleanly: the peer's FIN was read — by a body
    /// read, or by the end-of-stream probe when the response ends (<see cref="RefuseRemainder"/>) — so no
    /// further frame can arrive.
    /// </summary>
    public bool IsCompleted => _reader.IsCompleted;

    /// <inheritdoc />
    public override bool CanRead => !_disposed;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override bool CanWrite => false;

    /// <inheritdoc />
    public override long Length => throw new NotSupportedException("The HTTP/3 request body length is not known in advance.");

    /// <inheritdoc />
    public override long Position
    {
        get => throw new NotSupportedException("The HTTP/3 request body stream is not seekable.");
        set => throw new NotSupportedException("The HTTP/3 request body stream is not seekable.");
    }

    /// <inheritdoc />
    public void AttachInterception(HttpExchangeInterceptorRequestContext interception)
    {
        _interception = interception;
    }

    /// <summary>
    /// Attaches the exchange this body belongs to, so a read in flight is cancelled with the exchange
    /// (<see cref="HttpContext.RequestCancelled"/>) and an oversized trailer section can tell whether a
    /// 431 can still be sent. Called once, when the exchange is constructed.
    /// </summary>
    /// <param name="owner">The owning exchange.</param>
    public void AttachOwner(Http3Context owner)
    {
        _owner = owner;
        _requestAborted = owner.RequestCancelled;
    }

    /// <inheritdoc />
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_gate)
        {
            if (_reading)
            {
                throw new InvalidOperationException("The HTTP/3 request body does not support concurrent reads.");
            }

            _reading = true;
        }

        try
        {
            return await ReadCoreAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            EndRead();
        }
    }

    /// <inheritdoc />
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        ValidateBufferArguments(buffer, offset, count);
        return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count)
    {
        ValidateBufferArguments(buffer, offset, count);
        return ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();
    }

    /// <inheritdoc />
    public override void Flush()
    {
        // A request body is read-only; there is nothing to flush.
    }

    /// <inheritdoc />
    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException("The HTTP/3 request body stream is not seekable.");

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException("The HTTP/3 request body stream is read-only.");

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException("The HTTP/3 request body stream is read-only.");

    /// <summary>
    /// Refuses what remains of the request once the server no longer needs it — its response body is
    /// flushed (on the streamed path, before its trailers and final flush), or its tunnel is done: the peer
    /// is asked to stop sending with <c>STOP_SENDING(H3_NO_ERROR)</c> (RFC 9114 §4.1, which permits stopping
    /// the request before the response completes), and no application read follows. Called before the
    /// response's FIN, in RFC 9114 §4.1's order: abort reading, finish the response, then end the sending
    /// direction. Without it, releasing the input would stop an unread request with the driver's default
    /// code instead — a code .NET's <c>HttpClient</c> reports as a failed request even after a complete
    /// response. A body read to its end, a request whose FIN has already arrived, or a reset stream needs no
    /// signal. Idempotent.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A request the handler never read to its end — every GET, whose body it has no reason to read — may
    /// still have ended: its FIN is often already buffered. So when no read is in flight and the stream sits
    /// on a frame boundary, the remainder is first probed for the end of the stream, and a FIN found without
    /// waiting ends the request cleanly: no <c>STOP_SENDING</c>, and, with the QPACK dynamic table enabled,
    /// no Stream Cancellation (RFC 9204 §4.4.2) when reading stops. A probe that would have to wait is
    /// failed by the stop itself, and the input is released once it has ended.
    /// </para>
    /// <para>
    /// The stop is sent at once, even while a read is in flight (a handler that leaked its reader past the
    /// response): aborting the stream's receiving direction fails that read rather than completing its pipe
    /// underneath it. The input pipe itself is released later, by <see cref="StopReading"/>. On a stream that
    /// cannot carry a code the call only bars further reads (a pending probe is cancelled), and the driver
    /// stops the peer with its default code when the input is released.
    /// </para>
    /// </remarks>
    public void RefuseRemainder()
    {
        if (_reader.IsCompleted || IsReset)
        {
            return;
        }

        bool probe;

        lock (_gate)
        {
            if (_stopSent)
            {
                return;
            }

            _closed = true;
            _stopSent = true;

            // The probe reads one frame header, so it runs only where the stream is at a frame boundary this
            // body tracks: no read in flight, no frame partly delivered or skipped, and no failed read that
            // left the position unknown (a read cancelled inside the trailer section, a rejected frame).
            probe = !_reading
                && !_inputReleased
                && _failure is null
                && _dataRemaining == 0
                && _skipRemaining == 0;
            _probing = probe;
        }

        if (probe && ProbeEndOfStream())
        {
            return;
        }

        DirectionalAbort?.AbortRead((long)Http3ErrorCode.NoError);
    }

    /// <summary>
    /// Stops reading the request stream once the complete response is on the wire. When the body was not
    /// read to its end, the peer is asked to stop sending with <c>H3_NO_ERROR</c> — the
    /// <c>STOP_SENDING</c> RFC 9114 §4.1 asks for when a server no longer needs the rest of a request it
    /// has fully answered (see <see cref="RefuseRemainder"/>) — and the input is completed. A read still in
    /// flight (a handler that leaked its reader past the response) completes the input itself when it
    /// returns, so the pipe is never completed underneath an active read. Idempotent.
    /// </summary>
    public void StopReading()
    {
        bool release;

        lock (_gate)
        {
            if (_stopRequested)
            {
                return;
            }

            _closed = true;
            _stopRequested = true;
            release = !_reading && !_probing && !_inputReleased;
            _inputReleased |= release;
        }

        if (release)
        {
            CompleteInput();
        }
    }

    /// <summary>
    /// Records that the send path reset this request stream (the exchange was cancelled, or the request
    /// was never dispatched), so no later read touches the aborted pipe.
    /// </summary>
    public void MarkReset()
    {
        Fail(new IOException("The HTTP/3 request stream was reset before its body was read."));

        lock (_gate)
        {
            // The reset aborts the stream, which completes the input pipe underneath this body.
            _closed = true;
            _stopRequested = true;
            _inputReleased = true;
        }

        IsReset = true;
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        // The request stream belongs to the exchange, not to this body: disposal only bars further reads
        // and must never complete the input pipe, which would stop the request stream's receiving direction
        // with the driver's default code. The send path stops reading once the response is complete.
        _disposed = true;
        base.Dispose(disposing);
    }

    private IMultiplexedStreamAbort? DirectionalAbort
    {
        get
        {
            if (!Volatile.Read(ref _directionalAbortResolved))
            {
                lock (_gate)
                {
                    if (!_directionalAbortResolved)
                    {
                        _directionalAbort = _streamConnection.DirectionalAbort;
                        Volatile.Write(ref _directionalAbortResolved, true);
                    }
                }
            }

            return _directionalAbort;
        }
    }

    private async ValueTask<int> ReadCoreAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        if (ClientFaultStatusCode is not null)
        {
            _failure?.Throw();
        }

        ThrowIfExchangeCancelled();
        _failure?.Throw();

        if (_endDelivered)
        {
            return 0;
        }

        if (Volatile.Read(ref _closed))
        {
            // The send path refused the rest of the request, or stopped reading, once the response was
            // complete: the body's remainder was never delivered, so this is not an end of body.
            throw new IOException("The HTTP/3 request body is no longer readable: the server stopped reading the request stream after the response completed.");
        }

        EnsureStarted();

        if (buffer.IsEmpty)
        {
            return 0;
        }

        // #1085 — the read may wait for the peer no longer than what is left of its allowance. Where the
        // stream carries a code, the deadline stops it with STOP_SENDING(H3_NO_ERROR), which fails the read
        // (RFC 9114 §4.1: the server needs no more of the request and will answer it); elsewhere it
        // cancels the read.
        CancellationTokenSource? rateDeadline = null;
        CancellationTokenRegistration stopOnDeadline = default;
        long rateStart = 0;
        int delivered = 0;

        if (_rateGate is not null)
        {
            if (!_rateGate.TryGetOperationTimeout(out TimeSpan allowance))
            {
                throw RejectTooSlow();
            }

            rateDeadline = new CancellationTokenSource(allowance, _timeProvider);

            if (DirectionalAbort is not null)
            {
                stopOnDeadline = rateDeadline.Token.UnsafeRegister(
                    static state => ((IMultiplexedStreamAbort)state!).AbortRead((long)Http3ErrorCode.NoError),
                    DirectionalAbort);
            }

            rateStart = _timeProvider.GetTimestamp();
        }

        (CancellationToken readToken, CancellationTokenSource? linked) = LinkAbort(
            cancellationToken,
            rateDeadline is not null && DirectionalAbort is null ? rateDeadline.Token : default);

        try
        {
            // RFC 9110 §10.1.1 — the client declared Expect: 100-continue and withholds the content until it
            // is solicited. The first read solicits it, as HTTP/1.1 does (#1385), after the cap and the rate
            // are resolved, so a Content-Length over the cap is refused (413) rather than invited, and a
            // handler that answers without reading the body never invites it onto the wire. It is written
            // under the read's token and inside its error handling, so a write the client's STOP_SENDING
            // fails reports what a read would. The rate deadline above stays out of that token on a driver
            // that stops the stream with a code, so the write is held to the response's minimum data rate
            // instead (#1384): a peer that grants it no credit has the stream reset.
            if (_solicitContinue)
            {
                _solicitContinue = false;
                await SolicitContinueAsync(readToken).ConfigureAwait(false);
            }

            while (true)
            {
                if (_dataRemaining > 0)
                {
                    int toRead = (int)Math.Min(buffer.Length, _dataRemaining);
                    int read = await _reader.ReadDataAsync(buffer[..toRead], readToken).ConfigureAwait(false);
                    ThrowIfExchangeCancelled();
                    _dataRemaining -= read;
                    _received += read;
                    delivered = read;
                    return read;
                }

                if (_skipRemaining > 0)
                {
                    _skipRemaining -= await _reader.SkipAvailableAsync(_skipRemaining, readToken).ConfigureAwait(false);
                    continue;
                }

                Http3FrameHeader? next = await _reader.ReadFrameHeaderAsync(readToken).ConfigureAwait(false);
                ThrowIfExchangeCancelled();
                if (next is not { } frame)
                {
                    EnsureContentLengthSatisfied();
                    _endDelivered = true;
                    return 0;
                }

                await ProcessFrameAsync(frame, readToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (_owner is { } owner
            && (exception is OperationCanceledException || IsStreamFailure(exception))
            && (_requestAborted.IsCancellationRequested || _connection.ConnectionClosed.IsCancellationRequested))
        {
            // A connection error elsewhere cancels this exchange, not its body. Preserve any rejection
            // already recorded before the close, then expose the exact exchange token (#1430).
            if (ClientFaultStatusCode is not null)
            {
                _failure?.Throw();
                Fail(exception);
                throw;
            }

            OperationCanceledException cancelled = owner.AbortRequestForPeer(exception);
            Fail(cancelled);
            throw cancelled;
        }
        catch (Http3StreamException exception)
        {
            // RFC 9114 §4.1.2 / §7.1 — a malformed request (or an oversized trailer section) found in the
            // body is a stream error: reset this request stream; the connection keeps serving.
            ResetStream(exception);
            throw;
        }
        catch (Http3ConnectionException exception)
        {
            // RFC 9114 §7.1 / §4.1 — a truncated frame, an invalid frame sequence, or a frame type a request
            // stream may not carry: a connection error.
            FailConnection(exception);
            throw;
        }
        catch (QPackException exception)
        {
            // RFC 9204 §2.2 — a trailer section that cannot be decompressed corrupts the shared decoder
            // state: a connection error.
            Http3ConnectionException error = new(exception.ErrorCode, exception.Message, exception);
            FailConnection(error);
            throw error;
        }
        catch (Http3LimitExceededException)
        {
            // Recorded by Reject; the send path answers 413 (or 431) and stops reading.
            throw;
        }
        catch (Exception exception) when (rateDeadline is { IsCancellationRequested: true }
            && (exception is OperationCanceledException || IsStreamFailure(exception))
            && !cancellationToken.IsCancellationRequested
            && !_requestAborted.IsCancellationRequested)
        {
            // #1085 — the allowance ran out while the read waited: the deadline stopped the stream (or
            // cancelled the read). RFC 9110 §15.5.9 — recorded as 408, which the send path answers.
            throw RejectTooSlow();
        }
        catch (OperationCanceledException)
        {
            // The caller, or the exchange, cancelled the read. Nothing was consumed past a frame boundary
            // the stream has already accounted for, so the body stays readable.
            throw;
        }
        catch (Exception exception) when (IsStreamFailure(exception) && _owner is { } owner && IsPeerAbort(exception))
        {
            // RFC 9114 §4.1.1 — the client abandoned its own request with RESET_STREAM (or refused the response,
            // the 100 Continue's write included, with STOP_SENDING). That is a cancellation,
            // as an HTTP/2 RST_STREAM is (#1378), not a body that failed: the exchange's RequestCancelled fires
            // before the read throws, so a host's fault boundary sees a cancelled exchange, never an
            // application fault. The stream's own closure fires it too, but on the QUIC driver only later.
            OperationCanceledException cancelled = owner.AbortRequestForPeer(exception);
            Fail(cancelled);
            throw cancelled;
        }
        catch (Exception exception) when (IsStreamFailure(exception))
        {
            // The request stream itself failed — reset by the peer before the exchange existed, or torn down
            // with its connection.
            IOException failure = exception as IOException
                ?? new IOException("The HTTP/3 request stream failed while the request body was being read.", exception);
            Fail(failure);

            if (ReferenceEquals(failure, exception))
            {
                throw;
            }

            throw failure;
        }
        finally
        {
            linked?.Dispose();

            if (rateDeadline is not null)
            {
                stopOnDeadline.Dispose();
                _rateGate!.Record(_timeProvider.GetTimestamp() - rateStart, delivered);

                // The deadline fired just as the read completed: the stream is already stopped, so the
                // next read is the one that reports the 408 — unless the read delivered the body's end.
                if (rateDeadline.IsCancellationRequested && _failure is null && !_endDelivered)
                {
                    RejectTooSlow();
                }

                rateDeadline.Dispose();
            }
        }
    }

    private async ValueTask ProcessFrameAsync(Http3FrameHeader frame, CancellationToken cancellationToken)
    {
        switch (frame.Type)
        {
            case (long)Http3FrameType.Data:
                if (_trailersReceived)
                {
                    throw new Http3ConnectionException(
                        Http3ErrorCode.UnexpectedFrame,
                        "An HTTP/3 request stream carried a DATA frame after its trailer section (RFC 9114 §4.1).");
                }

                // The frame header is consumed, so its payload is next on the stream whether or not it is
                // delivered.
                _dataRemaining = frame.Length;

                if (!_isTunnel)
                {
                    long total = _received + frame.Length;

                    if (_declaredContentLength is { } declared && total > declared)
                    {
                        throw new Http3StreamException(
                            Http3ErrorCode.MessageError,
                            $"The request's DATA frames exceed its Content-Length of {declared} octets (RFC 9114 §4.1.2).");
                    }

                    if (_cap is { } cap && total > cap)
                    {
                        // RFC 9110 §15.5.14 — reject before a single octet of the offending frame is delivered.
                        throw Reject($"The request body exceeds the configured maximum request body size ({cap} octets) at a DATA frame of {frame.Length} octets after {_received} octets.");
                    }
                }

                return;

            case (long)Http3FrameType.Headers:
                if (_isTunnel || _trailersReceived)
                {
                    // RFC 9114 §4.1 — at most one trailing HEADERS frame; §4.4 — a CONNECT stream carries
                    // only DATA after the request head.
                    throw new Http3ConnectionException(
                        Http3ErrorCode.UnexpectedFrame,
                        "An HTTP/3 request stream carried a HEADERS frame where none is permitted (RFC 9114 §4.1 / §4.4).");
                }

                await ReadTrailerSectionAsync(frame, cancellationToken).ConfigureAwait(false);
                return;

            default:
                if (Http3RequestStreamReader.IsProhibitedOnRequestStream(frame.Type))
                {
                    throw new Http3ConnectionException(
                        Http3ErrorCode.UnexpectedFrame,
                        $"Frame type 0x{frame.Type:x} is not permitted on an HTTP/3 request stream (RFC 9114 §7.2).");
                }

                // RFC 9114 §9 — frames of unknown or reserved type are ignored wherever they appear. The
                // payload is discarded incrementally by the read loop, never buffered.
                _skipRemaining = frame.Length;
                return;
        }
    }

    private async ValueTask ReadTrailerSectionAsync(Http3FrameHeader frame, CancellationToken cancellationToken)
    {
        List<(string Name, string Value)> fields;

        try
        {
            byte[] fieldSection = await _reader.ReadFieldSectionAsync(frame, _maxHeadersFrameSize, cancellationToken).ConfigureAwait(false);
            fields = await _connection.DecodeFieldSectionAsync(fieldSection, _streamId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception)
        {
            // The trailing HEADERS frame's header is already consumed, so a read cancelled inside its field
            // section cannot resume. Exchange cancellation is normalized by the outer read; only a
            // caller-only cancellation leaves an unreadable body rather than a cancelled exchange.
            if (_owner is null || (!_requestAborted.IsCancellationRequested && !_connection.ConnectionClosed.IsCancellationRequested))
            {
                Fail(new IOException("A read was cancelled inside the request's trailer section; the request body can no longer be read.", exception));
            }
            throw;
        }
        catch (Http3LimitExceededException exception)
        {
            // RFC 9114 §4.2.2 — the trailer section decodes past SETTINGS_MAX_FIELD_SECTION_SIZE. Its frame was
            // read whole, so the stream sits on a frame boundary. While the response head is uncommitted the
            // exchange is answered 431, as an over-cap body is answered 413. Once the head is on the wire no
            // status can follow, so the section is treated as malformed (RFC 9114 §10.5.1): an
            // H3_MESSAGE_ERROR stream error.
            if (_owner is { HasFinalResponseStarted: true })
            {
                // Still the client's fault, reported as the 431 it would have been answered with (#1378).
                LatchClientFault(exception.StatusCode);
                throw new Http3StreamException(Http3ErrorCode.MessageError, exception.Message, exception);
            }

            Reject(exception);
            throw;
        }

        try
        {
            Http3HeaderCodec.AddTrailers(fields, _trailers);
        }
        catch (InvalidDataException exception)
        {
            throw new Http3StreamException(Http3ErrorCode.MessageError, exception.Message, exception);
        }

        _trailersReceived = true;
    }

    private void EnsureStarted()
    {
        if (_started)
        {
            return;
        }

        _started = true;

        // The transport is now consuming the body: freeze the per-request cap (idempotent) and resolve the
        // value enforced for the rest of the exchange — the HTTP/1.1 freeze-at-first-read contract.
        _interception?.FreezeMaxRequestBodySize();
        _interception?.FreezeMinRequestBodyDataRate();

        if (_isTunnel)
        {
            // RFC 9110 §9.3.6 — a CONNECT's DATA frames are tunnel traffic, not a message body.
            return;
        }

        // #1085 — the peer's allowance starts with the first read, as on HTTP/1.1, under the rate the request
        // ended up with (#1383): an endpoint may have lowered, raised, or disabled the listener's.
        _rate = _interception is not null ? _interception.MinRequestBodyDataRate : _fallbackRate;
        if (_rate is not null)
        {
            _rateGate = new MinDataRateGate(_rate, _timeProvider);
        }

        _cap = _interception is not null ? _interception.MaxRequestBodySize : _fallbackCap;

        if (_cap is { } cap && _declaredContentLength is { } declared && declared > cap)
        {
            throw Reject($"Content-Length value '{declared}' exceeds the configured maximum request body size ({cap} octets).");
        }
    }

    /// <summary>
    /// Sends <c>100 Continue</c> (RFC 9110 §10.1.1) as a HEADERS frame on the request stream, ahead of the
    /// final response (RFC 9114 §4.1), through the connection's interim-response path — unless an interim
    /// response can no longer precede the final one: the final response has started, the exchange was
    /// cancelled, or the stream was reset or is no longer read. Once the exchange exists, the write is held
    /// to the response's minimum data rate (#1384, <see cref="Http3ConnectionContext.WriteContinueAsync"/>).
    /// </summary>
    /// <remarks>
    /// A full-duplex handler can start its streamed response on one task while another makes this read, so
    /// the <c>100</c> takes the request stream's writer from the exchange first
    /// (<see cref="Http3Context.TryBeginInterimAsync"/>): it is dropped once the final head has claimed the writer,
    /// and a final head that claims it while the <c>100</c> is being written waits for it. Without an exchange
    /// — a request hook reading the body before dispatch — no response can be under way.
    /// </remarks>
    private async ValueTask SolicitContinueAsync(CancellationToken cancellationToken)
    {
        Http3Context? owner = _owner;

        if (IsReset
            || Volatile.Read(ref _closed)
            || owner is { HasFinalResponseStarted: true } or { CancelRequested: true }
            || (owner is not null && !await owner.TryBeginInterimAsync(cancellationToken).ConfigureAwait(false)))
        {
            return;
        }

        try
        {
            await _connection.WriteContinueAsync(owner, _streamConnection, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            owner?.EndInterim();
        }
    }

    private void EnsureContentLengthSatisfied()
    {
        if (!_isTunnel && _declaredContentLength is { } declared && _received != declared)
        {
            throw new Http3StreamException(
                Http3ErrorCode.MessageError,
                $"The request stream ended after {_received} of the {declared} octets its Content-Length declared (RFC 9114 §4.1.2).");
        }
    }

    private Http3LimitExceededException Reject(string message)
    {
        return Reject(new Http3LimitExceededException(HttpStatusCode.RequestEntityTooLarge, message));
    }

    private Http3LimitExceededException RejectTooSlow()
    {
        // RFC 9110 §15.5.9 — a body received below the minimum data rate is answered 408 Request Timeout.
        return Reject(new Http3LimitExceededException(
            HttpStatusCode.RequestTimeout,
            $"The request body was received below the configured minimum data rate of {_rate!.BytesPerSecond} octets per second."));
    }

    private Http3LimitExceededException Reject(Http3LimitExceededException rejection)
    {
        RejectedStatusCode = rejection.StatusCode;
        LatchClientFault(rejection.StatusCode);
        Fail(rejection);
        return rejection;
    }

    private void ResetStream(Http3StreamException error)
    {
        // RFC 9114 §4.1.2 — every stream error the body finds is in the client's octets: a malformed
        // request, reported as the 400 HTTP/1.1 answers one with (#1378), latched before the reset fires
        // the exchange's cancellation.
        LatchClientFault(HttpStatusCode.BadRequest);
        Fail(error);

        lock (_gate)
        {
            // The reset aborts the stream, which completes the input pipe underneath this body.
            _closed = true;
            _stopRequested = true;
            _inputReleased = true;
        }

        IsReset = true;
        _connection.ResetRequestStream(_streamConnection, _streamId, ResetState, error, abandonsReading: !_reader.IsCompleted);
    }

    /// <summary>
    /// Raises a connection error the body found (RFC 9114 §8): the octets that caused it are the client's
    /// whatever the error's scope, so it is latched as the <c>400</c> HTTP/1.1 answers a malformed request
    /// with (#1378) before the abort fires the exchange's cancellation. The abort tears down every stream of
    /// the connection, this one included, so the stream counts as reset: the send path writes nothing on it
    /// and records the client fault's status.
    /// </summary>
    private void FailConnection(Http3ConnectionException error)
    {
        LatchClientFault(HttpStatusCode.BadRequest);
        Fail(error);

        lock (_gate)
        {
            // The abort completes the input pipe underneath this body.
            _closed = true;
            _stopRequested = true;
            _inputReleased = true;
        }

        IsReset = true;
        _connection.AbortConnection(error);
    }

    /// <summary>
    /// Whether a failed read is the client abandoning its request (RFC 9114 §4.1.1): a <c>RESET_STREAM</c> a
    /// read met, or a <c>STOP_SENDING</c> the <c>100 Continue</c>'s write met. The QUIC driver reports either
    /// as <see cref="QuicError.StreamAborted"/>, the in-memory driver as a
    /// <see cref="ConnectionResetException"/> that carries the code. A stream this server reset or stopped
    /// reading and a body already rejected for the client's fault are not. Native connection failures
    /// also abandon the exchange, even before a closure callback fires (#1430).
    /// </summary>
    private bool IsPeerAbort(Exception exception)
    {
        if (IsReset
            || Volatile.Read(ref _closed)
            || Volatile.Read(ref _clientFaultStatusCode) != 0
            || _connection.ConnectionClosed.IsCancellationRequested)
        {
            return false;
        }

        return exception is ConnectionResetException
            || (exception is QuicException quic
                && (OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
                && quic.QuicError is QuicError.StreamAborted or QuicError.ConnectionAborted
                    or QuicError.ConnectionIdle or QuicError.ConnectionTimeout or QuicError.TransportError);
    }

    private void ThrowIfExchangeCancelled()
    {
        if (_owner is not { } owner)
        {
            return;
        }

        _requestAborted.ThrowIfCancellationRequested();
        if (_connection.ConnectionClosed.IsCancellationRequested)
        {
            // The connection's callback can wake a read before it reaches the exchange's linked source.
            throw owner.AbortRequestForPeer(new OperationCanceledException(_connection.ConnectionClosed));
        }
    }

    private void Fail(Exception failure)
    {
        _failure ??= ExceptionDispatchInfo.Capture(failure);
    }

    /// <summary>
    /// Latches the client fault's status (<see cref="ClientFaultStatusCode"/>) unless one already is: the
    /// first fault stands for the rest of the exchange.
    /// </summary>
    private void LatchClientFault(HttpStatusCode statusCode)
    {
        if (Interlocked.CompareExchange(ref _clientFaultStatusCode, statusCode.Value, 0) == 0)
        {
            _connection.ReportRequestRejected(_streamId, statusCode);
        }
    }

    private void EndRead()
    {
        bool release;

        lock (_gate)
        {
            _reading = false;
            release = _stopRequested && !_probing && !_inputReleased;
            _inputReleased |= release;
        }

        if (release)
        {
            CompleteInput();
        }
    }

    /// <summary>
    /// Looks for the end of the request stream without waiting for it (see <see cref="RefuseRemainder"/>).
    /// When the peer's FIN is already buffered the probe completes at once with no frame, and the stream is
    /// marked completed. Otherwise it is left pending: the caller's stop fails it on a stream that carries a
    /// code, and it is cancelled on one that cannot, so it never waits for the peer.
    /// </summary>
    /// <returns><see langword="true"/> when the request had already ended, so there is nothing to refuse.</returns>
    private bool ProbeEndOfStream()
    {
        // A known FIN needs neither a pending read nor a directional-abort feature. The caller
        // established the frame boundary and excluded concurrent reads before starting this probe.
        if (_reader.TryCompleteAtFrameBoundary())
        {
            EndProbe();
            return true;
        }

        // Cancelling a pending read on the QUIC driver would stop the stream with the driver's default code, so
        // a stream that carries a code is stopped with H3_NO_ERROR instead, which fails the read.
        CancellationTokenSource? cancellation = DirectionalAbort is not null ? null : new CancellationTokenSource();
        ValueTask<Http3FrameHeader?> pending = _reader.ReadFrameHeaderAsync(cancellation?.Token ?? CancellationToken.None);

        if (!pending.IsCompleted)
        {
            cancellation?.Cancel();
            _ = AwaitProbeAsync(pending, cancellation);
            return false;
        }

        bool ended;

        try
        {
            ended = pending.Result is null;
        }
        catch (Exception exception) when (exception is OperationCanceledException || IsStreamFailure(exception))
        {
            // The stream failed, or ended inside a frame header: there is no clean end to report.
            ended = false;
        }

        cancellation?.Dispose();
        EndProbe();
        return ended;
    }

    private async Task AwaitProbeAsync(ValueTask<Http3FrameHeader?> pending, CancellationTokenSource? cancellation)
    {
        try
        {
            await pending.ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException || IsStreamFailure(exception))
        {
            // The stop (or the cancellation) failed the probe, or the stream ended underneath it.
        }
        finally
        {
            cancellation?.Dispose();
            EndProbe();
        }
    }

    private void EndProbe()
    {
        bool release;

        lock (_gate)
        {
            _probing = false;
            release = _stopRequested && !_reading && !_inputReleased;
            _inputReleased |= release;
        }

        if (release)
        {
            CompleteInput();
        }
    }

    private void CompleteInput()
    {
        // RFC 9114 §4.1 — H3_NO_ERROR asks the peer to stop sending the rest of a request the server has
        // fully answered. A body read to its end needs no signal. The code reaches the wire through the
        // stream's code-carrying abort, which a path that did not refuse the remainder before the FIN
        // sends now; the reason then only ends the pipe, or carries the code on a stream without one.
        Exception? reason = null;

        if (!_reader.IsCompleted)
        {
            DirectionalAbort?.AbortRead((long)Http3ErrorCode.NoError);
            reason = new Http3StreamException(
                Http3ErrorCode.NoError,
                "The server stopped reading the request stream after sending a complete response (RFC 9114 §4.1).");
        }

        try
        {
            _reader.Input.Complete(reason);
        }
        catch (InvalidOperationException)
        {
            // ObjectDisposedException derives from InvalidOperationException: the stream was already
            // released underneath (connection teardown) — there is nothing left to stop.
        }
        catch (IOException)
        {
            // A driver's reader can fail its completion when the stream or its connection is already gone
            // (the QUIC driver's QuicException is an IOException).
        }
    }

    /// <summary>
    /// Combines the caller's token with the exchange's abort token and the connection's closure, so a
    /// read ends when any of them fires — the connection's closure included, since a body read may
    /// outlive the receive enumeration. Links only when more than one of them can fire.
    /// </summary>
    /// <param name="cancellationToken">The caller's token.</param>
    /// <param name="rateDeadline">
    /// The minimum data rate's deadline when it must cancel the read itself — on a stream that cannot be
    /// stopped with a code — or <see langword="default"/>.
    /// </param>
    private (CancellationToken Token, CancellationTokenSource? Linked) LinkAbort(CancellationToken cancellationToken, CancellationToken rateDeadline)
    {
        CancellationToken connectionClosed = _connection.ConnectionClosed;

        if (rateDeadline.CanBeCanceled)
        {
            CancellationTokenSource all = CancellationTokenSource.CreateLinkedTokenSource([cancellationToken, _requestAborted, connectionClosed, rateDeadline]);
            return (all.Token, all);
        }

        if (!cancellationToken.CanBeCanceled && !_requestAborted.CanBeCanceled)
        {
            return (connectionClosed, null);
        }

        if (!connectionClosed.CanBeCanceled && !_requestAborted.CanBeCanceled)
        {
            return (cancellationToken, null);
        }

        if (!connectionClosed.CanBeCanceled && !cancellationToken.CanBeCanceled)
        {
            return (_requestAborted, null);
        }

        CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _requestAborted, connectionClosed);
        return (linked.Token, linked);
    }

    private static bool IsStreamFailure(Exception exception)
    {
        // IOException covers QuicException (the QUIC driver) and EndOfStreamException; ConnectionException
        // is the in-memory driver's peer abort; InvalidOperationException (ObjectDisposedException
        // included) is a pipe completed or disposed underneath by a reset or connection teardown.
        return exception is IOException
            or ConnectionException
            or InvalidOperationException;
    }
}
