using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http.Connections.Internal;

namespace Assimalign.Cohesion.Http.Connections.Internal.Http1;

/// <summary>
/// The lazy, forward-only HTTP/1.1 request-body stream. Reads the body incrementally from the
/// connection on demand — never buffering the whole body before the request is dispatched — while
/// enforcing the effective per-request body-size cap (413) and, when configured, the minimum
/// request-body data rate (408). Framing (Content-Length or chunked, RFC 9112 §6 / §7) is decided
/// up front from the request headers and handed in as an <see cref="Http1RequestBodyFraming"/>.
/// </summary>
/// <remarks>
/// <para>
/// The stream never reads past its framing boundary: a Content-Length body reads at most the bytes
/// remaining, and a chunked body reads its size lines, terminators, and trailer section one byte at
/// a time. That byte-exact discipline is load-bearing — it keeps octets that a client pipelines
/// behind this request (or behind an accepted upgrade / CONNECT handshake) in the connection stream
/// for the next reader, and lets the connection realign for keep-alive via <see cref="DrainAsync"/>.
/// </para>
/// <para>
/// The stream does not own the connection stream (the connection does), so disposal never closes or
/// drains it — disposal only bars further public reads. The transport realigns the connection for
/// the next request by calling <see cref="DrainAsync"/>, which continues to work after disposal.
/// </para>
/// <para>
/// The body-size cap is frozen at the first read (the moment the transport starts consuming the
/// body), mirroring <see cref="HttpExchangeInterceptorRequestContext.FreezeMaxRequestBodySize"/>; up to
/// that point an endpoint or middleware may still raise or lower it through the typed
/// <c>IHttpMaxRequestBodySizeFeature</c>.
/// </para>
/// <para>
/// The <c>Expect: 100-continue</c> handshake (RFC 9110 §10.1.1) is solicited lazily here too: when
/// the request declared the expectation with a framed body, the first read emits
/// <c>100 Continue</c> before touching the wire — so a handler that answers 401 / 417 without
/// reading the body never solicits it. Solicitation is suppressed once the final response has
/// started (an interim response must precede it), and a declared-but-never-solicited body cannot
/// be drained for keep-alive — see <see cref="DrainAsync"/>.
/// </para>
/// </remarks>
internal sealed class Http1RequestBodyStream : Stream
{
    private readonly Stream _connection;
    private readonly Http1RequestBodyMode _mode;
    private readonly long _contentLength;
    private readonly bool _solicitContinue;
    private readonly HttpExchangeInterceptorRequestContext? _interception;
    private readonly long? _fallbackCap;
    private readonly HttpMinDataRate? _rate;
    private readonly TimeProvider _timeProvider;
    private readonly CancellationToken _connectionToken;
    private readonly HttpTrailerCollection _trailers;

    // The owning exchange, attached by Http1Context's constructor. Consulted before soliciting
    // 100 Continue: once the final response has started, an interim response can no longer
    // legally precede it (RFC 9110 §15.2), so the solicitation is suppressed.
    private TransportHttpContext? _owner;

    private readonly byte[] _oneByte = new byte[1];

    private bool _started;
    private bool _completed;
    private bool _disposed;
    private long? _cap;
    private long _totalRead;
    private MinDataRateGate? _gate;

    // Content-Length: octets still to deliver.
    private long _remaining;

    // Chunked state: octets left in the current chunk, or -1 when the next chunk header must be read.
    private long _chunkRemaining = -1;
    private bool _needChunkTerminator;

    /// <summary>
    /// Initializes the streaming request body.
    /// </summary>
    /// <param name="connection">The shared connection stream, positioned at the first body octet.</param>
    /// <param name="framing">The body framing decided from the request headers.</param>
    /// <param name="solicitContinue">
    /// Whether the request declared <c>Expect: 100-continue</c> with a framed body, so the first
    /// read must solicit the body with <c>100 Continue</c> before touching the wire.
    /// </param>
    /// <param name="interception">
    /// The request-parse context whose body-size knob is frozen on first read, or
    /// <see langword="null"/> on the zero-interceptor fast path (the cap is fixed at
    /// <paramref name="fallbackCap"/>).
    /// </param>
    /// <param name="fallbackCap">The listener-wide body-size cap used when <paramref name="interception"/> is <see langword="null"/>.</param>
    /// <param name="rate">The minimum request-body data rate to enforce, or <see langword="null"/> to disable it.</param>
    /// <param name="timeProvider">The monotonic clock used for data-rate measurement and deadlines.</param>
    /// <param name="connectionToken">The ambient connection token that aborts the body read on connection teardown.</param>
    /// <param name="trailers">
    /// The (supported, initially empty) trailer collection surfaced on the request; filled from the
    /// chunked trailer section when the body completes. For non-chunked framing this is the shared
    /// unsupported collection and is never written.
    /// </param>
    public Http1RequestBodyStream(
        Stream connection,
        Http1RequestBodyFraming framing,
        bool solicitContinue,
        HttpExchangeInterceptorRequestContext? interception,
        long? fallbackCap,
        HttpMinDataRate? rate,
        TimeProvider timeProvider,
        CancellationToken connectionToken,
        HttpTrailerCollection trailers)
    {
        _connection = connection;
        _mode = framing.Mode;
        _contentLength = framing.ContentLength;
        _remaining = framing.ContentLength;
        _solicitContinue = solicitContinue;
        _interception = interception;
        _fallbackCap = fallbackCap;
        _rate = rate;
        _timeProvider = timeProvider;
        _connectionToken = connectionToken;
        _trailers = trailers;
        // A `Content-Length: 0` body is born fully read: without this, the first read would issue
        // a zero-length wire read that blocks for octets the peer never sends, until the data-rate
        // gate reclaims the exchange (408) — for a request that is perfectly healthy.
        _completed = framing.Mode == Http1RequestBodyMode.None
            || (framing.Mode == Http1RequestBodyMode.ContentLength && framing.ContentLength == 0);
    }

    /// <summary>
    /// Attaches the exchange this body stream belongs to. Called once by
    /// <see cref="Http1Context"/>'s constructor (the stream is created first, so the back-reference
    /// cannot be a constructor argument). Consulted before soliciting <c>100 Continue</c>.
    /// </summary>
    /// <param name="owner">The owning exchange.</param>
    internal void SetOwner(TransportHttpContext owner)
    {
        _owner = owner;
    }

    /// <inheritdoc />
    public override bool CanRead => !_disposed;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override bool CanWrite => false;

    /// <inheritdoc />
    public override long Length => throw new NotSupportedException();

    /// <inheritdoc />
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <inheritdoc />
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        return await ReadCoreAsync(buffer, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count)
        => ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

    /// <inheritdoc />
    public override void Flush()
    {
    }

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <summary>
    /// Consumes and discards any request body not yet read, so the connection realigns on the next
    /// request's framing for keep-alive. Enforces the same body-size cap and data rate as a normal
    /// read; a violation (413 / 408), a malformed body, or a wire failure returns
    /// <see langword="false"/> so the caller closes the connection instead of reusing it. Safe to
    /// call after disposal — it operates on the connection stream, which the body stream never owns.
    /// </summary>
    /// <param name="cancellationToken">The ambient connection token.</param>
    /// <returns><see langword="true"/> when the body was fully drained and the connection realigned; otherwise <see langword="false"/>.</returns>
    internal async ValueTask<bool> DrainAsync(CancellationToken cancellationToken)
    {
        // An Expect: 100-continue body that was never solicited cannot be drained: the final
        // response has already been written by the time the transport drains for keep-alive, so
        // 100 Continue can no longer legally be emitted (an interim response must precede the
        // final response), and RFC 9110 §10.1.1 leaves it open whether the peer will transmit the
        // body anyway — the wire state is indeterminate. Close instead of reuse.
        if (_solicitContinue && !_started)
        {
            return false;
        }

        try
        {
            await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);

            byte[] scratch = new byte[4096];
            while (!_completed)
            {
                int read = await ReadCoreAsync(scratch, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }
            }

            return true;
        }
        catch (Exception exception) when (exception is IOException or OperationCanceledException or InvalidDataException)
        {
            // The unread body could not be drained cleanly (slow trickle, over-cap, malformed, or a
            // wire failure). The connection can no longer be safely reused; the caller closes it.
            return false;
        }
    }

    private async ValueTask EnsureStartedAsync(CancellationToken cancellationToken)
    {
        if (_started)
        {
            return;
        }

        _started = true;

        // The transport is now consuming the body: freeze the per-request cap (idempotent) and
        // resolve the value that will be enforced for the rest of the exchange.
        _interception?.FreezeMaxRequestBodySize();
        _cap = _interception is not null ? _interception.MaxRequestBodySize : _fallbackCap;

        if (_rate is not null)
        {
            _gate = new MinDataRateGate(_rate, _timeProvider);
        }

        // A Content-Length declaration over the (now frozen) cap is rejected before reading a byte
        // (RFC 9110 §15.5.14) — and before the body is solicited, so an over-cap declaration is
        // never invited onto the wire. A chunked body is checked as it accumulates in
        // ReadChunkedAsync.
        if (_mode == Http1RequestBodyMode.ContentLength && _cap is { } cap && _contentLength > cap)
        {
            throw new Http1LimitExceededException(
                HttpStatusCode.RequestEntityTooLarge,
                $"Content-Length value '{_contentLength}' exceeds the configured maximum request body size ({cap} octets).");
        }

        // RFC 9110 §10.1.1 — the peer declared Expect: 100-continue and is withholding the body
        // until solicited. Emit 100 Continue before the first wire read unblocks the handshake —
        // unless the final response has already started, after which an interim response can no
        // longer legally precede it (the read then proceeds unsolicited; a peer that transmits
        // anyway is drained normally, one that does not is reclaimed by the data-rate gate).
        if (_solicitContinue && _owner?.HasFinalResponseStarted != true)
        {
            await Http1MessageWriter.WriteInterimResponseAsync(
                _connection,
                HttpStatusCode.Continue,
                headers: null,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private ValueTask<int> ReadCoreAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        if (_completed || buffer.IsEmpty)
        {
            return new ValueTask<int>(0);
        }

        return _mode switch
        {
            Http1RequestBodyMode.ContentLength => ReadContentLengthAsync(buffer, cancellationToken),
            Http1RequestBodyMode.Chunked => ReadChunkedAsync(buffer, cancellationToken),
            _ => new ValueTask<int>(0),
        };
    }

    private async ValueTask<int> ReadContentLengthAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        int toRead = (int)Math.Min(buffer.Length, _remaining);
        int read = await ReadFromConnectionAsync(buffer[..toRead], cancellationToken).ConfigureAwait(false);
        if (read == 0)
        {
            throw new EndOfStreamException(
                $"The connection closed after {_totalRead} of {_contentLength} expected request-body octets.");
        }

        _remaining -= read;
        _totalRead += read;
        if (_remaining == 0)
        {
            _completed = true;
        }

        return read;
    }

    private async ValueTask<int> ReadChunkedAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        while (true)
        {
            if (_chunkRemaining < 0)
            {
                // RFC 9112 §7.1 — every chunk's data is terminated by CRLF before the next size line.
                if (_needChunkTerminator)
                {
                    string terminator = await ReadFramingLineAsync(cancellationToken).ConfigureAwait(false);
                    if (terminator.Length != 0)
                    {
                        throw new InvalidDataException(
                            $"RFC 9112 §7.1: chunk terminator must be CRLF only; got '{terminator}'.");
                    }
                    _needChunkTerminator = false;
                }

                int chunkSize = await ReadChunkSizeAsync(cancellationToken).ConfigureAwait(false);
                if (chunkSize == 0)
                {
                    // Last chunk — an optional trailer section then the terminating empty line.
                    await ReadTrailersAsync(cancellationToken).ConfigureAwait(false);
                    _completed = true;
                    return 0;
                }

                if (_cap is { } cap && _totalRead + chunkSize > cap)
                {
                    // RFC 9110 §15.5.14 — reject a chunked body that would exceed the cap before the
                    // offending chunk is delivered.
                    throw new Http1LimitExceededException(
                        HttpStatusCode.RequestEntityTooLarge,
                        $"Chunked body exceeds the configured maximum request body size ({cap} octets) at chunk size {chunkSize} after {_totalRead} octets read.");
                }

                _chunkRemaining = chunkSize;
                _needChunkTerminator = true;
            }

            int toRead = (int)Math.Min(buffer.Length, _chunkRemaining);
            int read = await ReadFromConnectionAsync(buffer[..toRead], cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException(
                    $"RFC 9112 §7.1: connection closed mid-chunk with {_chunkRemaining} octets outstanding.");
            }

            _chunkRemaining -= read;
            _totalRead += read;
            if (_chunkRemaining == 0)
            {
                _chunkRemaining = -1;
            }

            return read;
        }
    }

    private async ValueTask<int> ReadChunkSizeAsync(CancellationToken cancellationToken)
    {
        string sizeLine = await ReadFramingLineAsync(cancellationToken).ConfigureAwait(false);

        // RFC 9112 §7.1.1 — strip the optional ";<chunk-ext>" (BWS allowed before the ';').
        int semicolon = sizeLine.IndexOf(';');
        ReadOnlySpan<char> sizeText = semicolon < 0
            ? sizeLine.AsSpan()
            : sizeLine.AsSpan(0, semicolon).TrimEnd();

        if (sizeText.IsEmpty)
        {
            throw new InvalidDataException("RFC 9112 §7.1: empty chunk-size.");
        }

        // chunk-size = 1*HEXDIG — ASCII hex only, no leading sign, no whitespace.
        int value = 0;
        foreach (char c in sizeText)
        {
            int digit;
            if (c >= '0' && c <= '9')
            {
                digit = c - '0';
            }
            else if (c >= 'a' && c <= 'f')
            {
                digit = c - 'a' + 10;
            }
            else if (c >= 'A' && c <= 'F')
            {
                digit = c - 'A' + 10;
            }
            else
            {
                throw new InvalidDataException(
                    $"RFC 9112 §7.1: chunk-size '{sizeText.ToString()}' contains non-hex character '{c}'.");
            }

            // A single chunk-size is bounded by Int32; the accumulated body is separately bounded by
            // the effective body-size cap in ReadChunkedAsync.
            if (value > (int.MaxValue - digit) / 16)
            {
                throw new InvalidDataException(
                    $"RFC 9112 §7.1: chunk-size '{sizeText.ToString()}' overflows Int32.");
            }
            value = (value * 16) + digit;
        }

        return value;
    }

    private async ValueTask ReadTrailersAsync(CancellationToken cancellationToken)
    {
        // RFC 9112 §7.1.2 — the trailer-section follows the last chunk and ends with an empty line.
        while (true)
        {
            string line = await ReadFramingLineAsync(cancellationToken).ConfigureAwait(false);
            if (line.Length == 0)
            {
                return;
            }

            int colon = line.IndexOf(':');
            if (colon <= 0)
            {
                throw new InvalidDataException($"RFC 9112 §7.1.2: malformed trailer line '{line}'.");
            }

            string name = line[..colon].Trim();
            string value = line[(colon + 1)..].Trim();
            HttpHeaderKey key = new(name);

            // RFC 9112 §7.1.2 — framing-related fields are forbidden in the trailer section; letting
            // them through would be a request-smuggling vector.
            if (key.Equals(HttpHeaderKey.ContentLength)
                || key.Equals(HttpHeaderKey.TransferEncoding)
                || key.Equals(HttpHeaderKey.Host))
            {
                throw new InvalidDataException(
                    $"RFC 9112 §7.1.2: trailer field '{name}' is forbidden in the trailer section.");
            }

            if (_trailers.TryGetValue(key, out HttpHeaderValue existing))
            {
                _trailers[key] = HttpHeaderValue.Concat(existing, value);
            }
            else
            {
                _trailers[key] = value;
            }
        }
    }

    private async ValueTask<string> ReadFramingLineAsync(CancellationToken cancellationToken)
    {
        StringBuilder builder = new();
        bool sawCarriageReturn = false;

        while (true)
        {
            int read = await ReadFromConnectionAsync(_oneByte, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException("RFC 9112 §7.1: connection closed while a chunk framing line was being read.");
            }

            byte b = _oneByte[0];
            if (sawCarriageReturn)
            {
                if (b == (byte)'\n')
                {
                    return builder.ToString();
                }

                builder.Append('\r');
                sawCarriageReturn = false;
            }

            if (b == (byte)'\r')
            {
                sawCarriageReturn = true;
                continue;
            }

            builder.Append((char)b);
        }
    }

    private async ValueTask<int> ReadFromConnectionAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        // Always bound the socket read by the connection token so a body read parked on a stalled
        // peer is aborted on connection teardown, whatever token the caller passed (an application
        // may read Request.Body with CancellationToken.None).
        if (_gate is null)
        {
            (CancellationToken readToken, CancellationTokenSource? linkedAbort) = LinkAbort(cancellationToken);
            try
            {
                return await _connection.ReadAsync(buffer, readToken).ConfigureAwait(false);
            }
            finally
            {
                linkedAbort?.Dispose();
            }
        }

        if (!_gate.TryGetOperationTimeout(out TimeSpan timeout))
        {
            throw RateTooSlow();
        }

        using CancellationTokenSource timeoutSource = new(timeout, _gate.TimeProvider);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _connectionToken, timeoutSource.Token);

        long start = _gate.TimeProvider.GetTimestamp();
        try
        {
            int read = await _connection.ReadAsync(buffer, linked.Token).ConfigureAwait(false);
            _gate.Record(_gate.TimeProvider.GetTimestamp() - start, read);
            return read;
        }
        catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested
            && !cancellationToken.IsCancellationRequested
            && !_connectionToken.IsCancellationRequested)
        {
            throw RateTooSlow();
        }
    }

    /// <summary>
    /// Combines the caller's token with the connection token, avoiding a linked
    /// <see cref="CancellationTokenSource"/> allocation when one side cannot be cancelled.
    /// </summary>
    private (CancellationToken Token, CancellationTokenSource? Linked) LinkAbort(CancellationToken cancellationToken)
    {
        if (!_connectionToken.CanBeCanceled || cancellationToken == _connectionToken)
        {
            return (cancellationToken, null);
        }

        if (!cancellationToken.CanBeCanceled)
        {
            return (_connectionToken, null);
        }

        CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _connectionToken);
        return (linked.Token, linked);
    }

    private static Http1LimitExceededException RateTooSlow()
    {
        // RFC 9110 §15.5.9 — a body received below the configured minimum data rate is reclaimed
        // with 408 Request Timeout semantics.
        return new Http1LimitExceededException(
            HttpStatusCode.RequestTimeout,
            "The request body was received below the configured minimum data rate.");
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        // The connection stream is owned by the connection, never by this body stream, so disposal
        // must not close or drain it — it only bars further public reads. Realignment for keep-alive
        // is the transport's job via DrainAsync, which keeps working after disposal.
        _disposed = true;
        base.Dispose(disposing);
    }
}
