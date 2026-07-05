using System;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Thrown by an <see cref="IHttpRequestInterceptor"/> to reject the request currently being
/// parsed. The server transport answers the peer with the carried <see cref="StatusCode"/>
/// (a minimal, bodyless response) and closes the connection.
/// </summary>
/// <remarks>
/// <para>
/// Only error statuses (4xx / 5xx) may be carried — a rejection is a refusal, not a response.
/// The transport catches this type explicitly on its parse path, so a rejection is always
/// answered rather than being classified (and silently swallowed) as a wire-level failure.
/// After a rejection the connection is not reused: the request's remaining wire state is
/// indeterminate, so keep-alive would desynchronize the framing.
/// </para>
/// </remarks>
public sealed class HttpRequestRejectedException : HttpException
{
    /// <summary>
    /// Initializes a new rejection with the supplied status code.
    /// </summary>
    /// <param name="statusCode">The 4xx/5xx status code to answer with.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="statusCode"/> is not an error status (4xx / 5xx).
    /// </exception>
    public HttpRequestRejectedException(HttpStatusCode statusCode)
        : this(statusCode, $"The request was rejected with status '{statusCode}'.")
    {
    }

    /// <summary>
    /// Initializes a new rejection with the supplied status code and diagnostic message.
    /// </summary>
    /// <param name="statusCode">The 4xx/5xx status code to answer with.</param>
    /// <param name="message">A diagnostic description of why the request was rejected.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="statusCode"/> is not an error status (4xx / 5xx).
    /// </exception>
    public HttpRequestRejectedException(HttpStatusCode statusCode, string message)
        : base(message)
    {
        if (statusCode.Value is < 400 or > 599)
        {
            throw new ArgumentOutOfRangeException(
                nameof(statusCode),
                statusCode.Value,
                "A request rejection must carry an error status (4xx or 5xx).");
        }

        StatusCode = statusCode;
    }

    /// <summary>
    /// Gets the status code the transport answers the peer with before closing the connection.
    /// </summary>
    public HttpStatusCode StatusCode { get; }
}
