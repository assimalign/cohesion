using System.IO;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Represents the request data exposed during an HTTP exchange.
/// </summary>
/// <remarks>
/// The protocol core exposes only wire-level data &#8211; method, target, scheme, host,
/// query, headers, cookies, and the raw <see cref="Body"/> stream. Identity-aware
/// state (e.g. <c>ClaimsPrincipal</c>) and parsed-form access live in higher-layer
/// packages (<c>Assimalign.Cohesion.Web.Authentication</c>, <c>Assimalign.Cohesion.Http.Forms</c>)
/// and are surfaced as extension members or features on
/// <see cref="IHttpContext"/>.
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
    /// Gets the request cookies.
    /// </summary>
    IHttpCookieCollection Cookies { get; }

    /// <summary>
    /// Get's the context associated with the request.
    /// </summary>
    IHttpContext HttpContext { get; }

    /// <summary>
    /// Gets the body of the request.
    /// </summary>
    Stream Body { get; }
}
