using System;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>A term whose collation failed to resolve when the expression was bound: resolving it fails the same way.</summary>
[global::System.Diagnostics.DebuggerDisplay("SqlCollationFailureTerm")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Sql.Internal.SqlCollationFailureTerm.DebugView))]
internal sealed class SqlCollationFailureTerm : SqlCollationTerm
{
    private readonly Action _raise;

    /// <summary>Initializes the term.</summary>
    /// <param name="raise">Reruns the failing resolution, which throws.</param>
    internal SqlCollationFailureTerm(Action raise)
    {
        _raise = raise;
    }

    /// <inheritdoc />
    internal override SqlCollationCandidate Resolve(SqlSubqueryValues? subqueries)
    {
        _raise();
        throw new InvalidOperationException(ErrorMessages.SqlCollationFailureTermResolveFailure);
    }

    private sealed class DebugView
    {
        public DebugView(global::Assimalign.Cohesion.Database.Sql.Internal.SqlCollationFailureTerm value)
        {
            _ = value;
        }
    }
}
