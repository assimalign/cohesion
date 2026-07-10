using System.IO;

namespace Assimalign.Cohesion.Http.Connections.Internal.Http1;

/// <summary>
/// Raised by the HTTP/1.1 read path when a request violates a configured
/// <see cref="Http1ConnectionListenerOptions.Http1Limits"/> bound (request-line size, header count / total size, or body
/// size). Carries the HTTP status code the transport emits to the peer before closing the
/// connection (414 / 431 / 413).
/// </summary>
/// <remarks>
/// Derives from <see cref="IOException"/> (<see cref="InvalidDataException"/> is sealed) so that
/// if a limit rejection is ever not handled by the dedicated catch in
/// <c>Http1ConnectionContext</c>, it still degrades to the existing per-connection
/// wire-level-failure path (the connection is dropped) rather than escaping the receive loop and
/// faulting the host.
/// </remarks>
internal sealed class Http1LimitExceededException : IOException
{
    /// <summary>
    /// Initializes a new limit-exceeded exception.
    /// </summary>
    /// <param name="statusCode">The HTTP status code to emit before closing the connection.</param>
    /// <param name="message">A diagnostic description of the violated limit.</param>
    public Http1LimitExceededException(HttpStatusCode statusCode, string message)
        : base(message)
    {
        StatusCode = statusCode;
    }

    /// <summary>
    /// Gets the HTTP status code the transport emits in response to the limit violation.
    /// </summary>
    public HttpStatusCode StatusCode { get; }
}
