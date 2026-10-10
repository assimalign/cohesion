using System;
using System.Buffers;

using Assimalign.Cohesion.Http.Internal;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Cross-version normalization operations that translate the shared HTTP
/// concepts — authority, repeated-field combining, the connection-specific
/// field rules, and the field name and value syntax — consistently across
/// HTTP/1.1, HTTP/2, and HTTP/3, so the transports do not each re-encode the
/// version quirks.
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
/// <para>
/// The field syntax rule (<see cref="IsValidFieldName"/>,
/// <see cref="IsValidFieldValue"/>, <see cref="IndexOfInvalidControlCharacter"/>)
/// is the single rule for a field line, received or sent: a value that two
/// parsers split differently is how requests are smuggled and responses split.
/// The HTTP/1.1 reader applies it to headers and trailers; the response writers
/// (#1183) and the HTTP/2 and HTTP/3 decoders (#1376) adopt it. The members take
/// spans and allocate nothing.
/// </para>
/// </remarks>
public static class HttpFieldNormalization
{
    // CTL (RFC 5234 §B.1: %x00-1F / %x7F) without HTAB, which RFC 9110 §5.5 allows inside a field
    // value. NUL, CR and LF are among them.
    private static readonly SearchValues<char> _invalidControlCharacters = SearchValues.Create(
        "\u0000\u0001\u0002\u0003\u0004\u0005\u0006\u0007\u0008\u000A\u000B\u000C\u000D\u000E\u000F" +
        "\u0010\u0011\u0012\u0013\u0014\u0015\u0016\u0017\u0018\u0019\u001A\u001B\u001C\u001D\u001E\u001F\u007F");

    /// <summary>
    /// Determines whether <paramref name="name"/> is a valid field name: a
    /// <c>token</c>, one or more <c>tchar</c> (RFC 9110 §5.1, §5.6.2).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A token excludes every character an HTTP/1.1 parser treats as a delimiter
    /// (SP, HTAB, <c>:</c>, CR, LF), every other control character, and anything
    /// outside US-ASCII, so a valid name cannot end a field line early or move
    /// the colon. An empty name is invalid.
    /// </para>
    /// <para>
    /// The rule is version-neutral. HTTP/2 and HTTP/3 additionally require a
    /// lowercase name (RFC 9113 §8.2.1, RFC 9114 §4.2) and check that themselves.
    /// A pseudo-header (<c>:path</c>, <c>:status</c>, …) is not a field name and
    /// is not judged here.
    /// </para>
    /// </remarks>
    /// <param name="name">The field name, exactly as received or as it will be sent.</param>
    /// <returns><see langword="true"/> when <paramref name="name"/> is a non-empty token.</returns>
    public static bool IsValidFieldName(ReadOnlySpan<char> name)
    {
        return HttpFieldSyntax.IsToken(name);
    }

    /// <summary>
    /// Determines whether <paramref name="value"/> passes the field-value rule
    /// every version shares: no NUL, CR, or LF anywhere, and no leading or
    /// trailing SP or HTAB (RFC 9110 §5.5, RFC 9113 §8.2.1, RFC 9114 §4.2). An
    /// empty value is valid.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the minimum a recipient MUST enforce and a sender MUST NOT
    /// violate. CR and LF end an HTTP/1.1 field line, so a value carrying either
    /// is read as two fields by a parser that honors them and as one by a parser
    /// that does not; NUL ends a string in many implementations. RFC 9113 and
    /// RFC 9114 make a value that starts or ends with whitespace malformed,
    /// because an HTTP/1.1 hop would strip it as optional whitespace.
    /// </para>
    /// <para>
    /// RFC 9110 §5.5 also calls a value with any other control character
    /// invalid, though a recipient may retain one. A caller that rejects those
    /// too pairs this check with <see cref="IndexOfInvalidControlCharacter"/>.
    /// </para>
    /// <para>
    /// The check is over characters, not octets. A character above U+00FF is not
    /// judged: a transport that decodes octets as Latin-1 never produces one, and
    /// an encoder decides for itself how it writes one.
    /// </para>
    /// </remarks>
    /// <param name="value">The field value, after any transport framing is removed.</param>
    /// <returns><see langword="true"/> when <paramref name="value"/> may be received or sent.</returns>
    public static bool IsValidFieldValue(ReadOnlySpan<char> value)
    {
        if (value.IsEmpty)
        {
            return true;
        }

        if (IsOptionalWhitespace(value[0]) || IsOptionalWhitespace(value[^1]))
        {
            return false;
        }

        return value.IndexOfAny('\0', '\r', '\n') < 0;
    }

    /// <summary>
    /// Returns the index of the first control character in
    /// <paramref name="value"/> that a field value cannot carry: any CTL
    /// (<c>%x00-1F</c> / <c>%x7F</c>, RFC 5234 §B.1) other than HTAB, which
    /// RFC 9110 §5.5 allows between visible characters.
    /// </summary>
    /// <remarks>
    /// NUL, CR, and LF are included, so a value with no such character also has
    /// none of the characters <see cref="IsValidFieldValue"/> rejects; leading
    /// and trailing whitespace is not judged here. HTTP/1.1 applies this rule to
    /// a received value once its optional whitespace is trimmed (RFC 9112 §5.1).
    /// </remarks>
    /// <param name="value">The field value to scan.</param>
    /// <returns>
    /// The zero-based index of the first such character, or <c>-1</c> when there is none.
    /// </returns>
    public static int IndexOfInvalidControlCharacter(ReadOnlySpan<char> value)
    {
        return value.IndexOfAny(_invalidControlCharacters);
    }

    private static bool IsOptionalWhitespace(char character)
    {
        // RFC 9110 §5.6.3 — OWS is SP / HTAB, nothing else.
        return character is ' ' or '\t';
    }

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
    /// <remarks>
    /// A list field combines in amortized constant time (<see cref="HttpHeaderValue.Concat(HttpHeaderValue, HttpHeaderValue)"/>).
    /// A <c>Cookie</c> crumb is joined onto the whole value so far, which a string cannot do in place, so
    /// combining <c>n</c> crumbs one at a time copies quadratically. A caller that folds a whole field
    /// section should collect a field's crumbs and join them with <c>"; "</c> once, as the HTTP/2 and
    /// HTTP/3 transports do.
    /// </remarks>
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
    /// RFC 9220): a <c>CONNECT</c> request that carries a non-empty
    /// <c>:protocol</c> pseudo-header. Shared by HTTP/2 and HTTP/3 so both
    /// recognize the extension identically.
    /// </summary>
    /// <remarks>
    /// An empty <c>:protocol</c> is not a protocol name (a token is
    /// <c>1*tchar</c>, RFC 9110 §5.6.2), so it never identifies an extended
    /// CONNECT; <see cref="ValidateExtendedConnect"/> rejects it as malformed.
    /// </remarks>
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
    /// rules shared by HTTP/2 and HTTP/3 (RFC 8441 §4, RFC 9220 §3): a present
    /// <c>:protocol</c> field must carry a protocol name, it is only valid on a
    /// <c>CONNECT</c> request, and an extended CONNECT MUST also include
    /// <c>:scheme</c>, <c>:path</c>, and <c>:authority</c>. Returns a
    /// description of the violation, or <see langword="null"/> when the request
    /// is well-formed (including the common case where <c>:protocol</c> is
    /// absent).
    /// </summary>
    /// <remarks>
    /// Only <see langword="null"/> means the field is absent. A present but
    /// empty <c>:protocol</c> is a violation on every method: a protocol name
    /// is a token, which is <c>1*tchar</c> (RFC 9110 §5.6.2), so an empty value
    /// names no protocol and the request is malformed.
    /// </remarks>
    /// <param name="method">The decoded <c>:method</c> value, or <see langword="null"/>.</param>
    /// <param name="scheme">The decoded <c>:scheme</c> value, or <see langword="null"/>.</param>
    /// <param name="path">The decoded <c>:path</c> value, or <see langword="null"/>.</param>
    /// <param name="authority">The decoded <c>:authority</c> value, or <see langword="null"/>.</param>
    /// <param name="protocol">
    /// The decoded <c>:protocol</c> value, or <see langword="null"/> when the field is absent.
    /// </param>
    /// <returns>
    /// A human-readable violation message, or <see langword="null"/> when valid.
    /// Callers raise their own protocol-appropriate error from the message.
    /// </returns>
    public static string? ValidateExtendedConnect(string? method, string? scheme, string? path, string? authority, string? protocol)
    {
        if (protocol is null)
        {
            // No :protocol field — not an extended CONNECT; nothing to validate.
            return null;
        }

        // RFC 9110 §5.6.2 — a protocol name is a token (1*tchar); an empty
        // :protocol names no protocol, so the request is malformed whatever its
        // method (RFC 8441 §4, RFC 9220 §3).
        if (protocol.Length == 0)
        {
            return "The ':protocol' pseudo-header MUST carry a protocol name; an empty value is not a token (RFC 9110 §5.6.2, RFC 8441 §4).";
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
