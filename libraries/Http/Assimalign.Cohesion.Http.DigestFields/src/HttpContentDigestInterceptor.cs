using System.IO;

using Assimalign.Cohesion.Http.Internal;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// A request-parse interceptor that verifies an inbound <c>Content-Digest</c> against the request
/// body. Opt-in: the composition root installs it via
/// <see cref="HttpContentDigestInterceptor.Create"/>.
/// </summary>
/// <remarks>
/// <para>
/// Stateless — one instance serves every connection and request on the listener. It hooks
/// <c>AfterRequestBody</c> and verifies in one of two modes, chosen per protocol by where the
/// verdict can be produced without violating the hook's CPU-only contract:
/// </para>
/// <para>
/// <b>Eager buffer-and-replay (HTTP/1.1):</b> the hook reads the body in full, compares against
/// every supported digest the field carries, and returns a replay stream so the application still
/// observes the body. A mismatch throws <see cref="HttpRequestRejectedException"/> (<c>400</c>)
/// before dispatch. Safe there because the in-hook read cannot wait on work the hook itself is
/// blocking: the HTTP/1.1 parse path is its own body reader (the peer needs nothing from the server
/// to keep sending), and the transport's minimum data rate bounds how long a slow sender can hold it.
/// </para>
/// <para>
/// <b>Lazy verify-on-read (HTTP/2, HTTP/3, and any version not known to be eager-safe):</b> the
/// hook is CPU-only — it wraps the body in a <see cref="HttpDigestVerifyingStream"/> that hashes
/// incrementally as the application reads and resolves the verdict on the terminal read. The h2
/// hook runs on the connection's single frame pump while the body may still be arriving under
/// flow-control backpressure; an in-hook read would stall the pump that delivers the very DATA it
/// waits for, deadlocking every stream on the connection. The h3 body is read lazily off the QUIC
/// stream after the request's HEADERS frame; an in-hook read would hold a thread-pool thread for the
/// whole upload, with no minimum data rate on HTTP/3 to bound it. A mismatch therefore surfaces as
/// <see cref="HttpContentDigestMismatchException"/> from the application's end-of-body read, and
/// the application aborts the exchange (<see cref="IHttpContext.Cancel"/> → a stream reset).
/// </para>
/// <para>
/// A malformed <c>Content-Digest</c> field is rejected with <c>400</c> in-hook on every protocol —
/// parsing is CPU-only, so the fail-closed pre-dispatch rejection needs no body octet. Verification
/// is over the message content <em>as received</em> (RFC 9530 <c>Content-Digest</c> semantics), so
/// this interceptor must run before any content-decoding interceptor. A request carrying no
/// <c>Content-Digest</c>, or only deprecated/unknown algorithms, passes through untouched.
/// </para>
/// </remarks>
public sealed class HttpContentDigestInterceptor : HttpExchangeInterceptor
{
    private HttpContentDigestInterceptor()
    {
    }

    /// <summary>
    /// Creates the stateless <see cref="IHttpExchangeInterceptor"/> that verifies an inbound
    /// <c>Content-Digest</c> against the request body. On HTTP/1.1 verification is eager and a
    /// mismatch (or a malformed field) is rejected with <c>400 Bad Request</c> before the request is
    /// dispatched; on HTTP/2 and HTTP/3, whose bodies stream in after dispatch, the body is verified
    /// lazily as the application reads it, and a mismatch surfaces as
    /// <see cref="HttpContentDigestMismatchException"/> from the terminal body read (the
    /// malformed-field <c>400</c> stays pre-dispatch there too).
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
    public static HttpContentDigestInterceptor Create() => new HttpContentDigestInterceptor();

    /// <inheritdoc />
    public override HttpInterceptorScopes Scopes => HttpInterceptorScopes.Request;

    /// <inheritdoc />
    /// <exception cref="HttpRequestRejectedException">The digest field is malformed or the HTTP/1.1 body does not match it.</exception>
    public override Stream AfterRequestBody(HttpExchangeInterceptorRequestContext context, Stream body)
    {
        if (!context.Headers.TryGetValue(HttpHeaderKey.ContentDigest, out HttpHeaderValue raw) || raw.IsEmpty)
        {
            return body;
        }

        if (!HttpDigestField.TryParse(raw, out HttpDigestField field))
        {
            // Fail closed: an operator-installed verifier treats a malformed Content-Digest as a
            // client error rather than silently ignoring it (RFC 9530 §2 permits either).
            throw new HttpRequestRejectedException(HttpStatusCode.BadRequest, ErrorMessages.ContentDigestFieldMalformed);
        }

        if (!field.HasSupportedAlgorithm)
        {
            // Only deprecated/unregistered algorithms were offered — nothing this library verifies.
            return body;
        }

        // Eager reading is an optimization (it buys the clean pre-dispatch 400), never assumed:
        // only the protocol whose parse path makes an in-hook full read free takes it. Everything
        // else (HTTP/2, HTTP/3 since it streams request bodies, any future version until proven)
        // gets the lazy wrapper, which is safe everywhere because the hook itself reads nothing.
        if (context.Version is not HttpVersion.Http11)
        {
            return new HttpDigestVerifyingStream(field, body);
        }

        byte[] content = ReadToEnd(body);
        if (field.VerifyContent(content) == HttpDigestVerificationResult.Mismatched)
        {
            throw new HttpRequestRejectedException(
                HttpStatusCode.BadRequest, ErrorMessages.ContentDigestMismatch);
        }

        return new HttpDigestReplayStream(content, body);
    }

    private static byte[] ReadToEnd(Stream body)
    {
        // Read from the current position so a body another interceptor already partially consumed
        // is not double-counted; the verifier is registered first, so it observes the pristine body.
        using var buffer = new MemoryStream();
        body.CopyTo(buffer);
        return buffer.ToArray();
    }
}
