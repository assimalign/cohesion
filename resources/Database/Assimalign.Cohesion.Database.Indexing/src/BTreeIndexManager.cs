using System;

namespace Assimalign.Cohesion.Database.Indexing;

/// <summary>
/// Creates <see cref="IIndexManager"/> instances backed by B+Trees on shared
/// storage pages.
/// </summary>
public static class BTreeIndexManager
{
    /// <summary>
    /// Creates an index manager over the specified storage.
    /// </summary>
    /// <param name="options">The composition options.</param>
    /// <returns>The index manager (it also implements <see cref="IIndexRegistry"/> for catalog persistence).</returns>
    public static IIndexManager Create(BTreeIndexManagerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return new DefaultIndexManager(options);
    }
}
