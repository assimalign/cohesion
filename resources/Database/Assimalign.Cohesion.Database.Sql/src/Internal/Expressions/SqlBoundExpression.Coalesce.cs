using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// <c>COALESCE</c>: the first non-NULL argument, evaluated left to right and no further, so an
/// argument after the first non-NULL one never runs and never raises its error. A special form,
/// as in PostgreSQL's grammar, not a call through the function table's calling convention.
/// </summary>
[global::System.Diagnostics.DebuggerDisplay("SqlBoundCoalesce")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Sql.Internal.SqlBoundCoalesce.DebugView))]
internal sealed class SqlBoundCoalesce : SqlBoundExpression
{
    /// <summary>Initializes a bound <c>COALESCE</c>.</summary>
    /// <param name="arguments">The bound arguments, at least one.</param>
    internal SqlBoundCoalesce(SqlBoundExpression[] arguments, DatabaseType resultType = DatabaseType.Null)
        : base(SqlBoundExpressionKind.Coalesce)
    {
        ResultType = resultType;
        Arguments = arguments;
    }

    internal DatabaseType ResultType { get; }

    /// <summary>Gets the bound arguments, in order.</summary>
    internal SqlBoundExpression[] Arguments { get; }

    private sealed class DebugView
    {
        public DebugView(global::Assimalign.Cohesion.Database.Sql.Internal.SqlBoundCoalesce value)
        {
            _ = value;
        }
    }
}
