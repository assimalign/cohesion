using System;

namespace Assimalign.Cohesion.Http.Connections.Internal;

/// <summary>
/// The response-head rule the HTTP/2 and HTTP/3 encoders share: a field that only means something on a
/// single HTTP/1.1 connection never reaches an HTTP/2 or HTTP/3 field section.
/// </summary>
/// <remarks>
/// <para>
/// RFC 9113 §8.2.2 and RFC 9114 §4.2 make a message that carries a connection-specific field
/// (<c>Connection</c>, <c>Keep-Alive</c>, <c>Proxy-Connection</c>, <c>Transfer-Encoding</c>,
/// <c>Upgrade</c>) malformed, and allow <c>TE</c> only with the value <c>trailers</c>; a client may reset
/// the stream that carries one. Applications, middleware written for HTTP/1.1, and proxies set these
/// fields, so the encoders skip them while they encode a response head: buffered, streamed, interim
/// (early hints), and a tunnel's <c>200</c> (#1328). The application's header collection is left as it
/// was; only the wire loses the field.
/// </para>
/// <para>
/// A trailer section needs no filter here: <see cref="HttpTrailerFieldRules"/> rejects these fields when
/// the application stages them.
/// </para>
/// </remarks>
internal static class HttpResponseFieldRules
{
    /// <summary>
    /// Determines whether a response field may be encoded into an HTTP/2 or HTTP/3 response head.
    /// </summary>
    /// <param name="key">The field name.</param>
    /// <param name="value">The field value.</param>
    /// <returns>
    /// <see langword="false"/> for a connection-specific field, and for <c>TE</c> with any value other
    /// than <c>trailers</c>; otherwise <see langword="true"/>.
    /// </returns>
    public static bool IsSendable(HttpHeaderKey key, HttpHeaderValue value)
    {
        if (HttpFieldNormalization.IsForbiddenInHttp2Or3(key))
        {
            return false;
        }

        // An empty TE says nothing and a strict client rejects any value but "trailers", so only that
        // value goes out.
        return key != HttpHeaderKey.TE || string.Equals(value.Value, "trailers", StringComparison.OrdinalIgnoreCase);
    }
}
