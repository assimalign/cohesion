using System.Collections.Generic;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Represents a case-insensitive collection of HTTP headers.
/// </summary>
public interface IHttpHeaderCollection : IEnumerable<KeyValuePair<HttpHeaderKey, HttpHeaderValue>>
{
    /// <summary>
    /// Gets the number of headers in the collection.
    /// </summary>
    int Count { get; }

    /// <summary>
    /// Gets a value indicating whether the collection is read-only.
    /// </summary>
    bool IsReadOnly { get; }

    /// <summary>
    /// Gets or sets a header value.
    /// </summary>
    /// <param name="key">The header key.</param>
    /// <returns>The resolved header value when present; otherwise <see cref="HttpHeaderValue.Empty"/>.</returns>
    HttpHeaderValue this[HttpHeaderKey key] { get; set; }

    /// <summary>
    /// Determines whether a header exists.
    /// </summary>
    /// <param name="key">The header key.</param>
    /// <returns><see langword="true"/> when the key exists; otherwise <see langword="false"/>.</returns>
    bool ContainsKey(HttpHeaderKey key);

    /// <summary>
    /// Attempts to retrieve a header value.
    /// </summary>
    /// <param name="key">The header key.</param>
    /// <param name="value">The resolved header value.</param>
    /// <returns><see langword="true"/> when the key exists; otherwise <see langword="false"/>.</returns>
    bool TryGetValue(HttpHeaderKey key, out HttpHeaderValue value);

    /// <summary>
    /// Adds a header to the collection.
    /// </summary>
    /// <param name="key">The header key.</param>
    /// <param name="value">The header value.</param>
    void Add(HttpHeaderKey key, HttpHeaderValue value);

    /// <summary>
    /// Removes a header from the collection.
    /// </summary>
    /// <param name="key">The header key.</param>
    void Remove(HttpHeaderKey key);

    /// <summary>
    /// Removes all headers from the collection.
    /// </summary>
    void Clear();
}
