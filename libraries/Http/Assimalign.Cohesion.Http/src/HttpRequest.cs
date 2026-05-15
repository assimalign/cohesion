using System.IO;
using System.Security.Claims;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Provides a base abstraction for concrete HTTP request implementations.
/// </summary>
public abstract class HttpRequest : IHttpRequest
{
    /// <inheritdoc />
    public abstract HttpHost Host { get; set; }

    /// <inheritdoc />
    public abstract HttpPath Path { get; set; }

    /// <inheritdoc />
    public abstract HttpMethod Method { get; set; }

    /// <inheritdoc />
    public abstract HttpScheme Scheme { get; set; }

    /// <inheritdoc />
    public abstract IHttpQueryCollection Query { get; }

    /// <inheritdoc />
    public abstract IHttpHeaderCollection Headers { get; }

    /// <inheritdoc />
    public abstract IHttpCookieCollection Cookies { get; }

    /// <inheritdoc />
    public abstract Stream Body { get; set; }

    /// <inheritdoc />
    public abstract ClaimsPrincipal ClaimsPrincipal { get; set; }
}
