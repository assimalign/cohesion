namespace Assimalign.Cohesion.Database.Sql.Language;

using Assimalign.Cohesion.Database.Language;

/// <summary>
/// Represents a unary expression such as <c>NOT x</c> or <c>-5</c>.
/// </summary>
[global::System.Diagnostics.DebuggerDisplay("SqlUnaryExpression")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Sql.Language.SqlUnaryExpression.DebugView))]
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
        Depth = 1 + operand.Depth;
    }

    /// <summary>
    /// Gets the operand.
    /// </summary>
    public SqlExpression Operand { get; }

    /// <summary>
    /// Gets the unary operator.
    /// </summary>
    public SqlUnaryOperator Operator { get; }

    private sealed class DebugView
    {
        private readonly global::Assimalign.Cohesion.Database.Sql.Language.SqlUnaryExpression _target;

        public DebugView(global::Assimalign.Cohesion.Database.Sql.Language.SqlUnaryExpression value)
        {
            _target = value;
        }

        public global::Assimalign.Cohesion.Database.Sql.Language.SqlExpression Operand => _target.Operand;
        public global::Assimalign.Cohesion.Database.Sql.Language.SqlUnaryOperator Operator => _target.Operator;
    }
}
