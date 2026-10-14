using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Http.Internal;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Provides options used when serializing an HTTP cookie.
/// </summary>
/// <remarks>
/// <see cref="Path"/> and <see cref="Domain"/> are <see cref="HttpCookiePath"/>
/// and <see cref="HttpCookieDomain"/> values, checked against the RFC 6265
/// &#167; 4.1.1 attribute grammar when they are constructed &#8212; for a
/// <see cref="string"/> assignment, by its implicit conversion before the setter
/// runs &#8212; so an options instance never holds a value that would add
/// attributes to, or corrupt, the <c>Set-Cookie</c> line
/// <see cref="HttpCookie.ToString"/> writes. A value built from untrusted input,
/// such as a tenant segment or a forwarded host, is therefore refused instead of
/// emitted.
/// </remarks>
public sealed class HttpCookieOptions
{
    private HttpCookieExtensionCollection? _extensions;

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

        // Both are value types their own construction already checked.
        Domain = options.Domain;
        Path = options.Path;
        Expires = options.Expires;
        Secure = options.Secure;
        SameSite = options.SameSite;
        HttpOnly = options.HttpOnly;
        MaxAge = options.MaxAge;
        IsEssential = options.IsEssential;

        if (options._extensions is not null)
        {
            _extensions = new HttpCookieExtensionCollection();
            foreach (HttpCookieExtension extension in options._extensions)
            {
                _extensions.Add(extension);
            }
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
    /// <remarks>
    /// Assigning a <see cref="string"/> converts it first: <see langword="null"/>
    /// and empty become <see langword="null"/>, and anything else that is not a
    /// <c>domain-value</c> &#8212; <c>;</c>, whitespace, a control character, or
    /// another character outside the label grammar, an empty or over-long label,
    /// or more than 253 characters after the optional leading dot &#8212; throws
    /// <see cref="ArgumentException"/> before the setter runs, leaving the
    /// current value in place.
    /// </remarks>
    public HttpCookieDomain? Domain { get; set; }

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
    /// <remarks>
    /// Assigning a <see cref="string"/> converts it first: <see langword="null"/>
    /// stays <see langword="null"/>, and a value holding <c>;</c> (which would
    /// add an attribute of the value's own to the <c>Set-Cookie</c> line), a
    /// control character (<c>0x00</c>&#8211;<c>0x1F</c>, <c>0x7F</c>), or a
    /// character outside US-ASCII throws <see cref="ArgumentException"/> before
    /// the setter runs, leaving the current value in place.
    /// </remarks>
    public HttpCookiePath? Path { get; set; }

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
    /// Gets the extension attributes, each written after the known attributes
    /// as <c>; &lt;extension&gt;</c>.
    /// </summary>
    /// <remarks>
    /// Each item is a non-empty RFC 6265 <c>extension-av</c>: characters in
    /// <c>0x20</c>&#8211;<c>0x7E</c> except <c>;</c>. Adding or assigning an
    /// invalid string throws <see cref="ArgumentException"/> through its
    /// implicit conversion, leaving the collection unchanged. The collection
    /// also refuses <see langword="default"/>, which holds no extension.
    /// </remarks>
    public IList<HttpCookieExtension> Extensions => _extensions ??= new HttpCookieExtensionCollection();
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
