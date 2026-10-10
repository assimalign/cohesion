using System.IO;

namespace Assimalign.Cohesion.Http.Connections.Internal;

/// <summary>
/// A request head a transport decoded off the wire: every value a <see cref="TransportHttpRequest"/>
/// is built from. The transport hands it to its exchange context, and the context constructs the
/// request (see <see cref="TransportHttpContext"/>).
/// </summary>
/// <remarks>
/// The head exists because of construction order. A request needs its owning context as a constructor
/// argument (the <see cref="HttpRequest.HttpContext"/> back-reference), but every transport decodes the
/// request, and runs the request-parse interceptors over it, before the context exists. Carrying the
/// decoded values instead of a finished request lets the context construct its own request and
/// response and pass itself to each, so neither type has a wire-up method.
/// </remarks>
/// <param name="Host">The request host (<c>Host</c>, or <c>:authority</c> on HTTP/2 and HTTP/3).</param>
/// <param name="Path">The decoded request path.</param>
/// <param name="Method">The request method.</param>
/// <param name="Scheme">The request scheme.</param>
/// <param name="Query">The parsed query collection.</param>
/// <param name="Headers">The request header fields.</param>
/// <param name="Body">
/// The request body: the transport body stream, or the outermost wrapper the request-parse
/// interceptors produced over it.
/// </param>
/// <param name="Trailers">
/// The request trailer collection, or <see langword="null"/> when the transport does not surface
/// request trailers (the request then reports <see cref="HttpTrailerCollection.Unsupported"/>).
/// </param>
/// <param name="Protocol">
/// The <c>:protocol</c> of an HTTP/2 or HTTP/3 extended CONNECT, set only once the head has been
/// validated as one (RFC 8441 §4, RFC 9220 §3), or <see langword="null"/> for any other request. The
/// request-parse interceptors read it as <see cref="HttpExchangeInterceptorRequestContext.Protocol"/>,
/// and the exchange keeps it so its control can accept the tunnel.
/// </param>
internal readonly record struct TransportHttpRequestHead(
    HttpHost Host,
    HttpPath Path,
    HttpMethod Method,
    HttpScheme Scheme,
    HttpQueryCollection Query,
    HttpHeaderCollection Headers,
    Stream Body,
    HttpTrailerCollection? Trailers = null,
    string? Protocol = null);
