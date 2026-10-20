using System.Collections.Generic;

using Assimalign.Cohesion.Database.Sql.Catalog;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// A correlated equality-prefix seek into one input, with probe ordinals in
/// the accumulated physical row and the inner columns of the seek prefix, resolved
/// once when the statement is planned rather than by name for every outer row.
/// A null path means a buffered nested-loop scan.
/// </summary>
internal sealed record SqlJoinIndexPath(
    int InnerBinding,
    SqlCatalogIndex Index,
    IReadOnlyList<int> OuterOrdinals,
    IReadOnlyList<SqlCatalogColumn> InnerColumns);
