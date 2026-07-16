using System;

namespace Assimalign.Cohesion.Database.KeyValuePair.Catalog;

using Assimalign.Cohesion.Database.KeyValuePair.Storage;

/// <summary>
/// Opens key-value catalogs over a dedicated catalog storage file set.
/// </summary>
public static class KeyValueCatalog
{
    /// <summary>
    /// Opens the catalog persisted on the given storage, loading any existing
    /// metadata records.
    /// </summary>
    /// <param name="storage">The dedicated catalog storage file set.</param>
    /// <returns>The catalog.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="storage"/> is null.</exception>
    /// <exception cref="KeyValueCatalogException">Thrown when a persisted record is malformed.</exception>
    public static IKeyValueCatalog Open(KeyValueStorage storage)
    {
        ArgumentNullException.ThrowIfNull(storage);
        return DefaultKeyValueCatalog.Open(storage);
    }
}
