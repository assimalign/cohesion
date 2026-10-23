using Assimalign.Cohesion.Database.Sql.Language;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>One written join step; comma items introduce unconditional CROSS steps.</summary>
internal sealed record SqlJoinStep(int Binding, SqlJoinType Kind, SqlBoundExpression? Condition, SqlJoinIndexPath? Access)
{
    internal SqlHashJoinPath? Hash { get; init; }
}
