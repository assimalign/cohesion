using System;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Provides helpers for working with HTTP header collections.
/// </summary>
public static class HttpHeaderCollectionExtensions
{
    /// <summary>
    /// Gets a header value as a string.
    /// </summary>
    /// <param name="headers">The header collection.</param>
    /// <param name="key">The header key.</param>
    /// <returns>The resolved header value, or <see langword="null"/> when the header is not present.</returns>
    public static string? GetValue(this IHttpHeaderCollection headers, HttpHeaderKey key)
    {
        ArgumentNullException.ThrowIfNull(headers);

        return headers.TryGetValue(key, out HttpHeaderValue value)
            ? value.Value
            : null;
    }

    /// <summary>
    /// Sets a header value.
    /// </summary>
    /// <param name="headers">The header collection.</param>
    /// <param name="key">The header key.</param>
    /// <param name="value">The header value.</param>
    public static void SetValue(this IHttpHeaderCollection headers, HttpHeaderKey key, string? value)
    {
        ArgumentNullException.ThrowIfNull(headers);

        if (string.IsNullOrEmpty(value))
        {
            headers.Remove(key);
            return;
        }

        headers[key] = value;
    }

    /// <summary>
    /// Appends a string value to a header.
    /// </summary>
    /// <param name="headers">The header collection.</param>
    /// <param name="key">The header key.</param>
    /// <param name="value">The header value to append.</param>
    public static void AppendValue(this IHttpHeaderCollection headers, HttpHeaderKey key, string value)
    {
        ArgumentNullException.ThrowIfNull(headers);
        ArgumentNullException.ThrowIfNull(value);

        headers[key] = headers.TryGetValue(key, out HttpHeaderValue existing)
            ? HttpHeaderValue.Concat(existing, value)
            : new HttpHeaderValue(value);
    }
}
