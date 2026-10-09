using System;

using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// The collation one comparison of a bound expression uses (or the collation an <c>ORDER BY</c> key,
/// a <c>DISTINCT</c> column or a grouping key compares under), resolved when the expression was
/// bound. The unbound evaluator searched both operand trees for it on every comparison of every row.
/// </summary>
/// <remarks>
/// <para>
/// Precedence is unchanged (<see cref="SqlExpressionEvaluator.ResolveCollation"/>): an explicit
/// <c>COLLATE</c> (priority 3) over an explicitly collated column or a subquery's value (2) over an
/// implicitly collated column (1), the first operand winning a tie, the database default when
/// neither operand has one.
/// </para>
/// <para>
/// Two shapes cannot be decided when the expression is bound, and resolve when the comparison runs:
/// a term under an <c>IN (subquery)</c> node, whose collation the unbound evaluator took from the
/// subquery's first value when it returned rows and from the IN's operand when it returned none; and
/// a term whose collation failed to resolve (an unknown column or collation), which fails the
/// comparison, as before, only when it is reached. Every other comparison carries a fixed collation
/// and resolves with one field read.
/// </para>
/// </remarks>
internal readonly struct SqlBoundCollation
{
    private readonly Collation? _collation;
    private readonly SqlDeferredCollation? _deferred;

    /// <summary>Initializes a collation fixed when the expression was bound.</summary>
    /// <param name="collation">The collation.</param>
    internal SqlBoundCollation(Collation collation)
    {
        _collation = collation;
        _deferred = null;
    }

    /// <summary>Initializes a collation resolved when the comparison runs.</summary>
    /// <param name="deferred">How to resolve it.</param>
    internal SqlBoundCollation(SqlDeferredCollation deferred)
    {
        _collation = null;
        _deferred = deferred;
    }

    /// <summary>Gets whether the collation is fixed (resolving it reads a field).</summary>
    internal bool IsFixed => _deferred is null;

    /// <summary>Resolves the collation.</summary>
    /// <param name="subqueries">The statement's materialized subquery values, or null when it has none.</param>
    /// <returns>The collation.</returns>
    /// <exception cref="Exception">A term of the comparison does not resolve; the exception is the one the term raises.</exception>
    internal Collation Resolve(SqlSubqueryValues? subqueries) => _deferred is null ? _collation! : _deferred.Resolve(subqueries);

    /// <summary>
    /// Combines the collations of two operands the way a comparison of them resolves its collation:
    /// the second wins only with a strictly higher priority, and the database default applies when
    /// neither has one.
    /// </summary>
    /// <param name="first">The first operand's collation.</param>
    /// <param name="second">The second operand's collation, or <see langword="default"/> for none.</param>
    /// <param name="defaultCollation">The database default collation.</param>
    /// <returns>The comparison's collation.</returns>
    internal static SqlBoundCollation Of(SqlCollationCandidate first, SqlCollationCandidate second, Collation defaultCollation)
        => first.IsDeferred || second.IsDeferred
            ? new SqlBoundCollation(new SqlDeferredCollation(first, second, defaultCollation))
            : new SqlBoundCollation(SqlCollationCandidate.Choose(first, second) ?? defaultCollation);
}

/// <summary>A comparison's collation that one of its operands decides only at run time (<see cref="SqlBoundCollation"/>).</summary>
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
}

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

/// <summary>What decides a deferred <see cref="SqlCollationCandidate"/> when its comparison runs.</summary>
internal abstract class SqlCollationTerm
{
    /// <summary>Resolves the term.</summary>
    /// <param name="subqueries">The statement's materialized subquery values, or null when it has none.</param>
    /// <returns>The fixed candidate.</returns>
    internal abstract SqlCollationCandidate Resolve(SqlSubqueryValues? subqueries);
}

/// <summary>An expression whose operands include a deferred candidate: the fold runs at run time.</summary>
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
}

/// <summary>A <c>COLLATE</c> over a deferred operand: an inner explicit collation still wins.</summary>
internal sealed class SqlCollationCollateTerm : SqlCollationTerm
{
    private readonly SqlCollationCandidate _operand;
    private readonly Collation? _collation;
    private readonly string _name;

    /// <summary>Initializes the term.</summary>
    /// <param name="operand">The operand's candidate.</param>
    /// <param name="collation">
    /// The collation the clause names, resolved when the expression was bound, or null when the
    /// name did not resolve: the parser refuses an unknown name, so only a tree no SQL text spells
    /// has one, and it fails, as before, only when the clause applies.
    /// </param>
    /// <param name="name">The collation name the clause names.</param>
    internal SqlCollationCollateTerm(SqlCollationCandidate operand, Collation? collation, string name)
    {
        _operand = operand;
        _collation = collation;
        _name = name;
    }

    /// <inheritdoc />
    internal override SqlCollationCandidate Resolve(SqlSubqueryValues? subqueries)
    {
        var inner = _operand.Resolve(subqueries);
        return inner.Priority == 3 ? inner : new SqlCollationCandidate(_collation ?? Collation.FromName(_name), 3);
    }
}

/// <summary>
/// An <c>IN (subquery)</c> node: when the subquery returned rows, its values' collation (priority 2);
/// when it returned none, the collation of the IN's operand.
/// </summary>
internal sealed class SqlCollationSubqueryTerm : SqlCollationTerm
{
    private readonly int _slot;
    private readonly Collation _collation;
    private readonly SqlCollationCandidate _empty;

    /// <summary>Initializes the term.</summary>
    /// <param name="slot">The subquery's slot in the statement.</param>
    /// <param name="collation">The subquery column's collation, which its values carry.</param>
    /// <param name="empty">The candidate when the subquery returned no rows.</param>
    internal SqlCollationSubqueryTerm(int slot, Collation collation, SqlCollationCandidate empty)
    {
        _slot = slot;
        _collation = collation;
        _empty = empty;
    }

    /// <inheritdoc />
    internal override SqlCollationCandidate Resolve(SqlSubqueryValues? subqueries)
        => subqueries is not null && subqueries.TryGet(_slot, out var values) && values.Length > 0
            ? new SqlCollationCandidate(_collation, 2)
            : _empty.Resolve(subqueries);
}

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
