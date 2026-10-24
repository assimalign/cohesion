using Assimalign.Cohesion.Database.Language;
using Assimalign.Cohesion.Database.Sql.Language;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>The bound Distinct predicate.</summary>
[global::System.Diagnostics.DebuggerDisplay("SqlBoundDistinct")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Sql.Internal.SqlBoundDistinct.DebugView))]
internal sealed class SqlBoundDistinct : SqlBoundExpression
{
    internal SqlBoundDistinct(SqlBoundExpression left, SqlBoundExpression right, bool isNegated, SqlBoundCollation collation, Location? location = null)
        : base(SqlBoundExpressionKind.Distinct)
    {
        Location = location;
        Left = left;
        Right = right;
        IsNegated = isNegated;
        Collation = collation;
    }

    internal SqlBoundExpression Left { get; }

    internal SqlBoundExpression Right { get; }

    internal bool IsNegated { get; }

    internal SqlBoundCollation Collation { get; }

    internal Location? Location { get; }

    private sealed class DebugView
    {
        public DebugView(global::Assimalign.Cohesion.Database.Sql.Internal.SqlBoundDistinct value)
        {
            _ = value;
        }
    }
}
