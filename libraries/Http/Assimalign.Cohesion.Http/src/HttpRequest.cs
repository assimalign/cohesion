using System.IO;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Provides a base abstraction for concrete HTTP request implementations.
/// </summary>
/// <remarks>
/// <para>
/// Collection-typed members (<see cref="Query"/>, <see cref="Headers"/>,
/// <see cref="Cookies"/>) are declared with the concrete collection types so
/// transport implementations and derived classes can program against the
/// concrete surface directly &#8211; without re-casting from the interface
/// at every call site. The corresponding members on <see cref="IHttpRequest"/>
/// are implemented explicitly and delegate to these properties so external
/// consumers programming against the interface still see only the interface
/// contract.
/// </para>
/// </remarks>
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

    /// <summary>
    /// Gets the parsed query-string values.
    /// </summary>
    public abstract HttpQueryCollection Query { get; }

    /// <summary>
    /// Gets the request headers.
    /// </summary>
    public abstract HttpHeaderCollection Headers { get; }

    /// <summary>
    /// Gets the request cookies.
    /// </summary>
    public abstract HttpCookieCollection Cookies { get; }

    /// <inheritdoc />
    public abstract Stream Body { get; set; }

    IHttpQueryCollection IHttpRequest.Query => Query;
    IHttpHeaderCollection IHttpRequest.Headers => Headers;
    IHttpCookieCollection IHttpRequest.Cookies => Cookies;
}
