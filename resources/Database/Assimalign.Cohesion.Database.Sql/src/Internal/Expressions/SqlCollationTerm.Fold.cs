namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>An expression whose operands include a deferred candidate: the fold runs at run time.</summary>
[global::System.Diagnostics.DebuggerDisplay("SqlCollationFoldTerm")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Sql.Internal.SqlCollationFoldTerm.DebugView))]
internal sealed class SqlCollationFoldTerm : SqlCollationTerm
{
    private readonly SqlCollationCandidate[] _candidates;

    /// <summary>Initializes the fold.</summary>
    /// <param name="candidates">The operands' candidates, in order, led by the best fixed one before them.</param>
    internal SqlCollationFoldTerm(SqlCollationCandidate[] candidates)
    {
        _candidates = candidates;
    }

    /// <inheritdoc />
    internal override SqlCollationCandidate Resolve(SqlSubqueryValues? subqueries)
    {
        SqlCollationCandidate best = default;
        foreach (var candidate in _candidates)
        {
            var resolved = candidate.Resolve(subqueries);
            if (resolved.Priority > best.Priority)
            {
                best = resolved;
            }
        }

        return best;
    }

    private sealed class DebugView
    {
        public DebugView(global::Assimalign.Cohesion.Database.Sql.Internal.SqlCollationFoldTerm value)
        {
            _ = value;
        }
    }
}
