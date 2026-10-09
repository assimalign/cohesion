namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// <c>COALESCE</c>: the first non-NULL argument, evaluated left to right and no further, so an
/// argument after the first non-NULL one never runs and never raises its error. A special form,
/// as in PostgreSQL's grammar, not a call through the function table's calling convention.
/// </summary>
internal sealed class SqlBoundCoalesce : SqlBoundExpression
{
    /// <summary>Initializes a bound <c>COALESCE</c>.</summary>
    /// <param name="arguments">The bound arguments, at least one.</param>
    internal SqlBoundCoalesce(SqlBoundExpression[] arguments)
        : base(SqlBoundExpressionKind.Coalesce)
    {
        Arguments = arguments;
    }

    /// <summary>Gets the bound arguments, in order.</summary>
    internal SqlBoundExpression[] Arguments { get; }
}
