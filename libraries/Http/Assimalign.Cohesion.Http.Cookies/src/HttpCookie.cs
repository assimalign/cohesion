using System;
using System.Diagnostics;
using System.Text;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Represents an HTTP cookie value.
/// </summary>
/// <remarks>
/// The <see cref="Name"/> and <see cref="Value"/> are validated against the
/// RFC 6265 &#167; 4.1.1 grammar at construction: the name must be a
/// <c>token</c> and the value must be <c>*cookie-octet</c> (optionally
/// DQUOTE-wrapped). Because the two are immutable, a constructed
/// <see cref="HttpCookie"/> can never carry an octet &#8212; <c>;</c>,
/// <c>,</c>, whitespace, a control character, or CR/LF &#8212; that would
/// split or corrupt the serialized <c>Set-Cookie</c> line.
/// </remarks>
[DebuggerDisplay("{ToString()}")]
public sealed class HttpCookie
{
    /// <summary>
    /// Initializes a new cookie instance.
    /// </summary>
    /// <param name="name">The cookie name. Must be a non-empty RFC 6265 &#167; 4.1.1 <c>token</c>.</param>
    /// <param name="value">The cookie value. Must be RFC 6265 &#167; 4.1.1 <c>*cookie-octet</c>, optionally wrapped in a single pair of DQUOTEs.</param>
    /// <param name="options">The cookie options.</param>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is <see langword="null"/> or empty, or <paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="name"/> is not a valid RFC 6265 token, or
    /// <paramref name="value"/> contains an octet outside the RFC 6265
    /// cookie-octet grammar (a control character including CR/LF, whitespace,
    /// DQUOTE, comma, semicolon, or backslash). Such input is rejected so the
    /// value cannot split or corrupt the emitted <c>Set-Cookie</c> header.
    /// </exception>
    public HttpCookie(string name, string value = "", HttpCookieOptions? options = null)
    {
        ArgumentNullException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(value);

        // RFC 6265 §4.1.1 octet-grammar validation (anti header-splitting). The
        // raw name/value is deliberately not echoed into the message so a
        // hostile control character cannot leak into logs downstream.
        if (!HttpCookieGrammar.IsValidName(name))
        {
            throw new ArgumentException(
                "Cookie name is not a valid RFC 6265 token: it must be non-empty and contain only token characters (no control characters, whitespace, or separators such as '=', ';', or ',').",
                nameof(name));
        }

        if (!HttpCookieGrammar.IsValidValue(value))
        {
            throw new ArgumentException(
                "Cookie value contains characters outside the RFC 6265 cookie-octet grammar. Control characters (including CR/LF), whitespace, DQUOTE, comma, semicolon, and backslash are forbidden so the value cannot split or corrupt the Set-Cookie header line; percent- or base64-encode such values first.",
                nameof(value));
        }

        Name = name;
        Value = value;
        Options = options is null ? new HttpCookieOptions() : new HttpCookieOptions(options);
    }

    /// <summary>
    /// Gets the cookie name.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets the cookie value.
    /// </summary>
    public string Value { get; }

    /// <summary>
    /// Gets the cookie options.
    /// </summary>
    public HttpCookieOptions Options { get; }

    /// <summary>
    /// Gets a value indicating whether the cookie has a non-empty value.
    /// </summary>
    public bool HasValue => !string.IsNullOrEmpty(Value);

    /// <summary>
    /// Returns a cookie whose lifetime is capped at the RFC 6265bis 400-day
    /// maximum (<see cref="HttpCookieLimits.DefaultMaxLifetime"/>), measured
    /// relative to <paramref name="referenceTime"/>.
    /// </summary>
    /// <param name="referenceTime">The instant a future <see cref="HttpCookieOptions.Expires"/> is measured against &#8212; typically the moment the cookie is emitted.</param>
    /// <returns>A clamped cookie, or the same instance when no clamping is required.</returns>
    /// <remarks>See <see cref="ClampLifetime(DateTimeOffset, TimeSpan)"/> for the full clamping rules.</remarks>
    public HttpCookie ClampLifetime(DateTimeOffset referenceTime)
        => ClampLifetime(referenceTime, HttpCookieLimits.DefaultMaxLifetime);

    /// <summary>
    /// Returns a cookie whose <c>Max-Age</c> and <c>Expires</c> are capped at
    /// <paramref name="maxLifetime"/> (RFC 6265bis &#167; 5.5 lifetime limit).
    /// </summary>
    /// <param name="referenceTime">The instant a future <see cref="HttpCookieOptions.Expires"/> is measured against &#8212; typically the moment the cookie is emitted.</param>
    /// <param name="maxLifetime">The maximum permitted lifetime. Must be non-negative.</param>
    /// <returns>
    /// A cookie with <c>Max-Age</c> reduced to <paramref name="maxLifetime"/>
    /// when it was longer, and <c>Expires</c> pulled back to
    /// <paramref name="referenceTime"/> + <paramref name="maxLifetime"/> when
    /// it was further out. A zero or negative <c>Max-Age</c> (a deletion
    /// signal per RFC 6265 &#167; 5.2.2) and a past or near-future
    /// <c>Expires</c> are preserved exactly. Returns the same instance when no
    /// clamping is required.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxLifetime"/> is negative.</exception>
    public HttpCookie ClampLifetime(DateTimeOffset referenceTime, TimeSpan maxLifetime)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxLifetime.Ticks, nameof(maxLifetime));

        TimeSpan? maxAge = Options.MaxAge;
        DateTimeOffset? expires = Options.Expires;
        bool changed = false;

        // Max-Age: only a positive duration longer than the cap is clamped.
        // Zero and negative Max-Age are deletion signals (RFC 6265 §5.2.2) and
        // are left untouched so deletion semantics still round-trip.
        if (maxAge is TimeSpan age && age > maxLifetime)
        {
            maxAge = maxLifetime;
            changed = true;
        }

        // Expires: an absolute expiry more than maxLifetime past referenceTime
        // is pulled back to the cap. A past or near-future expiry is left as-is.
        if (expires is DateTimeOffset when)
        {
            // Saturate at DateTimeOffset.MaxValue rather than overflowing when
            // referenceTime + maxLifetime would run off the end of the range.
            DateTimeOffset cap = maxLifetime < DateTimeOffset.MaxValue - referenceTime
                ? referenceTime + maxLifetime
                : DateTimeOffset.MaxValue;

            if (when > cap)
            {
                expires = cap;
                changed = true;
            }
        }

        if (!changed)
        {
            return this;
        }

        HttpCookieOptions clamped = new(Options)
        {
            MaxAge = maxAge,
            Expires = expires,
        };

        return new HttpCookie(Name, Value, clamped);
    }

    /// <inheritdoc />
    public override string ToString()
    {
        StringBuilder builder = new();
        builder.Append(Name);
        builder.Append('=');
        builder.Append(Value);

        if (!string.IsNullOrWhiteSpace(Options.Domain))
        {
            builder.Append("; Domain=").Append(Options.Domain);
        }

        if (!string.IsNullOrWhiteSpace(Options.Path))
        {
            builder.Append("; Path=").Append(Options.Path);
        }

        if (Options.Expires is DateTimeOffset expires)
        {
            builder.Append("; Expires=").Append(expires.ToUniversalTime().ToString("R"));
        }

        if (Options.MaxAge is TimeSpan maxAge)
        {
            builder.Append("; Max-Age=").Append((long)maxAge.TotalSeconds);
        }

        if (Options.Secure)
        {
            builder.Append("; Secure");
        }

        if (Options.HttpOnly)
        {
            builder.Append("; HttpOnly");
        }

        if (Options.SameSite != HttpCookieSameSiteMode.Unspecified)
        {
            builder.Append("; SameSite=").Append(Options.SameSite);
        }

        foreach (string extension in Options.Extensions)
        {
            builder.Append("; ").Append(extension);
        }

        return builder.ToString();
    }
}
