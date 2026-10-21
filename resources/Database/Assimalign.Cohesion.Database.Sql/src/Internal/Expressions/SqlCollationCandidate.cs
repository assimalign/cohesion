using System;

using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// One expression's collation and how strongly it binds: 3 for an explicit <c>COLLATE</c>, 2 for a
/// column declared with a collation or a subquery's value, 1 for a column that inherits the database
/// default, 0 when the expression has none. A deferred candidate carries the term that decides it
/// when the comparison runs (<see cref="SqlBoundCollation"/>).
/// </summary>
internal readonly struct SqlCollationCandidate
{
    /// <summary>Initializes a fixed candidate.</summary>
    /// <param name="collation">The collation, or null when the expression has none.</param>
    /// <param name="priority">How strongly the collation binds, 0 to 3.</param>
    internal SqlCollationCandidate(Collation? collation, int priority)
    {
        Collation = collation;
        Priority = priority;
        Term = null;
    }

    private SqlCollationCandidate(SqlCollationTerm term)
    {
        Collation = null;
        Priority = 0;
        Term = term;
    }

    /// <summary>Gets the collation of a fixed candidate.</summary>
    internal Collation? Collation { get; }

    /// <summary>Gets the priority of a fixed candidate.</summary>
    internal int Priority { get; }

    /// <summary>Gets the term that decides a deferred candidate, or null for a fixed one.</summary>
    internal SqlCollationTerm? Term { get; }

    /// <summary>Gets whether the candidate is decided only at run time.</summary>
    internal bool IsDeferred => Term is not null;

    /// <summary>Creates a candidate a term decides at run time.</summary>
    /// <param name="term">The term.</param>
    /// <returns>The deferred candidate.</returns>
    internal static SqlCollationCandidate Deferred(SqlCollationTerm term) => new(term);

    /// <summary>Resolves the candidate to a fixed one.</summary>
    /// <param name="subqueries">The statement's materialized subquery values, or null when it has none.</param>
    /// <returns>The fixed candidate.</returns>
    internal SqlCollationCandidate Resolve(SqlSubqueryValues? subqueries) => Term is null ? this : Term.Resolve(subqueries);

    /// <summary>The comparison rule over two fixed candidates: the second wins only with a strictly higher priority.</summary>
    /// <param name="first">The first operand's candidate.</param>
    /// <param name="second">The second operand's candidate.</param>
    /// <returns>The winning collation, or null when neither has one.</returns>
    internal static Collation? Choose(SqlCollationCandidate first, SqlCollationCandidate second)
        => second.Priority > first.Priority ? second.Collation : first.Collation;

    /// <summary>
    /// Folds the candidates of an expression's operands, in operand order, into the expression's own
    /// candidate: the first operand of the highest priority wins.
    /// </summary>
    /// <param name="candidates">The operands' candidates, in order.</param>
    /// <returns>The expression's candidate; fixed when every operand's is.</returns>
    internal static SqlCollationCandidate Fold(ReadOnlySpan<SqlCollationCandidate> candidates)
    {
        SqlCollationCandidate best = default;
        for (int index = 0; index < candidates.Length; index++)
        {
            var candidate = candidates[index];
            if (candidate.IsDeferred)
            {
                // The rest of the fold waits for run time; the best fixed candidate so far leads it.
                var terms = new SqlCollationCandidate[candidates.Length - index + 1];
                terms[0] = best;
                candidates[index..].CopyTo(terms.AsSpan(1));
                return Deferred(new SqlCollationFoldTerm(terms));
            }

            if (candidate.Priority > best.Priority)
            {
                best = candidate;
            }
        }

        return best;
    }

    /// <summary>The fold of two operands' candidates (see <see cref="Fold(ReadOnlySpan{SqlCollationCandidate})"/>).</summary>
    /// <param name="first">The first operand's candidate.</param>
    /// <param name="second">The second operand's candidate.</param>
    /// <returns>The expression's candidate.</returns>
    internal static SqlCollationCandidate Fold(SqlCollationCandidate first, SqlCollationCandidate second)
        => !first.IsDeferred && !second.IsDeferred
            ? (second.Priority > first.Priority ? second : first.Priority > 0 ? first : default)
            : Fold([first, second]);
}
