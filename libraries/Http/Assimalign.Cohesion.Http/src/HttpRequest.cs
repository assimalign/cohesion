using System.IO;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Provides a base abstraction for concrete HTTP request implementations.
/// </summary>
/// <remarks>
/// <para>
/// Collection-typed members (<see cref="Query"/>, <see cref="Headers"/>) are
/// declared with the concrete collection types so transport implementations
/// and derived classes can program against the concrete surface directly
/// &#8211; without re-casting from the interface at every call site. The
/// corresponding members on <see cref="IHttpRequest"/> are implemented
/// explicitly and delegate to these properties so external consumers
/// programming against the interface still see only the interface contract.
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

    /// <inheritdoc cref="IHttpRequest.Query" />
    public abstract HttpQueryCollection Query { get; }

    /// <inheritdoc cref="IHttpRequest.Headers" />
    public abstract HttpHeaderCollection Headers { get; }

    /// <inheritdoc cref="IHttpRequest.Trailers" />
    public virtual HttpTrailerCollection Trailers => HttpTrailerCollection.Unsupported;

    /// <summary>
    /// Gets the context of the exchange this request belongs to.
    /// </summary>
    /// <remarks>
    /// The back-reference is fixed when the request is constructed. The owning context constructs
    /// its request and response and passes itself to each, so an implementation stores the context
    /// in a read-only field and it can never be observed unset or re-parented. A transport that
    /// decodes the request before the context exists hands the decoded values to the context
    /// instead of a finished request: the <c>Assimalign.Cohesion.Http.Connections</c> transports
    /// decode a request head, and each exchange context builds its request from that head.
    /// </remarks>
    public abstract HttpContext HttpContext { get; }

    /// <inheritdoc />
    public abstract Stream Body { get; set; }

    IHttpQueryCollection IHttpRequest.Query => Query;
    IHttpHeaderCollection IHttpRequest.Headers => Headers;
    IHttpTrailerCollection IHttpRequest.Trailers => Trailers;
    IHttpContext IHttpRequest.HttpContext => HttpContext;
}
