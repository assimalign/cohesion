using System;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Cross-version normalization operations that translate the shared HTTP
/// concepts — authority, repeated-field combining, and the connection-specific
/// field rules — consistently across HTTP/1.1, HTTP/2, and HTTP/3, so the
/// transports do not each re-encode the version quirks.
/// </summary>
/// <remarks>
/// <para>
/// This is the operational layer over <see cref="HttpFieldRules"/> (which
/// classifies field <em>names</em>): it composes those classifications into the
/// actual translation operations a transport performs while building an
/// <see cref="IHttpRequest"/> / <see cref="IHttpResponse"/> from a wire field
/// section. Keeping it in one place is what lets HTTP/2 and HTTP/3 behave
/// identically for authority resolution, cookie coalescing, and
/// connection-specific rejection.
/// </para>
/// <para>
/// The classification methods return booleans rather than throwing so each
/// transport can raise its own protocol-appropriate error (HTTP/2
/// <c>PROTOCOL_ERROR</c>, HTTP/3 <c>H3_MESSAGE_ERROR</c>, etc.).
/// </para>
/// </remarks>
public static class HttpFieldNormalization
{
    /// <summary>
    /// Resolves the message authority from the version-specific source with
    /// the correct precedence: an explicit authority (the HTTP/2 / HTTP/3
    /// <c>:authority</c> pseudo-header, or the HTTP/1.1 absolute-form target
    /// authority) supersedes the <c>Host</c> header (RFC 9112 §3.2.2,
    /// RFC 9113 §8.3.1, RFC 9114 §4.3.1). Falls back to <c>Host</c>, then to
    /// <see cref="HttpHost.Empty"/>.
    /// </summary>
    /// <param name="authority">The explicit authority, or <see langword="null"/>.</param>
    /// <param name="headers">The message headers (consulted for <c>Host</c>).</param>
    /// <returns>The resolved host.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="headers"/> is <see langword="null"/>.</exception>
    public static HttpHost ResolveAuthority(string? authority, IHttpHeaderCollection headers)
    {
        ArgumentNullException.ThrowIfNull(headers);

        if (!string.IsNullOrWhiteSpace(authority))
        {
            return new HttpHost(authority);
        }

        if (headers.TryGetValue(HttpHeaderKey.Host, out HttpHeaderValue host) && !host.IsEmpty)
        {
            return new HttpHost(host.Value);
        }

        return HttpHost.Empty;
    }

    /// <summary>
    /// Determines whether a field is forbidden in an HTTP/2 or HTTP/3 field
    /// section because it is connection-specific (RFC 9113 §8.2.2,
    /// RFC 9114 §4.2). <c>TE</c> is intentionally excluded here — it is allowed
    /// with a restricted value; use <see cref="IsTeValueValidInHttp2Or3"/>.
    /// </summary>
    /// <param name="key">The field name.</param>
    /// <returns><see langword="true"/> when the field must be rejected.</returns>
    public static bool IsForbiddenInHttp2Or3(HttpHeaderKey key)
    {
        return HttpFieldRules.IsConnectionSpecific(key);
    }

    /// <summary>
    /// Determines whether a <c>TE</c> field value is valid in an HTTP/2 or
    /// HTTP/3 field section. <c>TE</c> may only carry the value <c>trailers</c>
    /// (RFC 9113 §8.2.2, RFC 9114 §4.2); an empty value is treated as absent.
    /// </summary>
    /// <param name="value">The <c>TE</c> field value.</param>
    /// <returns><see langword="true"/> when the value is acceptable.</returns>
    public static bool IsTeValueValidInHttp2Or3(HttpHeaderValue value)
    {
        return value.IsEmpty || string.Equals(value.Value, "trailers", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Combines a repeated field line into an existing value using the
    /// version-neutral rule: the request <c>Cookie</c> field coalesces with a
    /// "; " separator (RFC 9113 §8.2.3, RFC 9114 §4.2.1); <c>Set-Cookie</c> and
    /// other list-valued fields are kept as distinct values (never folded into
    /// one comma line for <c>Set-Cookie</c> — see
    /// <see cref="HttpFieldRules.ProhibitsCombining"/>).
    /// </summary>
    /// <param name="key">The field name.</param>
    /// <param name="existing">The value already accumulated for the field.</param>
    /// <param name="incoming">The newly decoded value to combine.</param>
    /// <returns>The combined field value.</returns>
    public static HttpHeaderValue CombineFieldValue(HttpHeaderKey key, HttpHeaderValue existing, HttpHeaderValue incoming)
    {
        if (string.Equals(key.Value, "Cookie", StringComparison.OrdinalIgnoreCase))
        {
            return new HttpHeaderValue(string.Concat(existing.Value, "; ", incoming.Value));
        }

        // Set-Cookie and ordinary list fields become multiple distinct values.
        // HttpHeaderValue preserves them separately; its Value comma-joins list
        // fields on demand while Set-Cookie callers iterate the distinct values.
        return HttpHeaderValue.Concat(existing, incoming);
    }
}
