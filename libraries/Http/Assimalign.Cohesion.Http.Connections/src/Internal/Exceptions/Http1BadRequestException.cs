using System.IO;

namespace Assimalign.Cohesion.Http.Connections.Internal;

/// <summary>
/// Raised by the HTTP/1.1 head reader for a request it answers with <c>400 Bad Request</c> before it
/// closes the connection: a field line whose name is not a token, such as one with whitespace before
/// its colon (RFC 9112 §5.1); a field value with a control character other than HTAB, a bare CR or LF
/// among them (RFC 9110 §5.5); or a request line with an octet other than VCHAR and SP (RFC 9112 §3).
/// </summary>
/// <remarks>
/// Derives from <see cref="IOException"/>, like <see cref="Http1LimitExceededException"/>, so that if it
/// ever escapes the dedicated catch in <c>Http1ConnectionContext</c> it still degrades to the
/// per-connection wire-level-failure path (the connection is dropped) rather than faulting the host.
/// </remarks>
internal sealed class Http1BadRequestException : IOException
{
    /// <summary>
    /// Initializes a new bad-request exception.
    /// </summary>
    /// <param name="message">A diagnostic description of what is malformed.</param>
    public Http1BadRequestException(string message)
        : base(message)
    {
    }
}
