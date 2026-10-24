namespace Assimalign.Cohesion.Database.Sql.Language;

using Assimalign.Cohesion.Database.Language;

/// <summary>Represents an IS [NOT] TRUE, FALSE or UNKNOWN test.</summary>
[global::System.Diagnostics.DebuggerDisplay("SqlBooleanTestExpression")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Sql.Language.SqlBooleanTestExpression.DebugView))]
public sealed class SqlBooleanTestExpression : SqlExpression
{
    /// <summary>Initializes an IS [NOT] TRUE, FALSE or UNKNOWN test.</summary>
    /// <param name="operand">The tested operand.</param>
    /// <param name="truthValue">The truth value being tested.</param>
    /// <param name="isNegated">Whether the predicate includes NOT.</param>
    /// <param name="location">The source location.</param>
    internal SqlBooleanTestExpression(SqlExpression operand, SqlTruthValue truthValue, bool isNegated, Location? location)
        : base(location)
    {
        Operand = operand;
        TruthValue = truthValue;
        IsNegated = isNegated;
        Depth = 1 + operand.Depth;
    }

    /// <summary>Gets the tested operand.</summary>
    public SqlExpression Operand { get; }

    /// <summary>Gets the truth value being tested.</summary>
    public SqlTruthValue TruthValue { get; }

    /// <summary>Gets whether the predicate includes NOT.</summary>
    public bool IsNegated { get; }

    private sealed class DebugView
    {
        private readonly global::Assimalign.Cohesion.Database.Sql.Language.SqlBooleanTestExpression _target;

        public DebugView(global::Assimalign.Cohesion.Database.Sql.Language.SqlBooleanTestExpression value)
        {
            _target = value;
        }

        public global::Assimalign.Cohesion.Database.Sql.Language.SqlExpression Operand => _target.Operand;
        public global::Assimalign.Cohesion.Database.Sql.Language.SqlTruthValue TruthValue => _target.TruthValue;
        public bool IsNegated => _target.IsNegated;
    }
}
