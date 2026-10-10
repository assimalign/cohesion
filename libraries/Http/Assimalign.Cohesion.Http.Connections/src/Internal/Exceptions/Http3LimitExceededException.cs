using System.IO;

namespace Assimalign.Cohesion.Http.Connections.Internal;

/// <summary>
/// Raised when an HTTP/3 request exceeds a limit the server answers with a status: the request-body
/// stream's per-request body-size cap (<c>413 Content Too Large</c>, RFC 9110 §15.5.14), its minimum
/// data rate (<c>408 Request Timeout</c>, RFC 9110 §15.5.9, #1085), or the QPACK decoder's
/// <c>SETTINGS_MAX_FIELD_SECTION_SIZE</c> on a request head or trailer section
/// (<c>431 Request Header Fields Too Large</c>, RFC 9114 §4.2.2). The HTTP/3 counterpart of
/// <see cref="Http1LimitExceededException"/>.
/// </summary>
/// <remarks>
/// A request head over the field-section limit never becomes an exchange, so the transport answers
/// <c>431</c> itself. A rejection found while the body is read is recorded by the body stream before it
/// is thrown, so the transport's send path can answer the status when the response head has not been
/// committed and then stop reading the request stream (see docs/DESIGN.md, "Request streams"). Derives
/// from <see cref="IOException"/> so an application reading the body observes the rejection as a
/// body-read failure, exactly as on HTTP/1.1.
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
