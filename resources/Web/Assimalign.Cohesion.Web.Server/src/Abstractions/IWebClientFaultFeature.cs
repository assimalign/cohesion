using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web;

/// <summary>
/// Reports that the client's request was at fault: the status the transport answers the exchange with
/// because reading the request failed on the client's side.
/// </summary>
/// <remarks>
/// <para>
/// The server dispatches a request at its head, so some of the client's faults surface only while the
/// application reads the body: a malformed body (broken chunked framing, a malformed trailer section),
/// a body over a configured limit (its size, its data rate, the bounds on a trailer section), or a body
/// the client cut short by closing the connection. The read throws as any stream read does, an
/// <see cref="System.IO.InvalidDataException"/> or an <see cref="System.IO.IOException"/> (an
/// <see cref="System.IO.EndOfStreamException"/> for a body cut short), and the transport answers the
/// exchange itself with a <c>4xx</c> status in place of a response that has not started, then, on
/// HTTP/1.1, closes the connection. On HTTP/2 and HTTP/3 a body that contradicts its
/// <c>content-length</c>, or a malformed trailer section, makes the request malformed, and the transport
/// resets the stream instead: no status reaches the wire, and the feature reports <c>400</c>, as HTTP/1.1
/// answers a malformed body.
/// </para>
/// <para>
/// This feature tells the code that observes the exception that the client was at fault, so it is not
/// treated as an application defect. A fault boundary renders no <c>500</c> and reports no fault to
/// the application's fault observer, an access log records the exchange with the status that was sent
/// rather than at <c>Error</c>, and a middleware that relabels body-read failures leaves the
/// transport's own failure alone. A response that had already started when the read failed keeps its
/// status on the wire, and is ended rather than replaced.
/// </para>
/// <para>
/// The default Web server installs this feature, before the pipeline runs, on every exchange whose request
/// can have a body: on HTTP/1.1 one that declares a <c>Transfer-Encoding</c> or a <c>Content-Length</c>
/// above zero, on HTTP/2 one whose head did not end the stream, on HTTP/3 one whose request stream had not
/// ended with its head, and never a <c>CONNECT</c>. It reports <see langword="null"/> until a fault is
/// latched. A custom <see cref="IWebApplicationServer"/> may omit it, so consumers treat an absent feature
/// as "no client fault reported".
/// </para>
/// </remarks>
public interface IWebClientFaultFeature : IHttpFeature
{
    /// <summary>
    /// Gets the status the transport answers the exchange with because the client's request was at
    /// fault, or reports for it when an HTTP/2 or HTTP/3 transport resets the stream instead —
    /// <c>400 Bad Request</c> for a malformed body or one cut short, <c>413 Content Too Large</c>
    /// over the body-size cap, <c>408 Request Timeout</c> below the minimum data rate, or
    /// <c>431 Request Header Fields Too Large</c> for a trailer section over its bounds — or
    /// <see langword="null"/> when no client fault has been reported.
    /// </summary>
    /// <remarks>
    /// Once it reports a status, it reports the same status for the rest of the exchange.
    /// </remarks>
    HttpStatusCode? StatusCode { get; }
}
