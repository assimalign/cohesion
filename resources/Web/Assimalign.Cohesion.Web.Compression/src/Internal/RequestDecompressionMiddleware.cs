using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web;

namespace Assimalign.Cohesion.Web.Compression.Internal;

/// <summary>
/// The request-decompression middleware: transparently inflates a coded request body per its
/// <c>Content-Encoding</c> before handlers read it. It answers <c>415</c> for an unsupported coding,
/// bounds the decompressed size with a <c>413</c> guard against zip bombs, and hands downstream a
/// context whose <see cref="IHttpRequest.Body"/> is the decoded stream.
/// </summary>
/// <remarks>
/// <para>
/// The body is decoded lazily as the handler reads it &#8212; nothing is buffered up front. The
/// size guard therefore surfaces as an exception during the handler's read; register this middleware
/// <em>after</em> the exception boundary (<c>UseErrorHandling</c>) so that boundary sits outside it
/// and this middleware's own catch converts the guard trip into a clean <c>413</c> rather than a
/// generic <c>500</c>.
/// </para>
/// <para>
/// Multiple codings (<c>Content-Encoding: gzip, br</c>) are decoded in reverse application order, and
/// the request's <c>Content-Encoding</c> and <c>Content-Length</c> headers are removed once decoding
/// is in place, since downstream reads the decoded identity representation.
/// </para>
/// </remarks>
internal sealed class RequestDecompressionMiddleware : IWebApplicationMiddleware
{
    private readonly RequestDecompressionOptions _options;

    public RequestDecompressionMiddleware(RequestDecompressionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options;
    }

    /// <inheritdoc />
    public async Task InvokeAsync(IHttpContext context, WebApplicationMiddleware next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        IHttpHeaderCollection headers = context.Request.Headers;
        string? contentEncoding = headers.GetValue(HttpHeaderKey.ContentEncoding);

        if (string.IsNullOrWhiteSpace(contentEncoding))
        {
            await next.Invoke(context).ConfigureAwait(false);
            return;
        }

        if (!TryParseCodings(contentEncoding, out List<string> codings))
        {
            // An unsupported coding is refused before any handler runs (RFC 9110 §8.4 / §15.5.16).
            context.Response.StatusCode = HttpStatusCode.UnsupportedMediaType;
            return;
        }

        if (codings.Count == 0)
        {
            // Only identity (or a blank list): nothing to decode.
            await next.Invoke(context).ConfigureAwait(false);
            return;
        }

        Stream decoded = BuildDecodeChain(context.Request.Body, codings);
        LimitedDecompressionStream limited = new(decoded, _options.MaxDecompressedSizeBytes);

        // Downstream now reads the decoded identity representation; the coding and the compressed
        // length no longer describe it.
        headers.Remove(HttpHeaderKey.ContentEncoding);
        headers.Remove(HttpHeaderKey.ContentLength);

        RequestDecompressionHttpContext decorated = new(context, limited);

        try
        {
            await next.Invoke(decorated).ConfigureAwait(false);
        }
        catch (RequestDecompressionLimitException)
        {
            await RejectAsync(context, HttpStatusCode.RequestEntityTooLarge).ConfigureAwait(false);
        }
        catch (RequestDecompressionFormatException)
        {
            await RejectAsync(context, HttpStatusCode.BadRequest).ConfigureAwait(false);
        }
        finally
        {
            await limited.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static bool TryParseCodings(string contentEncoding, out List<string> codings)
    {
        codings = new List<string>();

        foreach (string segment in contentEncoding.Split(','))
        {
            string coding = segment.Trim();
            if (coding.Length == 0 || string.Equals(coding, ContentCodings.Identity, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (string.Equals(coding, ContentCodings.Gzip, StringComparison.OrdinalIgnoreCase))
            {
                codings.Add(ContentCodings.Gzip);
            }
            else if (string.Equals(coding, ContentCodings.Brotli, StringComparison.OrdinalIgnoreCase))
            {
                codings.Add(ContentCodings.Brotli);
            }
            else if (string.Equals(coding, ContentCodings.Deflate, StringComparison.OrdinalIgnoreCase))
            {
                codings.Add(ContentCodings.Deflate);
            }
            else
            {
                // An unknown or unsupported coding fails the whole request.
                return false;
            }
        }

        return true;
    }

    private static Stream BuildDecodeChain(Stream body, List<string> codings)
    {
        // Content-Encoding lists codings in application order, so decode in reverse. The innermost
        // decoder wraps the transport body and leaves it open (the transport owns it); each outer
        // decoder closes the one it wraps, so disposing the final stream cascades the whole chain.
        Stream stream = body;
        for (int i = codings.Count - 1; i >= 0; i--)
        {
            bool leaveOpen = i == codings.Count - 1;
            stream = CreateDecoder(codings[i], stream, leaveOpen);
        }

        return stream;
    }

    private static Stream CreateDecoder(string coding, Stream inner, bool leaveOpen) => coding switch
    {
        ContentCodings.Gzip => new GZipStream(inner, CompressionMode.Decompress, leaveOpen),
        ContentCodings.Brotli => new BrotliStream(inner, CompressionMode.Decompress, leaveOpen),
        // RFC 9110 §8.4.1.2: the "deflate" coding is the zlib data format (RFC 1950).
        _ => new ZLibStream(inner, CompressionMode.Decompress, leaveOpen),
    };

    private static async Task RejectAsync(IHttpContext context, HttpStatusCode statusCode)
    {
        // The failure surfaced while reading the request body. If the response already started
        // streaming, its head is locked — abort the exchange; otherwise discard any partial staged
        // response and answer with the status.
        if (context.Features.Get<IHttpResponseStreamingFeature>() is { HasStarted: true })
        {
            await context.CancelAsync().ConfigureAwait(false);
            return;
        }

        IHttpResponse response = context.Response;
        response.Headers.Clear();

        if (response.Body.CanSeek)
        {
            response.Body.SetLength(0);
        }

        response.StatusCode = statusCode;
    }
}
