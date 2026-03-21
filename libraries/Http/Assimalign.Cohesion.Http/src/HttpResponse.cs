using System.IO;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Provides a base abstraction for concrete HTTP response implementations.
/// </summary>
public abstract class HttpResponse : IHttpResponse
{
    /// <inheritdoc />
    public abstract HttpStatusCode StatusCode { get; set; }

    /// <inheritdoc />
    public abstract IHttpHeaderCollection Headers { get; }

    /// <inheritdoc />
    public abstract IHttpCookieCollection Cookies { get; }

    /// <inheritdoc />
    public abstract Stream Body { get; set; }
}
