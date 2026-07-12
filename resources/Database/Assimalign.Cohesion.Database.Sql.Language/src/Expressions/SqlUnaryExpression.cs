namespace Assimalign.Cohesion.Database.Sql.Language;

using Assimalign.Cohesion.Database.Language;

/// <summary>
/// Represents a unary expression such as <c>NOT x</c> or <c>-5</c>.
/// </summary>
public sealed class SqlUnaryExpression : SqlExpression
{
    /// <summary>
    /// Initializes a new <see cref="SqlUnaryExpression"/>.
    /// </summary>
    /// <param name="operand">The operand.</param>
    /// <param name="op">The unary operator.</param>
    /// <param name="location">The source location.</param>
    internal SqlUnaryExpression(SqlExpression operand, SqlUnaryOperator op, Location? location)
        : base(location)
    {
        Operand = operand;
        Operator = op;
    }

    /// <summary>
    /// Gets the operand.
    /// </summary>
    public SqlExpression Operand { get; }

    /// <summary>
    /// Gets the unary operator.
    /// </summary>
    public SqlUnaryOperator Operator { get; }
}
