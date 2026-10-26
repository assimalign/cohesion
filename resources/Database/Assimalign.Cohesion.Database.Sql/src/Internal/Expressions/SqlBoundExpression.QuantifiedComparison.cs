using Assimalign.Cohesion.Database.Language;
using Assimalign.Cohesion.Database.Sql.Language;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>A scalar comparison folded over a typed subquery slot.</summary>
[global::System.Diagnostics.DebuggerDisplay("SqlBoundQuantifiedComparison")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(SqlBoundQuantifiedComparison.DebugView))]
internal sealed class SqlBoundQuantifiedComparison : SqlBoundExpression
{
    internal SqlBoundQuantifiedComparison(SqlBoundExpression operand, int slot, SqlBinaryOperator comparison,
        bool all, SqlBoundCollation collation, Location? location)
        : base(SqlBoundExpressionKind.QuantifiedComparison)
    {
        Operand = operand;
        Slot = slot;
        Operator = comparison;
        All = all;
        Collation = collation;
        Location = location;
    }

    internal SqlBoundExpression Operand { get; }
    internal int Slot { get; }
    internal SqlBinaryOperator Operator { get; }
    internal bool All { get; }
    internal SqlBoundCollation Collation { get; }
    internal Location? Location { get; }

    private sealed class DebugView
    {
        public DebugView(SqlBoundQuantifiedComparison value) { _ = value; }
    }
}
