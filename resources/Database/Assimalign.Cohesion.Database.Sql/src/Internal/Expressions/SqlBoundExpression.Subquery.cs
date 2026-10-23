namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// A scalar subquery or an <c>EXISTS</c>: the slot whose value the enclosing
/// <see cref="SqlSubqueryPlan"/> materializes before the expression is evaluated (a scalar's one
/// value or typed NULL, an <c>EXISTS</c>'s Boolean with its negation applied). The value is read from
/// the statement's <see cref="SqlSubqueryValues"/>, never by running a query from the evaluator.
/// </summary>
[global::System.Diagnostics.DebuggerDisplay("SqlBoundSubquery")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Sql.Internal.SqlBoundSubquery.DebugView))]
internal sealed class SqlBoundSubquery : SqlBoundExpression
{
    /// <summary>Initializes a bound subquery value.</summary>
    /// <param name="slot">The subquery's slot in the statement (<see cref="SqlSubquerySlot.Id"/>).</param>
    internal SqlBoundSubquery(int slot)
        : base(SqlBoundExpressionKind.Subquery)
    {
        Slot = slot;
    }

    /// <summary>Gets the subquery's slot in the statement.</summary>
    internal int Slot { get; }

    private sealed class DebugView
    {
        public DebugView(global::Assimalign.Cohesion.Database.Sql.Internal.SqlBoundSubquery value)
        {
            _ = value;
        }
    }
}
