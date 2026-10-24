using System.Collections.Generic;

using Assimalign.Cohesion.Database.Sql.Catalog;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// A closed source value over existing metadata. One reference keeps stored-only binding
/// allocation-neutral; a virtual relation never acquires a fabricated storage identity.
/// </summary>
[global::System.Diagnostics.DebuggerDisplay("SqlRelationSource")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Sql.Internal.SqlRelationSource.DebugView))]
internal readonly struct SqlRelationSource
{
    private readonly object _metadata;

    internal SqlRelationSource(SqlCatalogTable table) { _metadata = table; }
    internal SqlRelationSource(SqlSystemViewDefinition view) { _metadata = view; }
    internal SqlRelationSource(SqlDerivedRelation derived) { _metadata = derived; }

    internal SqlCatalogTable? Table => _metadata as SqlCatalogTable;
    internal SqlSystemViewDefinition? View => _metadata as SqlSystemViewDefinition;
    internal SqlDerivedRelation? Derived => _metadata as SqlDerivedRelation;
    internal string Schema => Table?.Schema ?? View?.Schema ?? Derived?.CatalogView?.Schema ?? string.Empty;
    internal string Name => Table?.Name ?? View?.Name ?? Derived!.Name;
    internal IReadOnlyList<SqlCatalogColumn> Columns => Table?.Columns ?? View?.Columns ?? Derived!.Columns;

    private sealed class DebugView
    {
        public DebugView(global::Assimalign.Cohesion.Database.Sql.Internal.SqlRelationSource value)
        {
            _ = value;
        }
    }
}
