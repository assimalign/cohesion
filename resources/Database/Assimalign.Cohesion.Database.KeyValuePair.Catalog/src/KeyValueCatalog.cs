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

    /// <summary>
    /// Captures the entry-space format and index registrations atomically.
    /// </summary>
    /// <param name="catalog">The catalog to capture, as returned by <see cref="Open"/>.</param>
    /// <returns>A read-only capture unaffected by later catalog writes.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="catalog"/> is null.</exception>
    /// <exception cref="InvalidCastException">Thrown when <paramref name="catalog"/> was not produced by this class.</exception>
    /// <remarks>
    /// Declared here rather than on <see cref="IKeyValueCatalog"/> deliberately, and for the same
    /// reason as its SQL counterpart <c>SqlCatalog.CaptureSnapshot</c>: the capture is an engine
    /// convenience over the catalog's own state, so it stays off the contract every catalog
    /// implementation would otherwise have to honour.
    /// </remarks>
    public static IKeyValueCatalogSnapshot CaptureSnapshot(IKeyValueCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return ((DefaultKeyValueCatalog)catalog).CaptureSnapshot();
    }
}
