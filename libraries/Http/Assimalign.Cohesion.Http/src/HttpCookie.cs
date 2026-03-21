using System;
using System.Diagnostics;
using System.Text;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Represents an HTTP cookie value.
/// </summary>
[DebuggerDisplay("{ToString()}")]
public sealed class HttpCookie
{
    /// <summary>
    /// Initializes a new cookie instance.
    /// </summary>
    /// <param name="name">The cookie name.</param>
    /// <param name="value">The cookie value.</param>
    /// <param name="options">The cookie options.</param>
    public HttpCookie(string name, string value = "", HttpCookieOptions? options = null)
    {
        ArgumentNullException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(value);

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
