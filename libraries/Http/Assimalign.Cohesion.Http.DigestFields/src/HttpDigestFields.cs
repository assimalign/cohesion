using Assimalign.Cohesion.Http.Internal;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Entry points for the RFC 9530 digest-fields feature package: the server-side
/// <c>Content-Digest</c> verifier the composition root installs on its listener.
/// </summary>
public static class HttpDigestFields
{
    /// <summary>
    /// Creates the stateless <see cref="IHttpRequestInterceptor"/> that verifies an inbound
    /// <c>Content-Digest</c> against the request body and rejects a mismatch (or a malformed field)
    /// with <c>400 Bad Request</c> before the request is dispatched.
    /// </summary>
    /// <remarks>
    /// Register it <em>before</em> any content-decoding interceptor, because <c>Content-Digest</c>
    /// is taken over the message content as received. A request with no <c>Content-Digest</c>, or
    /// one offering only deprecated/unregistered algorithms, is passed through unverified.
    /// </remarks>
    /// <returns>The content-digest verification interceptor.</returns>
    public static IHttpExchangeInterceptor CreateContentDigestVerifier() => new HttpContentDigestInterceptor();
}
