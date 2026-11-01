using System.Collections.Generic;

using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// One distinct aggregate call of a grouping plan, bound to the overload it resolved to, so the
/// executor creates one accumulator of that function per group instead of finding it by name.
/// </summary>
/// <remarks>
/// A class with get-only properties, not a positional record, as <see cref="SqlBoundCall"/> is:
/// <see cref="FirstTarget"/> is derived from <see cref="Targets"/> once, at construction, and a
/// record's <c>with</c> would copy it beside a replaced <see cref="Targets"/>, so the one-argument
/// add would convert to the old target while the buffered add used the new one.
/// </remarks>
[global::System.Diagnostics.DebuggerDisplay("SqlGroupAggregate")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Sql.Internal.SqlGroupAggregate.DebugView))]
internal sealed class SqlGroupAggregate
{
    private readonly object _ordering;
    /// <summary>Initializes a bound aggregate call.</summary>
    /// <param name="call">The aggregate call as written.</param>
    /// <param name="function">The overload the call resolved to.</param>
    /// <param name="arguments">The bound arguments over the input row; empty for <c>name(*)</c>, as in <c>COUNT(*)</c>.</param>
    /// <param name="targets">The storage type each argument converts to before it is added, or null when none converts.</param>
    /// <param name="collation">The collation the arguments compare under, which the function's context carries (<c>MIN</c>, <c>MAX</c>).</param>
    /// <param name="database">The database whose statement runs the aggregate.</param>
    /// <param name="orderBy">The bound input ordering.</param>
    /// <param name="argumentCollations">The per-argument DISTINCT collations.</param>
    /// <param name="filter">The predicate evaluated before any aggregate inputs.</param>
    internal SqlGroupAggregate(SqlFunctionCallExpression call, SqlAggregateFunction function,
        SqlBoundExpression[] arguments, DatabaseType[]? targets, SqlBoundCollation collation, DatabaseName database, IReadOnlyList<SqlBoundOrdering> orderBy, IReadOnlyList<SqlBoundCollation> argumentCollations,
        SqlBoundExpression? filter)
    {
        ArgumentCollations = argumentCollations;
        _ordering = filter is null ? orderBy : new FilteredOrdering(orderBy, filter);
        Call = call;
        Function = function;
        Arguments = arguments;
        Targets = targets;
        FirstTarget = targets is { Length: > 0 } ? targets[0] : DatabaseType.Null;
        Collation = collation;
        Database = database;
    }

    /// <summary>Gets each DISTINCT argument's own comparison collation.</summary>
    internal IReadOnlyList<SqlBoundCollation> ArgumentCollations { get; }

    internal SqlBoundExpression? Filter => (_ordering as FilteredOrdering)?.Filter;

    /// <summary>Gets input ordering bound over the source row.</summary>
    internal IReadOnlyList<SqlBoundOrdering> OrderBy => _ordering is FilteredOrdering filtered ? filtered.OrderBy : (IReadOnlyList<SqlBoundOrdering>)_ordering;

    /// <summary>Gets the aggregate call as written.</summary>
    internal SqlFunctionCallExpression Call { get; }

    /// <summary>Gets the overload the call resolved to.</summary>
    internal SqlAggregateFunction Function { get; }

    /// <summary>Gets the bound arguments over the input row; empty for <c>name(*)</c>, as in <c>COUNT(*)</c>.</summary>
    internal SqlBoundExpression[] Arguments { get; }

    /// <summary>Gets the storage type each argument converts to before it is added, or null when none converts.</summary>
    internal DatabaseType[]? Targets { get; }

    /// <summary>
    /// Gets the storage type the first argument converts to (<see cref="DatabaseType.Null"/> when it
    /// converts to none), read by every row of a one-argument call without testing the array.
    /// </summary>
    internal DatabaseType FirstTarget { get; }

    /// <summary>Gets the collation the arguments compare under, which the function's context carries (<c>MIN</c>, <c>MAX</c>).</summary>
    internal SqlBoundCollation Collation { get; }

    /// <summary>Gets the database whose statement runs the aggregate.</summary>
    internal DatabaseName Database { get; }

    private sealed record FilteredOrdering(IReadOnlyList<SqlBoundOrdering> OrderBy, SqlBoundExpression Filter);

    private sealed class DebugView
    {
        public DebugView(global::Assimalign.Cohesion.Database.Sql.Internal.SqlGroupAggregate value)
        {
            _ = value;
        }
    }
}
