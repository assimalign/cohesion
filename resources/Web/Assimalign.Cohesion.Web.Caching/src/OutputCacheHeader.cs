using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Web.Caching;

/// <summary>
/// A single captured response header — its name and one or more values — stored alongside a cached
/// response in an <see cref="OutputCacheEntry"/>.
/// </summary>
/// <remarks>
/// The output cache captures the response header block as a flat list of these carriers rather than a
/// live header collection so the stored entry is a plain, self-describing data object: no reflection,
/// no serializer, and trivially framable by a future distributed store adapter. Values are preserved
/// in order and replayed verbatim onto the response on a cache hit.
/// </remarks>
public sealed class OutputCacheHeader
{
    /// <summary>
    /// Initializes a new captured header.
    /// </summary>
    /// <param name="name">The header field name.</param>
    /// <param name="values">The header field values, in order. An empty array is permitted.</param>
    /// <exception cref="ArgumentException"><paramref name="name"/> is <see langword="null"/> or empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="values"/> is <see langword="null"/>.</exception>
    public OutputCacheHeader(string name, string?[] values)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(values);

        Name = name;
        Values = values;
    }

    /// <summary>
    /// Gets the header field name.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets the header field values, in order.
    /// </summary>
    public IReadOnlyList<string?> Values { get; }
}
