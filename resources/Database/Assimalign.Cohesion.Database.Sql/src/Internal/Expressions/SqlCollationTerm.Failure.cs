using System;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>A term whose collation failed to resolve when the expression was bound: resolving it fails the same way.</summary>
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
        throw new InvalidOperationException("A collation that failed to bind resolved on a later attempt.");
    }
}
