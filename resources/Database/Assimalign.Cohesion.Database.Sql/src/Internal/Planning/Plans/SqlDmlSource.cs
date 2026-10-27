using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// A joined DML input. TargetBinding identifies the stored relation whose physical row location
/// and version must accompany each association; value equality is never target identity.
/// Columns remain in target-then-source order, independently of join probe direction.
/// VisibleOrdinals retains namespace output order after USING merges for RETURNING expansion.
/// </summary>
internal sealed record SqlDmlSource(SqlJoinPlan Associations, int TargetBinding, IReadOnlyList<int> VisibleOrdinals);
