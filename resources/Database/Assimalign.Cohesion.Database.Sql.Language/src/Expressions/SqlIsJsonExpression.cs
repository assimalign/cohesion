using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Sql.Language;

/// <summary>Represents an IS [NOT] JSON [VALUE | ARRAY | OBJECT | SCALAR] predicate.</summary>
[global::System.Diagnostics.DebuggerDisplay("SqlIsJsonExpression")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(SqlIsJsonExpression.DebugView))]
public sealed class SqlIsJsonExpression : SqlExpression
{
    internal SqlIsJsonExpression(SqlExpression operand, SqlJsonKind kind, bool isNegated, Location? location)
        : base(location)
    {
        Operand = operand;
        Kind = kind;
        IsNegated = isNegated;
        Depth = 1 + operand.Depth;
    }

    /// <summary>Gets the tested character or JSON expression.</summary>
    public SqlExpression Operand { get; }

    /// <summary>Gets the required JSON kind.</summary>
    public SqlJsonKind Kind { get; }

    /// <summary>Gets whether the predicate includes NOT.</summary>
    public bool IsNegated { get; }

    private sealed class DebugView
    {
        private readonly SqlIsJsonExpression _target;

        public DebugView(SqlIsJsonExpression value) { _target = value; }

        public SqlExpression Operand => _target.Operand;
        public SqlJsonKind Kind => _target.Kind;
        public bool IsNegated => _target.IsNegated;
    }
}
