namespace Assimalign.Cohesion.Database.Sql.Language;

using Assimalign.Cohesion.Database.Language;

/// <summary>
/// Represents a <c>BETWEEN</c> predicate such as <c>x BETWEEN 1 AND 10</c>.
/// </summary>
[global::System.Diagnostics.DebuggerDisplay("SqlBetweenExpression")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Sql.Language.SqlBetweenExpression.DebugView))]
public sealed class SqlBetweenExpression : SqlExpression
{
    /// <summary>
    /// Initializes a new <see cref="SqlBetweenExpression"/>.
    /// </summary>
    /// <param name="operand">The expression being tested.</param>
    /// <param name="low">The low bound.</param>
    /// <param name="high">The high bound.</param>
    /// <param name="isNegated">Whether the predicate is negated (<c>NOT BETWEEN</c>).</param>
    /// <param name="location">The source location.</param>
    internal SqlBetweenExpression(SqlExpression operand, SqlExpression low, SqlExpression high, bool isNegated, Location? location)
        : base(location)
    {
        Operand = operand;
        Low = low;
        High = high;
        IsNegated = isNegated;
        Depth = 1 + System.Math.Max(operand.Depth, System.Math.Max(low.Depth, high.Depth));
    }

    /// <summary>
    /// Gets the expression being tested.
    /// </summary>
    public SqlExpression Operand { get; }

    /// <summary>
    /// Gets the low bound of the range.
    /// </summary>
    public SqlExpression Low { get; }

    /// <summary>
    /// Gets the high bound of the range.
    /// </summary>
    public SqlExpression High { get; }

    /// <summary>
    /// Gets whether the predicate is negated (<c>NOT BETWEEN</c>).
    /// </summary>
    public bool IsNegated { get; }

    private sealed class DebugView
    {
        private readonly global::Assimalign.Cohesion.Database.Sql.Language.SqlBetweenExpression _target;

        public DebugView(global::Assimalign.Cohesion.Database.Sql.Language.SqlBetweenExpression value)
        {
            _target = value;
        }

        public global::Assimalign.Cohesion.Database.Sql.Language.SqlExpression Operand => _target.Operand;
        public global::Assimalign.Cohesion.Database.Sql.Language.SqlExpression Low => _target.Low;
        public global::Assimalign.Cohesion.Database.Sql.Language.SqlExpression High => _target.High;
        public bool IsNegated => _target.IsNegated;
    }
}
