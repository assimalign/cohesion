namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// <c>[NOT] IN</c> over a subquery: the bound operand and the slot whose materialized values are the
/// candidates. Every value of one subquery carries the subquery's column collation, so one
/// collation serves every candidate.
/// </summary>
internal sealed class SqlBoundInSubquery : SqlBoundExpression
{
    /// <summary>Initializes a bound subquery membership test.</summary>
    /// <param name="operand">The bound tested value.</param>
    /// <param name="slot">The subquery's slot in the statement (<see cref="SqlSubquerySlot.Id"/>).</param>
    /// <param name="collation">The collation of the operand and the subquery's values.</param>
    /// <param name="isNegated"><see langword="true"/> for <c>NOT IN</c>.</param>
    internal SqlBoundInSubquery(SqlBoundExpression operand, int slot, SqlBoundCollation collation, bool isNegated)
        : base(SqlBoundExpressionKind.InSubquery)
    {
        Operand = operand;
        Slot = slot;
        Collation = collation;
        IsNegated = isNegated;
    }

    /// <summary>Gets the bound tested value.</summary>
    internal SqlBoundExpression Operand { get; }

    /// <summary>Gets the subquery's slot in the statement.</summary>
    internal int Slot { get; }

    /// <summary>Gets the collation of the operand and the subquery's values.</summary>
    internal SqlBoundCollation Collation { get; }

    /// <summary>Gets whether the test is <c>NOT IN</c>.</summary>
    internal bool IsNegated { get; }
}
