using System.IO;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Provides a base abstraction for concrete HTTP response implementations.
/// </summary>
/// <remarks>
/// <para>
/// Collection-typed members (<see cref="Headers"/>, <see cref="Cookies"/>) are
/// declared with the concrete collection types so transport implementations and
/// derived classes can program against the concrete surface directly &#8211;
/// without re-casting from the interface at every call site. The corresponding
/// members on <see cref="IHttpResponse"/> are implemented explicitly and
/// delegate to these properties so external consumers programming against the
/// interface still see only the interface contract.
/// </para>
/// </remarks>
public abstract class HttpResponse : IHttpResponse
{
    /// <inheritdoc />
    public abstract HttpStatusCode StatusCode { get; set; }

    /// <summary>
    /// Gets the collection of response headers.
    /// </summary>
    public abstract HttpHeaderCollection Headers { get; }

    /// <summary>
    /// Gets the collection of response cookies.
    /// </summary>
    public abstract HttpCookieCollection Cookies { get; }


    /// <inheritdoc />
    public abstract HttpContext HttpContext { get; }

    /// <inheritdoc />
    public abstract Stream Body { get; set; }

    IHttpHeaderCollection IHttpResponse.Headers => Headers;
    IHttpCookieCollection IHttpResponse.Cookies => Cookies;
    IHttpContext IHttpResponse.HttpContext => HttpContext;
}
