namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// A whole expression the plan computes elsewhere and stores at an ordinal of the row: a grouping
/// key or an aggregate's result in a grouped row (the aggregate slot), or a completed output column
/// that <c>ORDER BY</c> names by alias or position. The expression's own nodes are not bound; the
/// slot's value stands for them.
/// </summary>
internal sealed class SqlBoundSlot : SqlBoundExpression
{
    /// <summary>Initializes a slot reference.</summary>
    /// <param name="ordinal">The slot's ordinal in the evaluated row.</param>
    internal SqlBoundSlot(int ordinal)
        : base(SqlBoundExpressionKind.Slot)
    {
        Ordinal = ordinal;
    }

    /// <summary>Gets the slot's ordinal in the evaluated row.</summary>
    internal int Ordinal { get; }
}
