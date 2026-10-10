using System.IO;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Represents the mutable response state for an HTTP exchange.
/// </summary>
public interface IHttpResponse
{
    /// <summary>
    /// Gets or sets the response status code.
    /// </summary>
    HttpStatusCode StatusCode { get; set; }

    /// <summary>
    /// Gets the collection of response headers.
    /// </summary>
    IHttpHeaderCollection Headers { get; }

    /// <summary>
    /// Gets the response trailer section — the fields a server queues to emit
    /// after the body (RFC 9110 §6.5).
    /// <see cref="IHttpTrailerCollection.IsSupported"/> reports whether the
    /// exchange can carry trailers; adding to an unsupported collection throws.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Defined as a default interface member returning the shared unsupported
    /// (empty, read-only) collection so the trailer section could be added to
    /// the core message model without breaking every existing
    /// <see cref="IHttpResponse"/> implementation. The abstract
    /// <see cref="HttpResponse"/> base returns the same unsupported collection,
    /// and an implementation that can send trailers overrides it.
    /// </para>
    /// <para>
    /// The <c>Assimalign.Cohesion.Http.Connections</c> transports support response
    /// trailers on HTTP/2 and HTTP/3: fields added before the response completes
    /// go out as a HEADERS frame after the body, and adding a pseudo-header, a name
    /// that is not a token, a connection-specific field, a field RFC 9110 §6.5.1
    /// prohibits in trailers (<see cref="HttpFieldRules.IsProhibitedInTrailers"/>), or
    /// a value holding a control character other than HTAB throws
    /// <see cref="System.ArgumentException"/>. A response to
    /// <c>HEAD</c> sends no trailers. On HTTP/1.1, and for a <c>CONNECT</c>
    /// exchange, the collection is unsupported.
    /// </para>
    /// </remarks>
    IHttpTrailerCollection Trailers => HttpTrailerCollection.Unsupported;

    /// <summary>
    /// Gets the context of the exchange this response belongs to.
    /// </summary>
    IHttpContext HttpContext { get; }

    /// <summary>
    /// Gets or sets the response body stream.
    /// </summary>
    Stream Body { get; set; }
}
