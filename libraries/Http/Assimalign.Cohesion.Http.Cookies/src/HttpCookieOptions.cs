using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Provides options used when serializing an HTTP cookie.
/// </summary>
public sealed class HttpCookieOptions
{
    private List<string>? _extensions;

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
            _extensions = new List<string>(options._extensions);
        }
    }

    public string? Domain { get; set; }
    public string? Path { get; set; }
    public DateTimeOffset? Expires { get; set; }
    public bool Secure { get; set; }
    public HttpCookieSameSiteMode SameSite { get; set; } = HttpCookieSameSiteMode.Unspecified;
    public bool HttpOnly { get; set; }
    public TimeSpan? MaxAge { get; set; }
    public bool IsEssential { get; set; }
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
