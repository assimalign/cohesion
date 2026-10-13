using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Http.Internal;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Provides options used when serializing an HTTP cookie.
/// </summary>
/// <remarks>
/// <see cref="Path"/> and <see cref="Domain"/> are checked against the
/// RFC 6265 &#167; 4.1.1 attribute grammar when they are set, so an options
/// instance never holds a value that would add attributes to, or corrupt, the
/// <c>Set-Cookie</c> line <see cref="HttpCookie.ToString"/> writes. A value
/// built from untrusted input, such as a tenant segment or a forwarded host, is
/// therefore refused instead of emitted.
/// </remarks>
public sealed class HttpCookieOptions
{
    private List<string>? _extensions;
    private string? _domain;
    private string? _path;

    /// <summary>
    /// Initializes a new set of cookie options.
    /// </summary>
    public HttpCookieOptions()
    {
        Path = "/";
    }

    /// <summary>
    /// Initializes a copy of the supplied cookie options.
    /// </summary>
    /// <param name="options">The options to copy.</param>
    public HttpCookieOptions(HttpCookieOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        // The source's setters already checked both values.
        _domain = options._domain;
        _path = options._path;
        Expires = options.Expires;
        Secure = options.Secure;
        SameSite = options.SameSite;
        HttpOnly = options.HttpOnly;
        MaxAge = options.MaxAge;
        IsEssential = options.IsEssential;

        if (options._extensions is not null)
        {
            _extensions = new List<string>(options._extensions);
        }
    }

    /// <summary>
    /// Gets or sets the <c>Domain</c> attribute: the host, and its subdomains,
    /// the cookie is sent to. <see langword="null"/> or empty omits the
    /// attribute, which makes the cookie host-only.
    /// </summary>
    /// <value>
    /// An RFC 6265 &#167; 4.1.1 <c>domain-value</c>: dot-separated labels of
    /// letters, digits, and hyphens, optionally preceded by one dot, which a
    /// user agent ignores (RFC 6265 &#167; 5.2.3).
    /// </value>
    /// <exception cref="ArgumentException">
    /// The value is not a <c>domain-value</c>: it holds <c>;</c>, whitespace, a
    /// control character, or another character outside the label grammar, has
    /// an empty or over-long label, or is longer than 253 characters after the
    /// optional leading dot.
    /// </exception>
    public string? Domain
    {
        get => _domain;
        set
        {
            // The value is not echoed: it may hold the control character that made it invalid.
            if (!string.IsNullOrEmpty(value) && !HttpCookieGrammar.IsValidDomain(value))
            {
                throw new ArgumentException(
                    ErrorMessages.CookieDomainInvalid,
                    nameof(Domain));
            }

            _domain = value;
        }
    }

    /// <summary>
    /// Gets or sets the <c>Path</c> attribute: the request-path prefix the
    /// cookie is sent with. Defaults to <c>/</c>. <see langword="null"/> or
    /// empty omits the attribute, so the user agent uses the default path
    /// (RFC 6265 &#167; 5.1.4).
    /// </summary>
    /// <value>
    /// An RFC 6265 &#167; 4.1.1 <c>path-value</c>: US-ASCII characters other
    /// than control characters and <c>;</c>.
    /// </value>
    /// <exception cref="ArgumentException">
    /// The value holds <c>;</c>, which would add an attribute of the value's own
    /// to the <c>Set-Cookie</c> line, a control character
    /// (<c>0x00</c>&#8211;<c>0x1F</c>, <c>0x7F</c>), or a character outside
    /// US-ASCII.
    /// </exception>
    public string? Path
    {
        get => _path;
        set
        {
            // The value is not echoed: it may hold the control character that made it invalid.
            if (value is not null && !HttpCookieGrammar.IsValidPath(value))
            {
                throw new ArgumentException(
                    ErrorMessages.CookiePathInvalid,
                    nameof(Path));
            }

            _path = value;
        }
    }

    /// <summary>
    /// Gets or sets the <c>Expires</c> attribute: the instant the cookie expires.
    /// </summary>
    public DateTimeOffset? Expires { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the <c>Secure</c> attribute is
    /// written, so the cookie is sent only over a secure channel.
    /// </summary>
    public bool Secure { get; set; }

    /// <summary>
    /// Gets or sets the <c>SameSite</c> attribute.
    /// <see cref="HttpCookieSameSiteMode.Unspecified"/> omits it.
    /// </summary>
    public HttpCookieSameSiteMode SameSite { get; set; } = HttpCookieSameSiteMode.Unspecified;

    /// <summary>
    /// Gets or sets a value indicating whether the <c>HttpOnly</c> attribute is
    /// written, so script cannot read the cookie.
    /// </summary>
    public bool HttpOnly { get; set; }

    /// <summary>
    /// Gets or sets the <c>Max-Age</c> attribute, written in whole seconds. Zero
    /// or a negative value deletes the cookie (RFC 6265 &#167; 5.2.2).
    /// </summary>
    public TimeSpan? MaxAge { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the cookie is essential to the
    /// application, so a consent policy issues it without consent. It is not
    /// written to the wire.
    /// </summary>
    public bool IsEssential { get; set; }

    /// <summary>
    /// Gets the extension attributes, each written verbatim after the known
    /// attributes as <c>; &lt;extension&gt;</c>.
    /// </summary>
    public IList<string> Extensions => _extensions ??= new List<string>();
}

/// <summary>
/// Defines the supported SameSite modes for cookies.
/// </summary>
public enum HttpCookieSameSiteMode
{
    Unspecified = 0,
    None = 1,
    Lax = 2,
    Strict = 3,
}
