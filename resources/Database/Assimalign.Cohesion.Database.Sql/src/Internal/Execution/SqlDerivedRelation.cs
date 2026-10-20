using System.Collections.Generic;

using Assimalign.Cohesion.Database.Sql.Catalog;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>A child query's output schema and plan, without a storage identity.</summary>
internal sealed record SqlDerivedRelation(string Name, SqlPlan Query, IReadOnlyList<SqlCatalogColumn> Columns, bool IsCorrelated = false)
{
    internal bool IsCommonTable { get; init; }
    internal SqlCatalogView? CatalogView { get; init; }
}
