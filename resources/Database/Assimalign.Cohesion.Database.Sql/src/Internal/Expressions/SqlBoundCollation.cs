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
[global::System.Diagnostics.DebuggerDisplay("SqlBoundCollation")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Sql.Internal.SqlBoundCollation.DebugView))]
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

    private sealed class DebugView
    {
        public DebugView(global::Assimalign.Cohesion.Database.Sql.Internal.SqlBoundCollation value)
        {
            _ = value;
        }
    }
}
