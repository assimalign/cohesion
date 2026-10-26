namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// A joined DML input. TargetBinding identifies the stored relation whose physical row location
/// and version must accompany each association; value equality is never target identity.
/// Columns remain in target-then-source order, independently of join probe direction.
/// </summary>
internal sealed record SqlDmlSource(SqlJoinPlan Associations, int TargetBinding);
