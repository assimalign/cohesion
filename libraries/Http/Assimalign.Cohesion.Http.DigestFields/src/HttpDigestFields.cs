using Assimalign.Cohesion.Http.Internal;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Entry points for the RFC 9530 digest-fields feature package: the server-side
/// <c>Content-Digest</c> verifier the composition root installs on its listener.
/// </summary>
public static class HttpDigestFields
{
    /// <summary>
    /// Creates the stateless <see cref="IHttpExchangeInterceptor"/> that verifies an inbound
    /// <c>Content-Digest</c> against the request body. On HTTP/1.1 and HTTP/3 verification is
    /// eager and a mismatch (or a malformed field) is rejected with <c>400 Bad Request</c> before
    /// the request is dispatched; on HTTP/2 the body is verified lazily as the application reads
    /// it, and a mismatch surfaces as <see cref="HttpContentDigestMismatchException"/> from the
    /// terminal body read (the malformed-field <c>400</c> stays pre-dispatch there too).
    /// </summary>
    /// <remarks>
    /// Register it <em>before</em> any content-decoding interceptor, because <c>Content-Digest</c>
    /// is taken over the message content as received. A request with no <c>Content-Digest</c>, or
    /// one offering only deprecated/unregistered algorithms, is passed through unverified — as is
    /// (on the lazy path) any part of a body the application never reads: verification can only
    /// cover what is consumed. An application that catches
    /// <see cref="HttpContentDigestMismatchException"/> must treat the body as corrupt and abort
    /// the exchange (<see cref="IHttpContext.Cancel"/>).
    /// </remarks>
    /// <returns>The content-digest verification interceptor.</returns>
    public static IHttpExchangeInterceptor CreateContentDigestVerifier() => new HttpContentDigestInterceptor();
}
