using System.IO;
using System.Security.Claims;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Represents the request data exposed during an HTTP exchange.
/// </summary>
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
    /// Gets the parsed form values and uploaded files.
    /// </summary>
    IHttpFormCollection Form { get; }

    /// <summary>
    /// Gets the body of the request.
    /// </summary>
    Stream Body { get; }

    /// <summary>
    /// Gets the authenticated principal associated with the request.
    /// </summary>
    ClaimsPrincipal ClaimsPrincipal { get; }
}
