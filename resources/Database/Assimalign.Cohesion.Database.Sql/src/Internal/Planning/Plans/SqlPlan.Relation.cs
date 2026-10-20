using System.Collections.Generic;

using Assimalign.Cohesion.Database.Sql.Catalog;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>The typed output contract of every executable relation.</summary>
internal abstract record SqlRelationPlan : SqlPlan
{
    internal abstract IReadOnlyList<SqlProjection> OutputProjections { get; }
    internal abstract IReadOnlyList<SqlCatalogColumn> OutputColumns { get; }
    internal virtual IReadOnlyList<SqlRelationBinding>? OutputBindings => null;
}
