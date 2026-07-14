namespace Assimalign.Cohesion.Http;

/// <summary>
/// Thrown when the received message content does not match a digest carried by its
/// <c>Content-Digest</c> field (RFC 9530 &#167; 2). On a streamed request body verified lazily
/// (HTTP/2), this surfaces on the <em>terminal</em> body read — the read that observes
/// end-of-body — because the verdict cannot exist until every content octet has been hashed.
/// Inherits the HTTP area exception root <see cref="HttpException"/>.
/// </summary>
/// <remarks>
/// <para>
/// By the time this exception is thrown the application has already consumed the (tampered or
/// corrupt) content, so it cannot become a clean pre-dispatch <c>400 Bad Request</c> the way an
/// eagerly verified mismatch does. The application must discard whatever it derived from the body
/// and abort the exchange (<see cref="IHttpContext.Cancel"/>), which the transport answers with
/// its per-exchange reset (HTTP/2 <c>RST_STREAM(CANCEL)</c>) instead of a response.
/// </para>
/// <para>
/// The exception is sticky: every subsequent read of the verified body stream rethrows it, so a
/// consumer that swallows the first failure cannot accidentally treat the body as verified.
/// </para>
/// </remarks>
public sealed class HttpContentDigestMismatchException : HttpException
{
    /// <summary>
    /// Initializes a new <see cref="HttpContentDigestMismatchException"/>.
    /// </summary>
    /// <param name="algorithm">The digest algorithm whose declared digest the content failed.</param>
    public HttpContentDigestMismatchException(HttpDigestAlgorithm algorithm)
        : base($"The request body does not match its Content-Digest ('{algorithm}').")
    {
        Algorithm = algorithm;
        Code = HttpErrorCode.ReadingError;
    }

    /// <summary>
    /// Gets the digest algorithm whose declared digest the content failed. When the field carried
    /// several supported digests, this is the first mismatching one in field order.
    /// </summary>
    public HttpDigestAlgorithm Algorithm { get; }
}
