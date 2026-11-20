using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Represents a parsed collection of query-string values.
/// </summary>
public interface IHttpQueryCollection : IEnumerable<KeyValuePair<HttpQueryKey, HttpQueryValue>>
{
    /// <summary>
    /// Gets the number of distinct query keys.
    /// </summary>
    int Count { get; }

    /// <summary>
    /// Gets all values for the key in arrival order; their scalar value is the first occurrence.
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
    /// Attempts to retrieve all values for a key; their scalar value is the first occurrence.
    /// </summary>
    /// <param name="key">The query key to resolve.</param>
    /// <param name="value">The resolved query value when found.</param>
    /// <returns><see langword="true"/> when the key was found; otherwise <see langword="false"/>.</returns>
    bool TryGetValue(HttpQueryKey key, out HttpQueryValue value);

    /// <summary>
    /// Gets the query text this collection was parsed from, while the collection still holds exactly the
    /// parameters that text describes.
    /// </summary>
    /// <value>
    /// The received query text, or <see langword="null"/> when the collection was not parsed from text, has
    /// changed since, or does not track its source. The default implementation returns
    /// <see langword="null"/>.
    /// </value>
    /// <remarks>
    /// <para>
    /// A parsed collection does not round-trip through <see cref="HttpQuery.Create"/>, which merges keys
    /// that differ only in case, regroups repeated keys and re-encodes every name and value. Code that must
    /// pass the query on as the client sent it, such as a proxy or a signed-URL check, reads this property
    /// first and falls back to <see cref="HttpQuery.Create"/> when it is <see langword="null"/>.
    /// </para>
    /// <para>
    /// An implementation returns <see langword="null"/> as soon as the parameters it exposes differ from the
    /// text. A wrapper that adds, removes or changes a parameter must not return its inner collection's
    /// value: a proxy would forward the original text, including a parameter the wrapper removed. Leaving
    /// the default in place is always safe; the query is then rebuilt from the parameters.
    /// <see cref="HttpQueryCollection"/> clears its value on its first change.
    /// </para>
    /// </remarks>
    HttpQuery? Source => null;
}
