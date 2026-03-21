using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Represents a parsed collection of query-string values.
/// </summary>
public interface IHttpQueryCollection : IEnumerable<KeyValuePair<HttpQueryKey, HttpQueryValue>>
{
    /// <summary>
    /// Gets the number of query entries.
    /// </summary>
    int Count { get; }

    /// <summary>
    /// Gets the query value associated with the supplied key.
    /// </summary>
    /// <param name="key">The query key to resolve.</param>
    /// <returns>The parsed query value if present; otherwise <see cref="HttpQueryValue.Empty"/>.</returns>
    HttpQueryValue this[HttpQueryKey key] { get; }

    /// <summary>
    /// Determines whether the collection contains the supplied query key.
    /// </summary>
    /// <param name="key">The query key to locate.</param>
    /// <returns><see langword="true"/> when the key exists; otherwise <see langword="false"/>.</returns>
    bool ContainsKey(HttpQueryKey key);

    /// <summary>
    /// Attempts to retrieve a parsed query value.
    /// </summary>
    /// <param name="key">The query key to resolve.</param>
    /// <param name="value">The resolved query value when found.</param>
    /// <returns><see langword="true"/> when the key was found; otherwise <see langword="false"/>.</returns>
    bool TryGetValue(HttpQueryKey key, out HttpQueryValue value);
}
