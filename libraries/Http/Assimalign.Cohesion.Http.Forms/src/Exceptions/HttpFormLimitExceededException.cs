using System.IO;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Identifies a form parse that failed because the request body exceeds one of the configured
/// <see cref="HttpFormOptions"/> limits: too many entries or sections, an oversized key, value, multipart section
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
/// <para>
/// Use <see cref="Limit"/> for diagnostics and limit-specific handling. Framework parsers select
/// that value from their configured limits, without deriving it from request input or parsing the
/// exception message. Other form parsing failures can include request input in their messages.
/// </para>
/// </remarks>
public sealed class HttpFormLimitExceededException : HttpException
{
    /// <summary>
    /// Initializes the exception with a message and an <see cref="HttpFormLimit.Unspecified"/> limit.
    /// </summary>
    /// <param name="message">The message naming the limit and its configured value.</param>
    /// <remarks>
    /// This constructor preserves callers that supply only a message. Use
    /// <see cref="HttpFormLimitExceededException(HttpFormLimit, string)"/> to identify the limit
    /// without interpreting message text.
    /// </remarks>
    public HttpFormLimitExceededException(string message)
        : this(HttpFormLimit.Unspecified, message)
    {
    }

    /// <summary>
    /// Initializes the exception with the configured limit that was exceeded and a message.
    /// </summary>
    /// <param name="limit">The limit category, independent of request input.</param>
    /// <param name="message">The message naming the limit and its configured value.</param>
    public HttpFormLimitExceededException(HttpFormLimit limit, string message)
        : base(message)
    {
        Limit = limit;
        Code = HttpErrorCode.ReadingError;
    }

    /// <summary>
    /// Gets the configured limit that was exceeded. Framework parsers always set a specific
    /// category; the message-only constructor reports <see cref="HttpFormLimit.Unspecified"/>.
    /// This value contains no request input and does not depend on the exception message.
    /// </summary>
    public HttpFormLimit Limit { get; }
}
