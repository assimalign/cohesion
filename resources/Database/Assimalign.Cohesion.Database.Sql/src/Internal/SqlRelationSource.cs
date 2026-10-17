using System.Collections.Generic;

using Assimalign.Cohesion.Database.Sql.Catalog;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>The closed sources a relation binding can read, without inventing storage identities.</summary>
internal abstract record SqlRelationSource
{
    private protected SqlRelationSource() { }

    internal abstract string Schema { get; }
    internal abstract string Name { get; }
    internal abstract IReadOnlyList<SqlCatalogColumn> Columns { get; }
}

internal sealed record SqlStoredTableSource(SqlCatalogTable Table) : SqlRelationSource
{
    internal override string Schema => Table.Schema;
    internal override string Name => Table.Name;
    internal override IReadOnlyList<SqlCatalogColumn> Columns => Table.Columns;
}

internal sealed record SqlSystemRelationSource(SqlSystemViewDefinition View) : SqlRelationSource
{
    internal override string Schema => View.Schema;
    internal override string Name => View.Name;
    internal override IReadOnlyList<SqlCatalogColumn> Columns => View.Columns;
}
