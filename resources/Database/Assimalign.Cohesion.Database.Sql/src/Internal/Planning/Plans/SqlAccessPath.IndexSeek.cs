using System.Collections.Generic;

using Assimalign.Cohesion.Database.Sql.Catalog;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// An index seek: an equality prefix over the index's leading key columns
/// (plan-time-evaluated, storage-coerced values) plus an optional range bound on
/// the next key column. The full WHERE stays the residual predicate — a seek only
/// narrows the candidate set, so re-evaluating everything keeps seek results
/// exactly equivalent to the scan they replace.
/// </summary>
/// <param name="Index">The chosen index's catalog description.</param>
/// <param name="EqualityValues">The equality prefix values, one per leading key column, coerced to storage types.</param>
/// <param name="Lower">The optional lower bound on the key column after the prefix.</param>
/// <param name="Upper">The optional upper bound on the key column after the prefix.</param>
internal sealed record SqlIndexSeekPath(
    SqlCatalogIndex Index,
    IReadOnlyList<object?> EqualityValues,
    SqlSeekBound? Lower,
    SqlSeekBound? Upper) : SqlAccessPath;
