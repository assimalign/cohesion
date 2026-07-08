using System.IO;

namespace Assimalign.Cohesion.Http.Internal;

/// <summary>
/// A request-parse interceptor that verifies an inbound <c>Content-Digest</c> against the request
/// body and rejects a mismatch with <c>400 Bad Request</c>. Opt-in: the composition root installs
/// it via <see cref="HttpDigestFields.CreateContentDigestVerifier"/>.
/// </summary>
/// <remarks>
/// <para>
/// Stateless — one instance serves every connection and request on the listener. It hooks
/// <c>AfterRequestBody</c>: by the time that hook runs the transport has materialized the request
/// body, so the verifier reads it in full (to hash it), compares against every supported digest the
/// field carries, and returns a replay stream so the application still observes the body. A
/// mismatch, or a malformed <c>Content-Digest</c> field, throws
/// <see cref="HttpRequestRejectedException"/> before dispatch, which the transport answers with the
/// carried status and then closes the connection.
/// </para>
/// <para>
/// Verification is over the message content <em>as received</em> (RFC 9530 <c>Content-Digest</c>
/// semantics), so this interceptor must run before any content-decoding interceptor. A request
/// carrying no <c>Content-Digest</c>, or only deprecated/unknown algorithms, passes through
/// untouched.
/// </para>
/// </remarks>
internal sealed class HttpContentDigestInterceptor : IHttpRequestInterceptor
{
    /// <inheritdoc />
    public Stream AfterRequestBody(HttpRequestInterceptorContext context, Stream body)
    {
        if (!context.Headers.TryGetValue(HttpHeaderKey.ContentDigest, out HttpHeaderValue raw) || raw.IsEmpty)
        {
            return body;
        }

        if (!HttpDigestField.TryParse(raw, out HttpDigestField field))
        {
            // Fail closed: an operator-installed verifier treats a malformed Content-Digest as a
            // client error rather than silently ignoring it (RFC 9530 §2 permits either).
            throw new HttpRequestRejectedException(HttpStatusCode.BadRequest, "The Content-Digest field is malformed.");
        }

        if (!field.HasSupportedAlgorithm)
        {
            // Only deprecated/unregistered algorithms were offered — nothing this library verifies.
            return body;
        }

        byte[] content = ReadToEnd(body);
        if (field.VerifyContent(content) == HttpDigestVerificationResult.Mismatched)
        {
            throw new HttpRequestRejectedException(
                HttpStatusCode.BadRequest, "The request body does not match its Content-Digest.");
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
