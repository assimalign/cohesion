using System;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.Compression.Internal;

/// <summary>
/// The response-body wrapper that defers the compression decision to the first body write. It
/// replaces <see cref="IHttpResponse.Body"/> for the duration of the exchange; the handler writes
/// plaintext into it, and it either streams those bytes straight through to the original buffered
/// body (identity) or through a gzip/Brotli encoder into that same body, choosing per the response
/// headers known at first write and the size threshold.
/// </summary>
/// <remarks>
/// <para>
/// The decision is deferred because there is no header-commit hook: the media type, status, and any
/// handler-set <c>Content-Encoding</c> are only known once the handler starts writing. On the first
/// write the stream evaluates eligibility (handler opt-out, already-encoded, status, media type);
/// for an eligible response it stamps <c>Vary: Accept-Encoding</c> and then applies the size
/// threshold, engaging the encoder only when the body crosses the threshold (or immediately when the
/// client refused <c>identity</c>, leaving no uncompressed fallback). When it engages it sets
/// <c>Content-Encoding</c> and removes any <c>Content-Length</c> so the transport re-synthesizes the
/// length from the compressed bytes.
/// </para>
/// <para>
/// Writes are flush-through: the encoder streams into the transport's existing response buffer, so
/// the stream never accumulates a second full copy of the body &#8212; it holds at most one
/// threshold's worth of bytes while undecided. The encoder's trailing block is written by
/// <see cref="CompleteAsync"/>, which the middleware calls after the pipeline returns and before the
/// transport reads the body.
/// </para>
/// </remarks>
internal sealed class CompressionBodyStream : Stream
{
    private enum Mode
    {
        Undecided,
        Buffering,
        Compressing,
        Passthrough,
    }

    private readonly Stream _inner;
    private readonly IHttpResponse _response;
    private readonly ResponseCompressionOptions _options;
    private readonly CompressibleMimeMatcher _matcher;
    private readonly ResponseCompressionFeature _feature;
    private readonly string? _coding;
    private readonly bool _identityAcceptable;

    private MemoryStream? _pending;
    private Stream? _encoder;
    private Mode _mode;

    public CompressionBodyStream(
        Stream inner,
        IHttpResponse response,
        ResponseCompressionOptions options,
        CompressibleMimeMatcher matcher,
        ResponseCompressionFeature feature,
        string? coding,
        bool identityAcceptable)
    {
        _inner = inner;
        _response = response;
        _options = options;
        _matcher = matcher;
        _feature = feature;
        _coding = coding;
        _identityAcceptable = identityAcceptable;
    }

    /// <summary>Gets a value indicating whether the stream ultimately compressed the response.</summary>
    public bool Compressed => _mode == Mode.Compressing;

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_mode == Mode.Undecided)
        {
            await DecideAsync(cancellationToken).ConfigureAwait(false);
        }

        switch (_mode)
        {
            case Mode.Compressing:
                await _encoder!.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
                break;

            case Mode.Passthrough:
                await _inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
                break;

            default: // Buffering
                _pending!.Write(buffer.Span);
                if (_pending.Length > _options.MinimumResponseSizeBytes)
                {
                    await EngageCompressionAsync(cancellationToken).ConfigureAwait(false);
                }
                break;
        }
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => WriteAsync(new ReadOnlyMemory<byte>(buffer, offset, count), cancellationToken).AsTask();

    public override void Write(byte[] buffer, int offset, int count)
        // The wrapped body is the transport's in-memory response buffer, so this completes
        // synchronously without blocking on real I/O; handlers overwhelmingly use the async path.
        => WriteAsync(new ReadOnlyMemory<byte>(buffer, offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

    public override void Flush()
    {
        if (_mode == Mode.Compressing)
        {
            _encoder!.Flush();
        }
    }

    public override async Task FlushAsync(CancellationToken cancellationToken)
    {
        if (_mode == Mode.Compressing)
        {
            await _encoder!.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Finalizes the response body: for a below-threshold candidate, flushes the buffered bytes
    /// uncompressed; for a compressed response, flushes and closes the encoder so its trailing block
    /// lands in the underlying buffer. Idempotent. The middleware calls this after the pipeline
    /// returns and before restoring the original body for the transport to read.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the finalization.</param>
    /// <returns>A task that completes when the body has been finalized.</returns>
    public async ValueTask CompleteAsync(CancellationToken cancellationToken = default)
    {
        switch (_mode)
        {
            case Mode.Buffering:
                // Eligible, but the body never crossed the threshold: send it uncompressed. Vary was
                // already stamped at the decision point, so caches still key on Accept-Encoding.
                await FlushPendingAsync(_inner, cancellationToken).ConfigureAwait(false);
                _mode = Mode.Passthrough;
                break;

            case Mode.Compressing:
                if (_encoder is not null)
                {
                    await _encoder.FlushAsync(cancellationToken).ConfigureAwait(false);
                    await _encoder.DisposeAsync().ConfigureAwait(false);
                    _encoder = null;
                }
                break;
        }

        if (_pending is not null)
        {
            await _pending.DisposeAsync().ConfigureAwait(false);
            _pending = null;
        }
    }

    private async ValueTask DecideAsync(CancellationToken cancellationToken)
    {
        // A handler may have opted this response out while composing it.
        if (!_feature.IsEnabled)
        {
            _mode = Mode.Passthrough;
            return;
        }

        IHttpHeaderCollection headers = _response.Headers;

        // Never double-compress: a response that already carries a Content-Encoding owns its coding.
        if (headers.TryGetValue(HttpHeaderKey.ContentEncoding, out HttpHeaderValue encoding) && !encoding.IsEmpty)
        {
            _mode = Mode.Passthrough;
            return;
        }

        // Bodyless statuses are not compressed (they should not reach a body write anyway).
        if (!IsCompressibleStatus(_response.StatusCode))
        {
            _mode = Mode.Passthrough;
            return;
        }

        // Media-type gate.
        if (!_matcher.IsMatch(headers.GetValue(HttpHeaderKey.ContentType)))
        {
            _mode = Mode.Passthrough;
            return;
        }

        // The representation now depends on Accept-Encoding, so every response for this URL must say
        // so — including the uncompressed one a non-accepting client receives.
        AppendVaryAcceptEncoding(headers);

        // The client did not accept any coding we offer: serve identity (Vary is already stamped).
        if (_coding is null)
        {
            _mode = Mode.Passthrough;
            return;
        }

        _pending = new MemoryStream();
        _mode = Mode.Buffering;

        // No uncompressed fallback is acceptable (identity;q=0): the threshold cannot apply, so
        // compress from the first byte regardless of size.
        if (!_identityAcceptable)
        {
            await EngageCompressionAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask EngageCompressionAsync(CancellationToken cancellationToken)
    {
        IHttpHeaderCollection headers = _response.Headers;

        headers[HttpHeaderKey.ContentEncoding] = _coding;
        // The buffered length no longer describes the wire body; drop it so the transport
        // re-synthesizes Content-Length from the compressed bytes (or streams it chunked).
        headers.Remove(HttpHeaderKey.ContentLength);

        _encoder = CreateEncoder(_coding!, _inner, _options.Level);
        _mode = Mode.Compressing;

        await FlushPendingAsync(_encoder, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask FlushPendingAsync(Stream destination, CancellationToken cancellationToken)
    {
        if (_pending is null || _pending.Length == 0)
        {
            return;
        }

        _pending.Position = 0;
        await _pending.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
        _pending.SetLength(0);
    }

    private static Stream CreateEncoder(string coding, Stream destination, CompressionLevel level)
        => coding == ContentCodings.Brotli
            ? new BrotliStream(destination, level, leaveOpen: true)
            : new GZipStream(destination, level, leaveOpen: true);

    private static bool IsCompressibleStatus(HttpStatusCode statusCode)
    {
        int code = statusCode.Value;

        // 1xx interim and the two bodyless success/redirect statuses carry no representation to code.
        if (code < 200 || code == HttpStatusCode.NoContent.Value || code == HttpStatusCode.NotModified.Value)
        {
            return false;
        }

        return true;
    }

    private static void AppendVaryAcceptEncoding(IHttpHeaderCollection headers)
    {
        // Mirrors the Web.Serialization / Web.StaticFiles append: preserve existing Vary tokens
        // (for example the Accept a negotiated content write stamped) and never duplicate the token
        // or override a Vary: *.
        if (!headers.TryGetValue(HttpHeaderKey.Vary, out HttpHeaderValue existing) || existing.IsEmpty)
        {
            headers[HttpHeaderKey.Vary] = "Accept-Encoding";
            return;
        }

        string current = existing.Value;
        foreach (string segment in current.Split(','))
        {
            string token = segment.Trim();
            if (token == "*" || string.Equals(token, "Accept-Encoding", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        headers[HttpHeaderKey.Vary] = $"{current}, Accept-Encoding";
    }
}
