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

    /// <summary>
    /// Determines whether a request is an <em>extended CONNECT</em> (RFC 8441 /
    /// RFC 9220): a <c>CONNECT</c> request that carries the <c>:protocol</c>
    /// pseudo-header. Shared by HTTP/2 and HTTP/3 so both recognize the
    /// extension identically.
    /// </summary>
    /// <param name="method">The decoded <c>:method</c> value, or <see langword="null"/>.</param>
    /// <param name="protocol">The decoded <c>:protocol</c> value, or <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when the request is an extended CONNECT.</returns>
    public static bool IsExtendedConnect(string? method, string? protocol)
    {
        return !string.IsNullOrEmpty(protocol)
            && string.Equals(method, HttpMethod.Connect.Value, StringComparison.Ordinal);
    }

    /// <summary>
    /// Validates the <c>:protocol</c> pseudo-header against the extended CONNECT
    /// rules shared by HTTP/2 and HTTP/3 (RFC 8441 §4, RFC 9220 §3): the
    /// <c>:protocol</c> field is only valid on a <c>CONNECT</c> request, and an
    /// extended CONNECT MUST also include <c>:scheme</c>, <c>:path</c>, and
    /// <c>:authority</c>. Returns a description of the violation, or
    /// <see langword="null"/> when the request is well-formed (including the
    /// common case where <c>:protocol</c> is absent).
    /// </summary>
    /// <param name="method">The decoded <c>:method</c> value, or <see langword="null"/>.</param>
    /// <param name="scheme">The decoded <c>:scheme</c> value, or <see langword="null"/>.</param>
    /// <param name="path">The decoded <c>:path</c> value, or <see langword="null"/>.</param>
    /// <param name="authority">The decoded <c>:authority</c> value, or <see langword="null"/>.</param>
    /// <param name="protocol">The decoded <c>:protocol</c> value, or <see langword="null"/>.</param>
    /// <returns>
    /// A human-readable violation message, or <see langword="null"/> when valid.
    /// Callers raise their own protocol-appropriate error from the message.
    /// </returns>
    public static string? ValidateExtendedConnect(string? method, string? scheme, string? path, string? authority, string? protocol)
    {
        if (string.IsNullOrEmpty(protocol))
        {
            // No :protocol field — not an extended CONNECT; nothing to validate.
            return null;
        }

        // RFC 8441 §4 — the :protocol pseudo-header is only defined on a
        // CONNECT request; on any other method the request is malformed.
        if (!string.Equals(method, HttpMethod.Connect.Value, StringComparison.Ordinal))
        {
            return "The ':protocol' pseudo-header is only valid on a CONNECT request (RFC 8441 §4).";
        }

        // RFC 8441 §4 — unlike a classic CONNECT, an extended CONNECT carries a
        // normal request shape and MUST include :scheme, :path, and :authority.
        if (string.IsNullOrEmpty(scheme))
        {
            return "An extended CONNECT request MUST include the ':scheme' pseudo-header (RFC 8441 §4).";
        }

        if (string.IsNullOrEmpty(path))
        {
            return "An extended CONNECT request MUST include the ':path' pseudo-header (RFC 8441 §4).";
        }

        if (string.IsNullOrEmpty(authority))
        {
            return "An extended CONNECT request MUST include the ':authority' pseudo-header (RFC 8441 §4).";
        }

        return null;
    }
}
