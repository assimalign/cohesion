using System;
using System.Collections.Generic;
using System.Text;

using Assimalign.Cohesion.Http.Internal;

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
    /// Repeated names retain every value in arrival order. A scalar read returns the first value.
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

            collection.Add(key, rawValue);
        }

        return collection;
    }

    /// <summary>
    /// Creates the raw query text of a parsed query collection, the inverse of <see cref="Parse"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every name and value is percent-encoded as UTF-8 except for the unreserved characters (RFC 3986 §2.3),
    /// so an <c>&amp;</c>, <c>=</c> or <c>+</c> inside one cannot split or change its entry. Entries are joined
    /// with <c>&amp;</c> in key enumeration order, with each key's values in arrival order. An empty value is written as its
    /// name alone, without <c>=</c>; <see cref="Parse"/> reads both forms as the same empty value.
    /// A manually added key with zero occurrences is likewise written as one empty occurrence.
    /// </para>
    /// <para>
    /// The result is the query a redirect carries, not the bytes the request carried: a transport keeps only
    /// the parsed collection, so the original order and percent-encoding are not available to reproduce.
    /// </para>
    /// </remarks>
    /// <param name="query">The parsed query.</param>
    /// <returns>The query text without a leading <c>?</c>; empty when the collection has no entries.</returns>
    public static HttpQuery Create(IHttpQueryCollection query)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (query.Count == 0)
        {
            return new HttpQuery(string.Empty);
        }

        StringBuilder builder = new(32);

        foreach (KeyValuePair<HttpQueryKey, HttpQueryValue> entry in query)
        {
            // A manually supplied zero-occurrence value still names an empty parameter.
            int count = Math.Max(1, entry.Value.Count);
            for (int index = 0; index < count; index++)
            {
                if (builder.Length != 0)
                {
                    builder.Append('&');
                }

                UrlEncoder.AppendComponent(builder, entry.Key.Value);

                string value = entry.Value.Count == 0 ? string.Empty : entry.Value[index];
                if (!string.IsNullOrEmpty(value))
                {
                    builder.Append('=');
                    UrlEncoder.AppendComponent(builder, value);
                }
            }
        }

        return new HttpQuery(builder.ToString());
    }

    public bool Equals(HttpQuery other) => string.Equals(Value, other.Value, StringComparison.Ordinal);
    public override bool Equals(object? obj) => obj is HttpQuery query && Equals(query);
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);
    public override string ToString() => Value;
}
