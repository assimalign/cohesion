using System.Collections.Generic;

using Assimalign.Cohesion.Database.Sql.Catalog;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>A seek whose subset completeness was proved at binding time.</summary>
internal sealed record SqlPartialIndexSeekPath(SqlCatalogIndex Index, IReadOnlyList<object?> EqualityValues,
    SqlSeekBound? Lower, SqlSeekBound? Upper, string Proof) : SqlIndexSeekPath(Index, EqualityValues, Lower, Upper);
