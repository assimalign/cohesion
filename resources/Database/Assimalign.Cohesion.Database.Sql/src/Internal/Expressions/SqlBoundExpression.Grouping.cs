using Assimalign.Cohesion.Database.Sql.Language;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>A correlated mask whose ordinal is fixed when its owning group plan is completed.</summary>
[global::System.Diagnostics.DebuggerDisplay("SqlBoundGrouping")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(SqlBoundGrouping.DebugView))]
internal sealed class SqlBoundGrouping : SqlBoundExpression
{
    internal SqlBoundGrouping(SqlFunctionCallExpression call, int scope, int[] arguments)
        : base(SqlBoundExpressionKind.Grouping)
    {
        Call = call;
        Scope = scope;
        Arguments = arguments;
    }

    internal SqlFunctionCallExpression Call { get; }
    internal int Scope { get; }
    internal int[] Arguments { get; }
    internal int Ordinal { get; set; }

    private sealed class DebugView
    {
        public DebugView(SqlBoundGrouping value)
        {
            _ = value;
        }
    }
}
