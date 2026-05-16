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
    /// Gets the collection of response cookies.
    /// </summary>
    IHttpCookieCollection Cookies { get; }

    /// <summary>
    /// Get's the context associated with the response.
    /// </summary>
    IHttpContext HttpContext { get; }

    /// <summary>
    /// Gets or sets the response body stream.
    /// </summary>
    Stream Body { get; set; }
}
