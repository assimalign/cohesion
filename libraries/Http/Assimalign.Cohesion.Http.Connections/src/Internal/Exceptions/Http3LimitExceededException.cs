using System.IO;

namespace Assimalign.Cohesion.Http.Connections.Internal;

/// <summary>
/// Raised by the HTTP/3 request-body stream when the request body exceeds the effective per-request
/// body-size cap. Carries the HTTP status the exchange is answered with — <c>413 Content Too Large</c>
/// (RFC 9110 §15.5.14) — the HTTP/3 counterpart of <see cref="Http1LimitExceededException"/>.
/// </summary>
/// <remarks>
/// The body stream records the rejection before throwing, so the transport's send path can answer
/// <c>413</c> when the response head has not been committed and then stop reading the request stream
/// (see docs/DESIGN.md, "Request streams"). Derives from <see cref="IOException"/> so an application
/// reading the body observes the rejection as a body-read failure, exactly as on HTTP/1.1.
/// </remarks>
internal sealed class Http3LimitExceededException : IOException
{
    /// <summary>
    /// Initializes a new limit-exceeded exception.
    /// </summary>
    /// <param name="statusCode">The HTTP status the exchange is answered with.</param>
    /// <param name="message">A diagnostic description of the violated limit.</param>
    public Http3LimitExceededException(HttpStatusCode statusCode, string message)
        : base(message)
    {
        StatusCode = statusCode;
    }

    /// <summary>
    /// Gets the HTTP status the exchange is answered with.
    /// </summary>
    public HttpStatusCode StatusCode { get; }
}
