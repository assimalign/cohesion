namespace Assimalign.Cohesion.Database.Language.Sql;

using Assimalign.Cohesion.Database.Language;

/// <summary>
/// Represents a binary expression such as <c>a + b</c>, <c>x = 1</c>, or <c>a AND b</c>.
/// </summary>
public sealed class SqlBinaryExpression : SqlExpression
{
    /// <summary>
    /// Initializes a new <see cref="SqlBinaryExpression"/>.
    /// </summary>
    /// <param name="left">The left operand.</param>
    /// <param name="op">The binary operator.</param>
    /// <param name="right">The right operand.</param>
    /// <param name="location">The source location.</param>
    internal SqlBinaryExpression(SqlExpression left, SqlBinaryOperator op, SqlExpression right, Location? location)
        : base(location)
    {
        Left = left;
        Operator = op;
        Right = right;
    }

    /// <summary>
    /// Gets the left operand.
    /// </summary>
    public SqlExpression Left { get; }

    /// <summary>
    /// Gets the binary operator.
    /// </summary>
    public SqlBinaryOperator Operator { get; }

    /// <summary>
    /// Gets the right operand.
    /// </summary>
    public SqlExpression Right { get; }
}
