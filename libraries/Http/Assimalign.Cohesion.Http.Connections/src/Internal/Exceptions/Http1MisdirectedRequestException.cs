using System.IO;

namespace Assimalign.Cohesion.Http.Connections.Internal;

/// <summary>
/// Raised by the HTTP/1.1 head reader for an absolute-form request-target with the <c>https</c> scheme,
/// such as <c>GET https://example.com/ HTTP/1.1</c>, received over a connection that is not secured. An
/// origin server must reject such a request (RFC 9110 §4.2.2, §7.4); the transport answers it with
/// <c>421 Misdirected Request</c> (RFC 9110 §15.5.20) before any interceptor or the application sees it,
/// then closes the connection.
/// </summary>
/// <remarks>
/// Derives from <see cref="IOException"/>, like <see cref="Http1BadRequestException"/>, so that if it ever
/// escapes the dedicated catch in <c>Http1ConnectionContext</c> it still degrades to the per-connection
/// wire-level-failure path (the connection is dropped) rather than faulting the host.
/// </remarks>
internal sealed class Http1MisdirectedRequestException : IOException
{
    /// <summary>
    /// Initializes a new misdirected-request exception.
    /// </summary>
    /// <param name="message">A diagnostic description of the target and the connection it arrived on.</param>
    public Http1MisdirectedRequestException(string message)
        : base(message)
    {
    }
}
