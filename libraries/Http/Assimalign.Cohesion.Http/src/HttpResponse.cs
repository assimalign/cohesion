using System.IO;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Provides a base abstraction for concrete HTTP response implementations.
/// </summary>
/// <remarks>
/// <para>
/// The collection-typed <see cref="Headers"/> member is declared with the
/// concrete collection type so transport implementations and derived classes
/// can program against the concrete surface directly &#8211; without re-casting
/// from the interface at every call site. The corresponding member on
/// <see cref="IHttpResponse"/> is implemented explicitly and delegates to this
/// property so external consumers programming against the interface still see
/// only the interface contract.
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
    /// Gets the response trailer section (RFC 9110 §6.5). Defaults to the
    /// shared unsupported (empty, read-only) collection; transports that emit
    /// trailers override this with a supported collection.
    /// </summary>
    public virtual HttpTrailerCollection Trailers => HttpTrailerCollection.Unsupported;

    /// <summary>
    /// Gets the context of the exchange this response belongs to.
    /// </summary>
    /// <remarks>
    /// The back-reference is fixed when the response is constructed: the owning context constructs
    /// its request and response and passes itself to each (see <see cref="HttpRequest.HttpContext"/>),
    /// so an implementation stores the context in a read-only field and it can never be observed
    /// unset or re-parented.
    /// </remarks>
    public abstract HttpContext HttpContext { get; }

    /// <inheritdoc />
    public abstract Stream Body { get; set; }

    IHttpHeaderCollection IHttpResponse.Headers => Headers;
    IHttpTrailerCollection IHttpResponse.Trailers => Trailers;
    IHttpContext IHttpResponse.HttpContext => HttpContext;
}
