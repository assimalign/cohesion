namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// A column reference resolved to its ordinal in the row the expression is evaluated over: the
/// table's columns, or a join's columns in FROM-then-JOIN order. The name, qualifier and any
/// ambiguity were settled when the expression was bound.
/// </summary>
internal sealed class SqlBoundColumn : SqlBoundExpression
{
    /// <summary>Initializes a column reference.</summary>
    /// <param name="ordinal">The column's ordinal in the evaluated row.</param>
    internal SqlBoundColumn(int ordinal)
        : base(SqlBoundExpressionKind.Column)
    {
        Ordinal = ordinal;
    }

    /// <summary>Gets the column's ordinal in the evaluated row.</summary>
    internal int Ordinal { get; }
}
