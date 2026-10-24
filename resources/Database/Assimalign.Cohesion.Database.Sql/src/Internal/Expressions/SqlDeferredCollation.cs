using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>A comparison's collation that one of its operands decides only at run time (<see cref="SqlBoundCollation"/>).</summary>
[global::System.Diagnostics.DebuggerDisplay("SqlDeferredCollation")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Sql.Internal.SqlDeferredCollation.DebugView))]
internal sealed class SqlDeferredCollation
{
    private readonly SqlCollationCandidate _first;
    private readonly SqlCollationCandidate _second;
    private readonly Collation _default;

    /// <summary>Initializes a deferred collation.</summary>
    /// <param name="first">The first operand's collation.</param>
    /// <param name="second">The second operand's collation.</param>
    /// <param name="defaultCollation">The database default collation.</param>
    internal SqlDeferredCollation(SqlCollationCandidate first, SqlCollationCandidate second, Collation defaultCollation)
    {
        _first = first;
        _second = second;
        _default = defaultCollation;
    }

    /// <summary>Resolves the first operand's collation, then the second's, and combines them.</summary>
    /// <param name="subqueries">The statement's materialized subquery values, or null when it has none.</param>
    /// <returns>The comparison's collation.</returns>
    internal Collation Resolve(SqlSubqueryValues? subqueries)
    {
        var first = _first.Resolve(subqueries);
        var second = _second.Resolve(subqueries);
        return SqlCollationCandidate.Choose(first, second) ?? _default;
    }

    private sealed class DebugView
    {
        public DebugView(global::Assimalign.Cohesion.Database.Sql.Internal.SqlDeferredCollation value)
        {
            _ = value;
        }
    }
}
