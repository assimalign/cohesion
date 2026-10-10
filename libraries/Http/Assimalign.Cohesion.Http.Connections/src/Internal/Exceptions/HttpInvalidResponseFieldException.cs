namespace Assimalign.Cohesion.Http.Connections.Internal;

/// <summary>
/// Thrown by a response head writer when a field cannot be sent: its name is not a token, or its value
/// holds a control character other than HTAB (RFC 9110 §5.1, §5.5). Carries
/// <see cref="HttpErrorCode.InvalidResponseField"/>.
/// </summary>
/// <remarks>
/// The writer throws it before any byte of the head reaches the wire and before the response is marked
/// as started, so the exchange can still be answered with another response. The message never quotes the
/// offending text, which may hold CR, LF, or NUL and would forge a log line.
/// </remarks>
internal sealed class HttpInvalidResponseFieldException : HttpException
{
    /// <summary>
    /// Initializes the exception.
    /// </summary>
    /// <param name="message">A description that names the violation without quoting the offending text.</param>
    public HttpInvalidResponseFieldException(string message)
        : base(message)
    {
        Code = HttpErrorCode.InvalidResponseField;
    }
}
