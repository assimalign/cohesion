using System.Collections.Generic;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Represents parsed form values and uploaded files.
/// </summary>
public interface IHttpFormCollection : IEnumerable<KeyValuePair<string, HttpQueryValue>>
{
    /// <summary>
    /// Gets the number of distinct scalar form keys.
    /// </summary>
    int Count { get; }

    /// <summary>
    /// Gets all form values for a key in arrival order; their scalar value is the first occurrence.
    /// </summary>
    /// <param name="key">The key to retrieve.</param>
    /// <returns>The parsed form value if present; otherwise <see cref="HttpQueryValue.Empty"/>.</returns>
    HttpQueryValue this[string key] { get; }

    /// <summary>
    /// Gets the uploaded files associated with the form.
    /// </summary>
    IHttpFormFileCollection Files { get; }

    /// <summary>
    /// Determines whether a form key exists.
    /// </summary>
    /// <param name="key">The key to test.</param>
    /// <returns><see langword="true"/> when the key exists; otherwise <see langword="false"/>.</returns>
    bool ContainsKey(string key);

    /// <summary>
    /// Attempts to retrieve all form values for a key; their scalar value is the first occurrence.
    /// </summary>
    /// <param name="key">The key to retrieve.</param>
    /// <param name="value">The resolved value when found.</param>
    /// <returns><see langword="true"/> when the value was found; otherwise <see langword="false"/>.</returns>
    bool TryGetValue(string key, out HttpQueryValue value);
}