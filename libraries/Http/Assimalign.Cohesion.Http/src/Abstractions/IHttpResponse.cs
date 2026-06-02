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
    /// Defined as a default interface member returning the shared unsupported
    /// (empty, read-only) collection so the trailer section could be added to
    /// the core message model without breaking every existing
    /// <see cref="IHttpResponse"/> implementation. The abstract
    /// <see cref="HttpResponse"/> base and the protocol transports override it
    /// with a real collection where trailers are emitted.
    /// </remarks>
    IHttpTrailerCollection Trailers => HttpTrailerCollection.Unsupported;

    /// <summary>
    /// Get's the context associated with the response.
    /// </summary>
    IHttpContext HttpContext { get; }

    /// <summary>
    /// Gets or sets the response body stream.
    /// </summary>
    Stream Body { get; set; }
}
