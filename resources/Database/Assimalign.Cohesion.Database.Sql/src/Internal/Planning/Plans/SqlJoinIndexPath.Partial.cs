using System.Collections.Generic;

using Assimalign.Cohesion.Database.Sql.Catalog;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>A correlated seek with a proof applying only to the indexed input.</summary>
internal sealed record SqlPartialJoinIndexPath(int InnerBinding, SqlCatalogIndex Index,
    IReadOnlyList<int> OuterOrdinals, IReadOnlyList<SqlCatalogColumn> InnerColumns, string Proof)
    : SqlJoinIndexPath(InnerBinding, Index, OuterOrdinals, InnerColumns);
