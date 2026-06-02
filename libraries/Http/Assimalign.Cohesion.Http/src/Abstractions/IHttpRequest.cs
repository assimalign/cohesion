using System.IO;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Represents the request data exposed during an HTTP exchange.
/// </summary>
/// <remarks>
/// The protocol core exposes only wire-level data &#8211; method, target, scheme, host,
/// query, headers, and the raw <see cref="Body"/> stream. Identity-aware state
/// (e.g. <c>ClaimsPrincipal</c>), parsed-form access, and typed cookie collections
/// live in higher-layer packages (<c>Assimalign.Cohesion.Web.Authentication</c>,
/// <c>Assimalign.Cohesion.Http.Forms</c>,
/// <c>Assimalign.Cohesion.Http.Cookies</c>) and are surfaced as extension members or
/// features on <see cref="IHttpContext"/>. The raw <c>Cookie</c> header remains
/// accessible through <see cref="Headers"/>.
/// </remarks>
public interface IHttpRequest
{
    /// <summary>
    /// Gets the host associated with the request target.
    /// </summary>
    HttpHost Host { get; }

    /// <summary>
    /// Gets the absolute request path.
    /// </summary>
    HttpPath Path { get; }

    /// <summary>
    /// Gets the HTTP method for the request.
    /// </summary>
    HttpMethod Method { get; }

    /// <summary>
    /// Gets the URI scheme used for the request.
    /// </summary>
    HttpScheme Scheme { get; }

    /// <summary>
    /// Gets the parsed query-string values.
    /// </summary>
    IHttpQueryCollection Query { get; }

    /// <summary>
    /// Gets the request headers.
    /// </summary>
    IHttpHeaderCollection Headers { get; }

    /// <summary>
    /// Gets the request trailer section — the fields that follow the body
    /// (RFC 9110 §6.5). Empty when the request carried no trailers;
    /// <see cref="IHttpTrailerCollection.IsSupported"/> reports whether the
    /// exchange surfaces a trailer section at all.
    /// </summary>
    /// <remarks>
    /// Defined as a default interface member returning the shared unsupported
    /// (empty, read-only) collection so the trailer section could be added to
    /// the core message model without breaking every existing
    /// <see cref="IHttpRequest"/> implementation. The abstract
    /// <see cref="HttpRequest"/> base and the protocol transports override it
    /// with a real collection where trailers are surfaced.
    /// </remarks>
    IHttpTrailerCollection Trailers => HttpTrailerCollection.Unsupported;

    /// <summary>
    /// Get's the context associated with the request.
    /// </summary>
    IHttpContext HttpContext { get; }

    /// <summary>
    /// Gets the body of the request.
    /// </summary>
    Stream Body { get; }
}
