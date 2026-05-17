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
    /// Get's the context associated with the request.
    /// </summary>
    IHttpContext HttpContext { get; }

    /// <summary>
    /// Gets the body of the request.
    /// </summary>
    Stream Body { get; }
}
