using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web;

namespace Assimalign.Cohesion.Web.Compression.Internal;

/// <summary>
/// The response-compression middleware: negotiates a content coding from the request's
/// <c>Accept-Encoding</c>, wraps <see cref="IHttpResponse.Body"/> in a
/// <see cref="CompressionBodyStream"/> that defers the compress/identity decision to the first body
/// write, and finalizes the encoder after the pipeline returns so the transport reads the coded
/// bytes and re-synthesizes <c>Content-Length</c>.
/// </summary>
/// <remarks>
/// <para>
/// Register it early so it wraps every middleware whose response it should compress. It engages only
/// on the buffered write path (<see cref="IHttpResponse.Body"/>); a handler that streams via the
/// response-streaming feature commits its own head and bypasses <see cref="IHttpResponse.Body"/>, so
/// such a response is left untouched (documented handoff, never corruption).
/// </para>
/// <para>
/// HTTPS handling is BREACH-cautious: over an <c>https</c> request the middleware does nothing unless
/// <see cref="ResponseCompressionOptions.EnableForHttps"/> is set, so the response is served
/// uncompressed and carries no <c>Vary: Accept-Encoding</c> it would not otherwise need.
/// </para>
/// </remarks>
internal sealed class ResponseCompressionMiddleware : IWebApplicationMiddleware
{
    private readonly ResponseCompressionOptions _options;
    private readonly CompressibleMimeMatcher _matcher;
    private readonly string[] _serverCodings;

    public ResponseCompressionMiddleware(ResponseCompressionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options;
        _matcher = CompressibleMimeMatcher.Create(options.MimeTypes);
        _serverCodings = BuildServerCodings(options);
    }

    /// <inheritdoc />
    public async Task InvokeAsync(IHttpContext context, WebApplicationMiddleware next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        // BREACH (CVE-2013-3587): do not compress dynamic content over HTTPS unless opted in.
        if (context.Request.Scheme == HttpScheme.Https && !_options.EnableForHttps)
        {
            await next.Invoke(context).ConfigureAwait(false);
            return;
        }

        // HEAD carries no body to compress; leaving it alone avoids stamping a coding on an empty
        // response whose length the transport handles on its own.
        if (context.Request.Method == HttpMethod.Head)
        {
            await next.Invoke(context).ConfigureAwait(false);
            return;
        }

        string? acceptEncoding = context.Request.Headers.GetValue(HttpHeaderKey.AcceptEncoding);

        // Empty server list means identity acceptability; a real coding means the client accepts one
        // of ours. The q-value / identity;q=0 semantics live entirely in the shared Http primitive.
        bool identityAcceptable = HttpContentNegotiation.TrySelectEncoding(acceptEncoding, Array.Empty<string>(), out _);
        string? coding =
            HttpContentNegotiation.TrySelectEncoding(acceptEncoding, _serverCodings, out string selected)
            && selected != ContentCodings.Identity
                ? selected
                : null;

        Stream originalBody = context.Response.Body;
        ResponseCompressionFeature feature = new();
        context.Features.Set<IResponseCompressionFeature>(feature);

        CompressionBodyStream body = new(originalBody, context.Response, _options, _matcher, feature, coding, identityAcceptable);
        context.Response.Body = body;

        try
        {
            await next.Invoke(context).ConfigureAwait(false);
        }
        finally
        {
            // Finalize on an uncancelled token: the encoder trailer must land in the buffer even when
            // the request was aborted, and the writes target the in-memory response buffer (no I/O to
            // cancel). The transport decides separately whether to actually send.
            await body.CompleteAsync(CancellationToken.None).ConfigureAwait(false);
            context.Response.Body = originalBody;
            context.Features.Set<IResponseCompressionFeature>(null);
        }
    }

    private static string[] BuildServerCodings(ResponseCompressionOptions options)
    {
        // Brotli first: at equal client quality it wins on ratio, and a higher client q for gzip
        // still overrides this order inside the negotiation primitive.
        if (options.EnableBrotli && options.EnableGzip)
        {
            return [ContentCodings.Brotli, ContentCodings.Gzip];
        }
        if (options.EnableBrotli)
        {
            return [ContentCodings.Brotli];
        }
        if (options.EnableGzip)
        {
            return [ContentCodings.Gzip];
        }

        throw new ArgumentException(
            "Response compression requires at least one coding; both EnableGzip and EnableBrotli are disabled.",
            nameof(options));
    }
}
