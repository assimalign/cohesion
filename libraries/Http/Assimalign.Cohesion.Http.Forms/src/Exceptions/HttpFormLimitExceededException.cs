using System.IO;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Identifies a form parse that failed because the request body exceeds one of the configured
/// <see cref="HttpFormOptions"/> limits: too many entries, an oversized key, value, multipart section
/// body or header block, or an overlong multipart boundary.
/// </summary>
/// <remarks>
/// <para>
/// A form parse surfaces every failure as an <see cref="InvalidDataException"/>, so a caller catches one
/// type whichever limit tripped. When the failure is a limit, that exception's
/// <see cref="System.Exception.InnerException"/> is an <see cref="HttpFormLimitExceededException"/>
/// carrying the same message; a malformed body has none. A caller that answers the request tells the two
/// apart by type rather than by message: a body over a limit is content the server is unwilling to
/// process, <c>413 Content Too Large</c> (RFC 9110 §15.5.14), and any other failure is a malformed body,
/// <c>400 Bad Request</c>.
/// </para>
/// <para>
/// <see cref="InvalidDataException"/> is sealed, so the limit cannot be a subtype of it; the inner
/// exception keeps the established contract while making the cause explicit.
/// </para>
/// </remarks>
public sealed class HttpFormLimitExceededException : HttpException
{
    /// <summary>
    /// Initializes the exception with a message naming the limit that was exceeded.
    /// </summary>
    /// <param name="message">The message naming the limit and its configured value.</param>
    public HttpFormLimitExceededException(string message)
        : base(message)
    {
        Code = HttpErrorCode.ReadingError;
    }
}
