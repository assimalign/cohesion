using System.Collections.Generic;

using Assimalign.Cohesion.Database.Sql.Catalog;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>A proven key order; only this path may omit SELECT's sort.</summary>
internal sealed record SqlOrderedIndexPath(SqlCatalogIndex Index, IReadOnlyList<object?> EqualityValues,
    SqlSeekBound? Lower, SqlSeekBound? Upper, bool Reverse, string? Proof)
    : SqlIndexSeekPath(Index, EqualityValues, Lower, Upper);
