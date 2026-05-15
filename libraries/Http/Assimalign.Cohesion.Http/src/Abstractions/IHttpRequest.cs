using System.IO;
using System.Security.Claims;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Represents the request data exposed during an HTTP exchange.
/// </summary>
/// <remarks>
/// The protocol core exposes only wire-level data &#8211; method, target, scheme, host,
/// query, headers, cookies, and the raw <see cref="Body"/> stream. Parsed-form access
/// lives in <c>Assimalign.Cohesion.Http.Forms</c> as an extension method that reads
/// the body and caches the result; the protocol core does not require any
/// form-body parser to be wired up.
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
    /// Gets the body of the request.
    /// </summary>
    Stream Body { get; }

    /// <summary>
    /// Gets the authenticated principal associated with the request.
    /// </summary>
    ClaimsPrincipal ClaimsPrincipal { get; }
}
