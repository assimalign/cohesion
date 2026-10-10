using System;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Represents a raw HTTP query string and provides parsing helpers.
/// </summary>
public readonly struct HttpQuery : IEquatable<HttpQuery>
{
    /// <summary>
    /// Initializes a new query-string value.
    /// </summary>
    /// <param name="value">The raw query-string value.</param>
    public HttpQuery(string? value)
    {
        Value = string.IsNullOrEmpty(value)
            ? string.Empty
            : value[0] == '?' ? value[1..] : value;
    }

    /// <summary>
    /// Gets the raw query-string value without the leading question mark.
    /// </summary>
    public string Value { get; }

    /// <summary>
    /// Parses the query-string into a collection.
    /// </summary>
    /// <remarks>
    /// A parameter with an empty name — <c>=1</c>, or a bare <c>=</c> — is skipped: a
    /// <see cref="HttpQueryKey"/> is never empty, and RFC 3986 §3.4 gives the query no syntax that
    /// would require one. The parameter stays in the raw <see cref="Value"/>. Skipping it, rather than
    /// failing, keeps a request any client can send from failing as it is read.
    /// </remarks>
    /// <returns>A parsed <see cref="HttpQueryCollection"/>.</returns>
    public HttpQueryCollection Parse()
    {
        HttpQueryCollection collection = new();

        if (string.IsNullOrEmpty(Value))
        {
            return collection;
        }

        foreach (string segment in Value.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = segment.Split('=', 2);

            if (parts[0].Length == 0)
            {
                continue;
            }

            string key = Uri.UnescapeDataString(parts[0]);
            string rawValue = parts.Length == 2 ? Uri.UnescapeDataString(parts[1]) : string.Empty;

            collection[key] = rawValue;
        }

        return collection;
    }

    public bool Equals(HttpQuery other) => string.Equals(Value, other.Value, StringComparison.Ordinal);
    public override bool Equals(object? obj) => obj is HttpQuery query && Equals(query);
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);
    public override string ToString() => Value;
}
