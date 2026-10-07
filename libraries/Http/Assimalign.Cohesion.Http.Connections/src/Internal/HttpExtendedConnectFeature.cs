using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http.Connections.Internal;

/// <summary>
/// The transport's <see cref="IHttpExtendedConnectFeature"/>, installed on every HTTP/2 and HTTP/3
/// exchange that is a valid extended CONNECT (RFC 8441, RFC 9220). This base owns the accept rules
/// both versions share: accept at most once, never after the response started or the exchange was
/// cancelled, and answer with a <c>200</c> stripped of the fields a tunnel cannot carry. The
/// per-version subclasses claim the stream and commit the head.
/// </summary>
/// <remarks>
/// <para>
/// Accepting registers the tunnel on the exchange <em>before</em> the head is written, the way an
/// HTTP/1.1 takeover claims the connection first. From then on the exchange reports
/// <see cref="HttpExchangeDirective.TakeOver"/>, so neither the buffered send path nor the raw response
/// body sink can put a second head on the stream, even if writing the <c>200</c> fails.
/// </para>
/// <para>
/// The response-head and after-response interceptor hooks do not run for a tunnel: accepting takes the
/// exchange over, as an HTTP/1.1 upgrade does, and the hooks are documented not to run for a
/// taken-over exchange. A WebSocket behaves the same on all three versions as a result.
/// </para>
/// </remarks>
internal abstract class HttpExtendedConnectFeature : IHttpExtendedConnectFeature
{
    /// <summary>The name under which the feature is registered on the exchange.</summary>
    public const string FeatureName = "Assimalign.Cohesion.Http.ExtendedConnect";

    private readonly TransportHttpContext _context;
    private int _acceptCalled;

    /// <summary>
    /// Initializes the feature for an exchange.
    /// </summary>
    /// <param name="context">The extended CONNECT exchange.</param>
    /// <param name="protocol">The <c>:protocol</c> the client requested; never <see langword="null"/> or empty.</param>
    protected HttpExtendedConnectFeature(TransportHttpContext context, string protocol)
    {
        _context = context;
        Protocol = protocol;
    }

    /// <inheritdoc />
    public string Name => FeatureName;

    /// <inheritdoc />
    public string Protocol { get; }

    /// <summary>
    /// Gets whether the exchange's final response has started, so a tunnel can no longer be accepted.
    /// </summary>
    protected virtual bool HasResponseStarted => _context.HasFinalResponseStarted;

    /// <inheritdoc />
    public async ValueTask<Stream> AcceptAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _acceptCalled, 1) == 1)
        {
            throw new InvalidOperationException("The extended CONNECT tunnel has already been accepted for this exchange.");
        }

        if (_context.CancelRequested)
        {
            throw new InvalidOperationException("The extended CONNECT tunnel cannot be accepted: the exchange was cancelled.");
        }

        if (HasResponseStarted)
        {
            throw new InvalidOperationException("The extended CONNECT tunnel cannot be accepted after the response has started.");
        }

        ThrowIfStreamAborted();
        PrepareResponseHead(_context.Response);

        return await AcceptCoreAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Throws an <see cref="IOException"/> when the exchange's stream is already gone — reset by the
    /// peer or by the transport, or its connection closed — so nothing can be accepted on it.
    /// </summary>
    protected abstract void ThrowIfStreamAborted();

    /// <summary>
    /// Claims the stream for the tunnel, registers the tunnel on the exchange, writes the prepared
    /// <c>200</c> head without ending the stream, and marks the head committed.
    /// </summary>
    /// <param name="cancellationToken">A token that cancels writing the head.</param>
    /// <returns>The tunnel, with its head on the wire.</returns>
    protected abstract ValueTask<HttpExtendedConnectStream> AcceptCoreAsync(CancellationToken cancellationToken);

    private static void PrepareResponseHead(HttpResponse response)
    {
        // RFC 8441 §5 / RFC 9220 §3 — a 2xx response establishes the tunnel.
        response.StatusCode = HttpStatusCode.Ok;

        HttpHeaderCollection headers = response.Headers;

        // RFC 9110 §9.3.6 — a 2xx response to CONNECT carries no Content-Length or Transfer-Encoding: the
        // stream that follows is the tunnel, not a body. RFC 9113 §8.2.2 / RFC 9114 §4.2 — an HTTP/2 or
        // HTTP/3 field section never carries connection-specific fields, which a client treats as a
        // malformed response; an application that set the HTTP/1.1 upgrade fields would break the tunnel.
        headers.Remove(HttpHeaderKey.ContentLength);

        List<HttpHeaderKey>? forbidden = null;

        foreach (KeyValuePair<HttpHeaderKey, HttpHeaderValue> header in headers)
        {
            if (HttpFieldNormalization.IsForbiddenInHttp2Or3(header.Key))
            {
                (forbidden ??= new List<HttpHeaderKey>()).Add(header.Key);
            }
        }

        if (forbidden is not null)
        {
            foreach (HttpHeaderKey key in forbidden)
            {
                headers.Remove(key);
            }
        }
    }
}
