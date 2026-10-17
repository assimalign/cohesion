using System.Collections.Generic;

using Assimalign.Cohesion.Database.Sql.Catalog;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// A closed source value over existing metadata. One reference keeps stored-only binding
/// allocation-neutral; a virtual relation never acquires a fabricated storage identity.
/// </summary>
internal readonly struct SqlRelationSource
{
    private readonly object _metadata;

    internal SqlRelationSource(SqlCatalogTable table) { _metadata = table; }
    internal SqlRelationSource(SqlSystemViewDefinition view) { _metadata = view; }

    internal SqlCatalogTable? Table => _metadata as SqlCatalogTable;
    internal SqlSystemViewDefinition? View => _metadata as SqlSystemViewDefinition;
    internal string Schema => Table?.Schema ?? View!.Schema;
    internal string Name => Table?.Name ?? View!.Name;
    internal IReadOnlyList<SqlCatalogColumn> Columns => Table?.Columns ?? View!.Columns;
}
