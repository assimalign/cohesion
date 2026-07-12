using System;

using Assimalign.Cohesion.Database.Sql.Storage;

namespace Assimalign.Cohesion.Database.Sql.Catalog;

/// <summary>
/// Opens <see cref="ISqlCatalog"/> instances over a dedicated catalog storage
/// file set.
/// </summary>
/// <remarks>
/// The catalog owns its own storage instance — separate from the database's data
/// file set — so metadata records and user rows never share a scan space, while
/// still getting the same page/WAL durability. The engine composes one catalog
/// storage per database (by convention the database name suffixed with
/// <c>.catalog</c>).
/// </remarks>
public static class SqlCatalog
{
    /// <summary>
    /// Opens the catalog persisted in the given storage (an empty storage yields an
    /// empty catalog).
    /// </summary>
    /// <param name="storage">The dedicated catalog storage file set.</param>
    /// <returns>The catalog.</returns>
    public static ISqlCatalog Open(SqlStorage storage)
    {
        ArgumentNullException.ThrowIfNull(storage);
        return DefaultSqlCatalog.Open(storage);
    }
}
