using System;

using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Sql.Language;

/// <summary>Represents a scalar comparison against ANY, SOME or ALL rows of a subquery.</summary>
[global::System.Diagnostics.DebuggerDisplay("SqlQuantifiedComparisonExpression")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(SqlQuantifiedComparisonExpression.DebugView))]
public sealed class SqlQuantifiedComparisonExpression : SqlExpression
{
    internal SqlQuantifiedComparisonExpression(SqlExpression operand, SqlBinaryOperator comparison,
        SqlComparisonQuantifier quantifier, SqlQueryBodyExpression subquery, Location? location)
        : base(location)
    {
        Operand = operand;
        Operator = comparison;
        Quantifier = quantifier;
        Subquery = subquery;
        Depth = 1 + Math.Max(operand.Depth, subquery.ExpressionDepth);
    }

    /// <summary>Gets the scalar left operand.</summary>
    public SqlExpression Operand { get; }

    /// <summary>Gets the comparison operator.</summary>
    public SqlBinaryOperator Operator { get; }

    /// <summary>Gets the written quantifier.</summary>
    public SqlComparisonQuantifier Quantifier { get; }

    /// <summary>Gets the single-column query supplying comparison candidates.</summary>
    public SqlQueryBodyExpression Subquery { get; }

    private sealed class DebugView
    {
        private readonly SqlQuantifiedComparisonExpression _target;

        public DebugView(SqlQuantifiedComparisonExpression value) { _target = value; }

        public SqlExpression Operand => _target.Operand;
        public SqlBinaryOperator Operator => _target.Operator;
        public SqlComparisonQuantifier Quantifier => _target.Quantifier;
        public SqlQueryBodyExpression Subquery => _target.Subquery;
    }
}
