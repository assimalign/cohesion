using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Sql.Language;

using Assimalign.Cohesion.Database.Language;

/// <summary>
/// Represents an <c>IN</c> predicate such as <c>x IN (1, 2, 3)</c> or <c>x IN (SELECT ...)</c>.
/// </summary>
[global::System.Diagnostics.DebuggerDisplay("SqlInExpression")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Sql.Language.SqlInExpression.DebugView))]
public sealed class SqlInExpression : SqlExpression
{
    /// <summary>
    /// Initializes a new <see cref="SqlInExpression"/>.
    /// </summary>
    /// <param name="operand">The expression being tested.</param>
    /// <param name="values">The list of values, if using a value list.</param>
    /// <param name="subquery">The subquery, if using a subquery.</param>
    /// <param name="isNegated">Whether the predicate is negated (<c>NOT IN</c>).</param>
    /// <param name="location">The source location.</param>
    internal SqlInExpression(SqlExpression operand, IReadOnlyList<SqlExpression>? values, SqlQueryBodyExpression? subquery, bool isNegated, Location? location)
        : base(location)
    {
        Operand = operand;
        Values = values;
        Subquery = subquery;
        IsNegated = isNegated;
        Depth = 1 + System.Math.Max(operand.Depth, System.Math.Max(DepthOf(values), subquery?.ExpressionDepth ?? 0));
    }

    /// <summary>
    /// Gets the expression being tested.
    /// </summary>
    public SqlExpression Operand { get; }

    /// <summary>
    /// Gets the list of values, if this is a value-list form.
    /// </summary>
    public IReadOnlyList<SqlExpression>? Values { get; }

    /// <summary>
    /// Gets the subquery, if this is a subquery form.
    /// </summary>
    public SqlQueryBodyExpression? Subquery { get; }

    /// <summary>
    /// Gets whether the predicate is negated (<c>NOT IN</c>).
    /// </summary>
    public bool IsNegated { get; }

    private sealed class DebugView
    {
        private readonly global::Assimalign.Cohesion.Database.Sql.Language.SqlInExpression _target;

        public DebugView(global::Assimalign.Cohesion.Database.Sql.Language.SqlInExpression value)
        {
            _target = value;
        }

        public global::Assimalign.Cohesion.Database.Sql.Language.SqlExpression Operand => _target.Operand;
        public global::System.Collections.Generic.IReadOnlyList<global::Assimalign.Cohesion.Database.Sql.Language.SqlExpression>? Values => _target.Values;
        public global::Assimalign.Cohesion.Database.Sql.Language.SqlQueryBodyExpression? Subquery => _target.Subquery;
        public bool IsNegated => _target.IsNegated;
    }
}
