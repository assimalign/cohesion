using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http.Connections.Internal;

namespace Assimalign.Cohesion.Http.Connections.Internal;

/// <summary>
/// The lazy, forward-only HTTP/1.1 request-body stream. Reads the body incrementally from the
/// connection on demand — never buffering the whole body before the request is dispatched — while
/// enforcing the effective per-request body-size cap (413), when configured the minimum request-body
/// data rate (408), and the header-section bounds on a chunked body's trailer section (431, #1375).
/// Framing (Content-Length or chunked, RFC 9112 §6 / §7) is decided up front from the request
/// headers and handed in as an <see cref="Http1RequestBodyFraming"/>.
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
/// Every chunk framing line is capped (<see cref="Http1ConnectionListenerOptions.Http1Limits.MaxChunkFramingLineSize"/>,
/// chunk extensions included), and the trailer section is held to the header section's count and
/// size bounds, so what a chunked body buffers outside its data is bounded however the peer frames
/// it (#1375). The chunk-size lines of the whole body are held to a framing budget as well, so what
/// a chunked body makes the server read beyond its data is bounded too.
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
    private readonly int _maxFramingLineSize;
    private readonly long _maxFramingExcess;
    private readonly int _maxTrailerFieldCount;
    private readonly int _maxTrailerSectionSize;
    private readonly TimeProvider _timeProvider;
    private readonly CancellationToken _connectionToken;
    private readonly HttpTrailerCollection _trailers;

    // The owning exchange, attached by Http1Context's constructor. Consulted before soliciting
    // 100 Continue: once the final response has started, an interim response can no longer
    // legally precede it (RFC 9110 §15.2), so the solicitation is suppressed.
    private TransportHttpContext? _owner;

    private readonly byte[] _oneByte = new byte[1];

    // The buffer each chunk framing line is read into, reused across lines. Never grows past the
    // largest line the limits allow.
    private StringBuilder? _line;

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

    // The chunk framing octets received so far that the chunks' data has not paid for; see
    // ChargeChunkFraming.
    private long _framingExcess;

    // Set when a read failed while a framing line or the trailer section was in progress. The octets
    // that read consumed are gone, so where the framing resumes on the wire is not known.
    private bool _framingInterrupted;

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
    /// <see langword="null"/> on the zero-interceptor fast path (the cap is fixed at the listener's
    /// <see cref="HttpConnectionListenerLimits.MaxRequestBodySize"/>).
    /// </param>
    /// <param name="limits">
    /// The listener's HTTP/1.1 limits: the body-size cap used when <paramref name="interception"/> is
    /// <see langword="null"/>, the minimum request-body data rate, the chunk framing-line cap, and the
    /// header-section bounds the trailer section is held to.
    /// </param>
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
        Http1ConnectionListenerOptions.Http1Limits limits,
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
        _fallbackCap = limits.MaxRequestBodySize;
        _rate = limits.MinRequestBodyDataRate;
        _maxFramingLineSize = limits.MaxChunkFramingLineSize;
        _maxFramingExcess = 2L * limits.MaxChunkFramingLineSize;
        _maxTrailerFieldCount = limits.MaxRequestHeaderCount;
        _maxTrailerSectionSize = limits.MaxRequestHeadersTotalSize;
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

    /// <summary>
    /// Whether the chunked framing or the trailer section turned out to be malformed — a read failed
    /// with an <see cref="InvalidDataException"/>. Once set, the body is never read again: the
    /// connection's framing is no longer known, so it is not drained for keep-alive, and the
    /// exchange's response becomes a <c>400</c> when it has not started (#1333).
    /// </summary>
    internal bool IsMalformed { get; private set; }

    /// <summary>
    /// The status the body was rejected with because it broke a configured limit — <c>413</c> over the
    /// body-size cap, <c>408</c> below the minimum data rate, <c>431</c> for a trailer section over the
    /// header-section bounds (#1375) — or <see langword="null"/> while it has not. Latched where the
    /// <see cref="Http1LimitExceededException"/> is thrown. Once set, the body is
    /// never read again: a later read fails with the same status, the drain gives up so the connection
    /// closes, and the exchange's response becomes this status when it has not started (#1339).
    /// </summary>
    internal HttpStatusCode? RejectedStatusCode { get; private set; }

    /// <summary>
    /// Whether the peer closed the connection before the body its framing declared was complete: a
    /// <c>Content-Length</c> body short of its length, or a chunked body cut off inside a chunk or a
    /// framing line before its last chunk. The read fails with an <see cref="EndOfStreamException"/>.
    /// RFC 9112 §8 lets a server answer an incomplete request with an error before it closes the
    /// connection, so the exchange's response becomes a <c>400</c> when it has not started, as it does
    /// for a malformed body. Once set, the body is never read again and is not drained.
    /// </summary>
    internal bool IsIncomplete { get; private set; }

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
    /// read; a violation (413 / 408), a malformed body, an earlier read that stopped inside the
    /// chunked framing, or a wire failure returns <see langword="false"/> so the caller closes the
    /// connection instead of reusing it. Safe to call after disposal — it operates on the connection
    /// stream, which the body stream never owns.
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

        // A malformed body has no known end on the wire; the connection cannot be reused (#1333). Nor
        // can one rejected over a limit: where a chunked body broke its cap, the octets that follow the
        // offending chunk-size line are that chunk's data, which can read like a last chunk and then a
        // new request (#1339). Nor can one whose read stopped inside its framing, cancelled by the
        // application for instance: the octets of the line read so far are gone, so a drain would
        // resume mid-line, and the rest of a chunk-size line "40" read as a line of its own is "0", a
        // last chunk. Nor, finally, can one the peer cut short: the connection's read side has ended.
        if (IsMalformed || RejectedStatusCode is not null || _framingInterrupted || IsIncomplete)
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
            throw Reject(
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
        // A body rejected over a limit stays rejected, as a malformed one stays malformed: nothing more
        // is read from it, because the octets past the breach have no known framing (#1339).
        if (RejectedStatusCode is { } rejectedStatus)
        {
            return ValueTask.FromException<int>(new Http1LimitExceededException(
                rejectedStatus,
                $"The request body was rejected with {rejectedStatus} and is not read further."));
        }

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
            throw Truncated(
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
        // A body found malformed stays malformed. Where its framing broke, the octets that follow can
        // read like a last chunk and then a new request, so nothing more is read from it: a later
        // read fails again, and the drain gives up so the connection closes (#1333).
        if (IsMalformed)
        {
            throw new InvalidDataException("RFC 9112 §7.1: the chunked request body is malformed and is not read further.");
        }

        // A read that stopped inside a framing line cannot be resumed: the line's octets read so far are
        // gone, and resuming would read the rest of the line as a line of its own. The status is left
        // alone, since the client did nothing wrong; the connection just cannot be reused.
        if (_framingInterrupted)
        {
            throw new IOException(
                "RFC 9112 §7.1: an earlier read of the chunked request body stopped inside its framing, so the body is not read further.");
        }

        bool succeeded = false;
        try
        {
            int read = await ReadChunkedCoreAsync(buffer, cancellationToken).ConfigureAwait(false);
            succeeded = true;
            return read;
        }
        catch (InvalidDataException)
        {
            IsMalformed = true;
            throw;
        }
        finally
        {
            // _chunkRemaining is negative only while a chunk terminator, a chunk-size line or the
            // trailer section is being read (or is about to be): a read that failed there, cancelled
            // or not, leaves the framing at an unknown offset. A read that failed inside a chunk's
            // data consumed none of it, so that one can be resumed.
            if (!succeeded && _chunkRemaining < 0)
            {
                _framingInterrupted = true;
            }
        }
    }

    private async ValueTask<int> ReadChunkedCoreAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        while (true)
        {
            if (_chunkRemaining < 0)
            {
                // RFC 9112 §7.1 — every chunk's data is terminated by CRLF before the next size line.
                // The line may hold nothing else, so its cap is zero: the first other octet fails it, and
                // the rejection never quotes it, since it can be a bare CR, LF, or NUL.
                if (_needChunkTerminator)
                {
                    await ReadFramingLineAsync(0, FramingLine.ChunkTerminator, cancellationToken).ConfigureAwait(false);
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
                    throw Reject(
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
                throw Truncated(
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
        // RFC 9112 §7.1.1 — a server ought to limit the total length of chunk extensions. The whole
        // line is capped, extensions included, so an extension that never ends is rejected as
        // malformed (400) at the cap instead of being buffered for as long as the peer sends it (#1375),
        // and ChargeChunkFraming holds the lines of the whole body to a total.
        string sizeLine = await ReadFramingLineAsync(_maxFramingLineSize, FramingLine.ChunkSize, cancellationToken).ConfigureAwait(false);

        // RFC 9112 §7.1.1 — a chunk-size line is chunk-size, then chunk-ext: BWS, token, and
        // quoted-string, none of which carries a control character but HTAB. Each part of the line is
        // checked once, before the extension is dropped, so an intermediary that reads "2;<LF>xx" as
        // the size line "2;" and the data "xx" can never disagree with this reader (#1341, #1375):
        // ReadFramingLineAsync has already refused a bare CR or LF anywhere in the line, the size
        // below must be HEXDIG only, and Http1ChunkExtensions holds the extensions to their grammar
        // with the core token and field-value rules. No rejection quotes the line: a control
        // character it holds is named in hex.
        //
        // Strip the optional ";<chunk-ext>". BWS before the ';' is SP and HTAB only (RFC 9110
        // §5.6.3): TrimEnd() would also strip a no-break space, and accept "5\xA0;x" as 5.
        int semicolon = sizeLine.IndexOf(';');
        ReadOnlySpan<char> sizeText = semicolon < 0
            ? sizeLine.AsSpan()
            : sizeLine.AsSpan(0, semicolon).TrimEnd(Http1FieldLine.OptionalWhitespace);

        if (sizeText.IsEmpty)
        {
            throw new InvalidDataException("RFC 9112 §7.1: empty chunk-size.");
        }

        // The extensions are ignored, but they must keep to their grammar, so nothing that another
        // parser could read differently, a control character above all, rides inside them.
        if (semicolon >= 0 && !Http1ChunkExtensions.IsWellFormed(sizeLine.AsSpan(semicolon)))
        {
            throw new InvalidDataException(
                "RFC 9112 §7.1.1: malformed chunk extensions; each is ';' and a token, optionally '=' and a token or a quoted-string, with only spaces and tabs between.");
        }

        // chunk-size = 1*HEXDIG — ASCII hex only, no leading sign, no whitespace. Neither rejection
        // quotes the size: the octets after the one that fails it are unchecked, and can be controls.
        int value = 0;
        for (int index = 0; index < sizeText.Length; index++)
        {
            char c = sizeText[index];
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
                    $"RFC 9112 §7.1: the chunk-size holds the non-hex octet 0x{(int)c:X2} at offset {index}.");
            }

            // A single chunk-size is bounded by Int32; the accumulated body is separately bounded by
            // the effective body-size cap in ReadChunkedAsync.
            if (value > (int.MaxValue - digit) / 16)
            {
                throw new InvalidDataException(
                    $"RFC 9112 §7.1: the chunk-size overflows Int32 at its hex digit {index + 1}.");
            }
            value = (value * 16) + digit;
        }

        ChargeChunkFraming(sizeLine.Length, value);
        return value;
    }

    /// <summary>
    /// Holds the chunk framing of the whole body to a budget, as Go's chunked reader does. RFC 9112
    /// §7.1.1 asks a server to limit the total length of a request's chunk extensions, and the
    /// per-line cap alone does not: a peer can put a line just under the cap before every one-octet
    /// chunk, and the body-size cap, which counts data only, never sees it. So each chunk-size line
    /// charges its octets, its CRLF and the CRLF that ends its chunk's data, and each chunk pays back
    /// 16 octets plus twice its size. Once the unpaid excess passes twice
    /// <see cref="Http1ConnectionListenerOptions.Http1Limits.MaxChunkFramingLineSize"/>, the body is
    /// malformed (400). Leading zeros in a chunk-size are charged the same way. Ordinary framing never
    /// accumulates: a line of up to 14 octets is paid for by the smallest chunk. A chunk's credit is
    /// always backed by data, since its data must arrive before the next chunk-size line is read.
    /// </summary>
    /// <param name="sizeLineLength">The chunk-size line's length, extensions included, without its CRLF.</param>
    /// <param name="chunkSize">The chunk's size.</param>
    private void ChargeChunkFraming(int sizeLineLength, int chunkSize)
    {
        _framingExcess = Math.Max(0, _framingExcess + sizeLineLength + 4 - (16 + (2L * chunkSize)));

        if (_framingExcess > _maxFramingExcess)
        {
            throw new InvalidDataException(
                $"RFC 9112 §7.1.1: the chunked request body carries more than {_maxFramingExcess} octets of chunk framing, chunk extensions included, beyond what its chunks' data pays for.");
        }
    }

    private async ValueTask ReadTrailersAsync(CancellationToken cancellationToken)
    {
        // RFC 9112 §7.1.2 — the trailer-section follows the last chunk and ends with an empty line.
        // RFC 9110 §5.4 — it is held to the header section's bounds (#1375): every field line counts
        // against MaxRequestHeaderCount and, with its CRLF, against MaxRequestHeadersTotalSize,
        // repeated names included, and no line may exceed MaxChunkFramingLineSize. A breach is
        // answered 431, latched like the other limits, so nothing past it is read.
        Dictionary<HttpHeaderKey, List<string>>? fields = null;
        int fieldCount = 0;
        int sectionRemaining = _maxTrailerSectionSize;

        while (true)
        {
            // As in the header section, a line may never push the section past its total bound.
            int lineCap = Math.Min(_maxFramingLineSize, sectionRemaining);
            string line = await ReadFramingLineAsync(
                lineCap,
                lineCap < _maxFramingLineSize ? FramingLine.TrailerSection : FramingLine.TrailerField,
                cancellationToken).ConfigureAwait(false);

            if (line.Length == 0)
            {
                PublishTrailers(fields);
                return;
            }

            int consumed = line.Length + 2;
            if (consumed > sectionRemaining)
            {
                throw Reject(
                    HttpStatusCode.RequestHeaderFieldsTooLarge,
                    $"RFC 9112 §7.1.2: the trailer section exceeds the configured maximum size ({_maxTrailerSectionSize} octets).");
            }

            sectionRemaining -= consumed;

            if (++fieldCount > _maxTrailerFieldCount)
            {
                throw Reject(
                    HttpStatusCode.RequestHeaderFieldsTooLarge,
                    $"RFC 9112 §7.1.2: the trailer section contains more than the configured maximum of {_maxTrailerFieldCount} fields.");
            }

            // RFC 9112 §5.1 / §7.1.2 — a trailer field line has the header section's syntax: the name
            // is a token, with no whitespace before its colon and never empty (#1333), and the value
            // holds no control character but HTAB (RFC 9110 §5.5, #1341).
            if (!Http1FieldLine.TryParse(line, out string name, out string value, out string? violation))
            {
                throw new InvalidDataException($"RFC 9112 §7.1.2: the HTTP/1.1 trailer section is malformed. {violation}");
            }

            HttpHeaderKey key = new(name);

            // RFC 9110 §6.5.1 / RFC 9112 §7.1.2 — the trailer section is held to the rules HTTP/2 and
            // HTTP/3 apply (#1319): no field RFC 9110 excludes from trailers (the framing and routing
            // fields among them, which would otherwise be a request-smuggling vector) and no
            // connection-specific field. The InvalidDataException fails the body read as malformed.
            HttpTrailerFieldRules.EnsureReceivable(key, "HTTP/1.1");

            // Repeated fields are gathered per name and combined once, when the section ends, so a
            // section of repeats costs time linear in its size; combining on each repeat copied the
            // values gathered so far every time.
            fields ??= new Dictionary<HttpHeaderKey, List<string>>();

            if (!fields.TryGetValue(key, out List<string>? values))
            {
                values = new List<string>(1);
                fields.Add(key, values);
            }

            values.Add(value);
        }
    }

    /// <summary>
    /// Publishes a complete trailer section on the request's trailer collection, one entry per field
    /// name with its values in arrival order. A section that fails is never published in part.
    /// </summary>
    private void PublishTrailers(Dictionary<HttpHeaderKey, List<string>>? fields)
    {
        if (fields is null)
        {
            return;
        }

        foreach (KeyValuePair<HttpHeaderKey, List<string>> field in fields)
        {
            List<string> values = field.Value;
            _trailers[field.Key] = values.Count == 1
                ? new HttpHeaderValue(values[0])
                : new HttpHeaderValue(values.ToArray());
        }
    }

    /// <summary>
    /// Reads one CRLF-terminated chunk framing line, holding it to <paramref name="maxLength"/> octets
    /// before its CRLF. An octet past the cap fails the read at once, so a line that never ends costs
    /// at most the cap, whether the application or the keep-alive drain is reading. A line ends only at
    /// CRLF, and a bare CR or a bare LF anywhere in it fails the read as malformed.
    /// </summary>
    /// <remarks>
    /// RFC 9112 §2.2 lets a recipient take a bare LF for the end of a line, and requires it to treat a
    /// bare CR as invalid or as a space. Keeping either inside the line, as this reader once did, is
    /// neither, and it is a smuggling vector: an intermediary that ends the chunk-size line
    /// <c>2;\nxx</c> at the LF reads <c>xx</c> as the chunk's data and the next line as the next
    /// chunk's size, while this reader took the whole of it for one line, so the two disagree about
    /// where every later chunk, and the next request, starts. Rejecting both is the one reading no
    /// peer can contradict.
    /// </remarks>
    /// <param name="maxLength">The most octets the line may hold before its CRLF.</param>
    /// <param name="line">Which line is read, which decides how an over-long one is rejected.</param>
    /// <param name="cancellationToken">A token to cancel the read.</param>
    /// <returns>The line, without its CRLF.</returns>
    private async ValueTask<string> ReadFramingLineAsync(int maxLength, FramingLine line, CancellationToken cancellationToken)
    {
        StringBuilder builder = _line ??= new StringBuilder();
        builder.Clear();
        bool sawCarriageReturn = false;

        while (true)
        {
            int read = await ReadFromConnectionAsync(_oneByte, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw Truncated("RFC 9112 §7.1: connection closed while a chunk framing line was being read.");
            }

            byte b = _oneByte[0];
            if (sawCarriageReturn)
            {
                if (b == (byte)'\n')
                {
                    return builder.ToString();
                }

                throw new InvalidDataException("RFC 9112 §2.2: a chunk framing line holds a bare CR; a line ends only at CRLF.");
            }

            if (b == (byte)'\r')
            {
                sawCarriageReturn = true;
                continue;
            }

            if (b == (byte)'\n')
            {
                throw new InvalidDataException("RFC 9112 §2.2: a chunk framing line holds a bare LF; a line ends only at CRLF.");
            }

            AppendFramingOctet(builder, (char)b, maxLength, line);
        }
    }

    private void AppendFramingOctet(StringBuilder builder, char octet, int maxLength, FramingLine line)
    {
        if (builder.Length >= maxLength)
        {
            throw FramingLineTooLong(line, maxLength);
        }

        builder.Append(octet);
    }

    /// <summary>
    /// The rejection for a framing line over its cap. A chunk-size line or a chunk terminator is
    /// malformed framing (400, through <see cref="IsMalformed"/>); a trailer line breaks the
    /// header-section bounds (431, latched in <see cref="RejectedStatusCode"/>).
    /// </summary>
    private Exception FramingLineTooLong(FramingLine line, int maxLength) => line switch
    {
        FramingLine.ChunkTerminator => new InvalidDataException(
            "RFC 9112 §7.1: chunk terminator must be CRLF only."),
        FramingLine.ChunkSize => new InvalidDataException(
            $"RFC 9112 §7.1.1: the chunk-size line, chunk extensions included, exceeds the configured maximum of {maxLength} octets."),
        FramingLine.TrailerField => Reject(
            HttpStatusCode.RequestHeaderFieldsTooLarge,
            $"RFC 9112 §7.1.2: a trailer field line exceeds the configured maximum of {maxLength} octets."),
        _ => Reject(
            HttpStatusCode.RequestHeaderFieldsTooLarge,
            $"RFC 9112 §7.1.2: the trailer section exceeds the configured maximum size ({_maxTrailerSectionSize} octets)."),
    };

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

    private Http1LimitExceededException RateTooSlow()
    {
        // RFC 9110 §15.5.9 — a body received below the configured minimum data rate is reclaimed
        // with 408 Request Timeout semantics.
        return Reject(
            HttpStatusCode.RequestTimeout,
            "The request body was received below the configured minimum data rate.");
    }

    /// <summary>
    /// Latches <paramref name="statusCode"/> as the body's rejection and returns the exception the
    /// caller throws. The first rejection wins; the body is not read again after it.
    /// </summary>
    private Http1LimitExceededException Reject(HttpStatusCode statusCode, string message)
    {
        RejectedStatusCode ??= statusCode;
        return new Http1LimitExceededException(statusCode, message);
    }

    /// <summary>
    /// Marks the body <see cref="IsIncomplete"/>, because the peer closed the connection before the
    /// body its framing declared was complete, and returns the exception the caller throws. The type
    /// stays <see cref="EndOfStreamException"/>, so a reader that catches it keeps working.
    /// </summary>
    private EndOfStreamException Truncated(string message)
    {
        IsIncomplete = true;
        return new EndOfStreamException(message);
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

    /// <summary>
    /// The chunk framing lines <see cref="ReadFramingLineAsync"/> reads (RFC 9112 §7.1), which decide
    /// how a line over its cap is rejected.
    /// </summary>
    private enum FramingLine
    {
        /// <summary>A chunk-size line, chunk extensions included.</summary>
        ChunkSize,

        /// <summary>The CRLF that ends a chunk's data.</summary>
        ChunkTerminator,

        /// <summary>A trailer field line, capped by the framing-line limit.</summary>
        TrailerField,

        /// <summary>A trailer field line, capped by what is left of the trailer section's size limit.</summary>
        TrailerSection,
    }
}
