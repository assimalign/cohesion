namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// A value fixed when the expression was bound: a literal, parsed and boxed once rather than for
/// every row, the BIGINT minimum a negated literal spells, or a persisted DEFAULT converted to its
/// column's type once per table version.
/// </summary>
internal sealed class SqlBoundConstant : SqlBoundExpression
{
    /// <summary>The bound SQL <c>NULL</c>.</summary>
    internal static SqlBoundConstant Null { get; } = new(null);

    /// <summary>Initializes a constant.</summary>
    /// <param name="value">The value, or null for SQL <c>NULL</c>.</param>
    internal SqlBoundConstant(object? value)
        : base(SqlBoundExpressionKind.Constant)
    {
        Value = value;
    }

    /// <summary>Gets the value every evaluation returns.</summary>
    internal object? Value { get; }
}
