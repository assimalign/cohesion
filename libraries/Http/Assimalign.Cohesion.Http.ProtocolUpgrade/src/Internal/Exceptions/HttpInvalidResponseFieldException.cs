namespace Assimalign.Cohesion.Http.Internal;

/// <summary>
/// Thrown by the transition head writer when a response field cannot be sent: its name is not a token,
/// or its value holds a control character other than HTAB (RFC 9110 §5.1, §5.5). Carries
/// <see cref="HttpErrorCode.InvalidResponseField"/>, the code the transport's head writers use.
/// </summary>
/// <remarks>
/// It is thrown before the connection is taken over and before any byte is written. The message never
/// quotes the offending text, which may hold CR, LF, or NUL and would forge a log line.
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
