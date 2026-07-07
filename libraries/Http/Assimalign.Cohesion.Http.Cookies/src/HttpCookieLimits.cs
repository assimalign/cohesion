using System;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Configurable RFC 6265bis defensive limits applied when
/// <see cref="HttpCookieCollection"/> parses a <c>Cookie</c> or
/// <c>Set-Cookie</c> header, plus the lifetime cap consumed by
/// <see cref="HttpCookie.ClampLifetime(DateTimeOffset, TimeSpan)"/>.
/// </summary>
/// <remarks>
/// <para>
/// The size limits are wire-safety, not policy: a cookie whose name plus
/// value exceeds <see cref="MaxNameValueLength"/> is ignored wholesale, an
/// attribute whose value exceeds <see cref="MaxAttributeValueLength"/> is
/// dropped, and attributes beyond <see cref="MaxAttributeCount"/> are not
/// retained. Oversized input is silently ignored rather than rejected with an
/// exception, matching RFC 6265bis parsing-robustness guidance
/// (draft-ietf-httpbis-rfc6265bis).
/// </para>
/// <para>
/// Set any dimension to <see cref="int.MaxValue"/> to make it effectively
/// unbounded. The limits bound each cookie individually; the outer bound on
/// total header length is owned by the transport's request/response header
/// size limits.
/// </para>
/// </remarks>
public sealed class HttpCookieLimits
{
    /// <summary>
    /// The RFC 6265bis default maximum number of octets in a single cookie's
    /// name plus value (4096).
    /// </summary>
    public const int DefaultMaxNameValueLength = 4096;

    /// <summary>
    /// The RFC 6265bis default maximum number of octets in a single cookie
    /// attribute value (1024).
    /// </summary>
    public const int DefaultMaxAttributeValueLength = 1024;

    /// <summary>
    /// The default upper bound on the number of attributes retained while
    /// parsing one <c>Set-Cookie</c> value (50). This is a denial-of-service
    /// backstop &#8212; not an RFC-mandated value &#8212; that keeps a hostile
    /// cookie with thousands of <c>;</c>-separated segments from growing the
    /// parsed attribute/extension list without bound.
    /// </summary>
    public const int DefaultMaxAttributeCount = 50;

    /// <summary>
    /// The RFC 6265bis default maximum cookie lifetime (400 days). Cookies
    /// whose <c>Max-Age</c> or <c>Expires</c> would exceed this are clamped by
    /// <see cref="HttpCookie.ClampLifetime(DateTimeOffset, TimeSpan)"/>.
    /// </summary>
    public static readonly TimeSpan DefaultMaxLifetime = TimeSpan.FromDays(400);

    /// <summary>
    /// Gets a shared instance carrying the RFC 6265bis default limits. Used by
    /// the <see cref="HttpCookieCollection"/> constructors that do not take an
    /// explicit <see cref="HttpCookieLimits"/>.
    /// </summary>
    public static HttpCookieLimits Default { get; } = new();

    /// <summary>
    /// Gets the maximum number of octets permitted in a parsed cookie's name
    /// plus value. Cookies exceeding this are ignored. Defaults to
    /// <see cref="DefaultMaxNameValueLength"/>.
    /// </summary>
    public int MaxNameValueLength { get; init; } = DefaultMaxNameValueLength;

    /// <summary>
    /// Gets the maximum number of octets permitted in a single parsed cookie
    /// attribute value. Attributes exceeding this are dropped while the rest of
    /// the cookie is retained. Defaults to
    /// <see cref="DefaultMaxAttributeValueLength"/>.
    /// </summary>
    public int MaxAttributeValueLength { get; init; } = DefaultMaxAttributeValueLength;

    /// <summary>
    /// Gets the maximum number of attributes retained while parsing a single
    /// <c>Set-Cookie</c> value. Attributes beyond this count are ignored.
    /// Defaults to <see cref="DefaultMaxAttributeCount"/>.
    /// </summary>
    public int MaxAttributeCount { get; init; } = DefaultMaxAttributeCount;

    /// <summary>
    /// Gets the maximum cookie lifetime enforced by
    /// <see cref="HttpCookie.ClampLifetime(DateTimeOffset)"/>. Defaults to
    /// <see cref="DefaultMaxLifetime"/> (400 days).
    /// </summary>
    public TimeSpan MaxLifetime { get; init; } = DefaultMaxLifetime;
}
