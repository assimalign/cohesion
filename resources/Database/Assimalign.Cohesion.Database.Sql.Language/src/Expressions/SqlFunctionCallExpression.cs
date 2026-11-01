using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Sql.Language;

using Assimalign.Cohesion.Database.Language;

/// <summary>
/// Represents a function call such as <c>COUNT(*)</c> or <c>UPPER(name)</c>.
/// </summary>
[global::System.Diagnostics.DebuggerDisplay("SqlFunctionCallExpression")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Sql.Language.SqlFunctionCallExpression.DebugView))]
public sealed class SqlFunctionCallExpression : SqlExpression
{
    // A filtered call alone pays for the optional payload. Ordinary calls retain
    // the original object size and argument list, including their allocation budget.
    private readonly object _arguments;
    /// <summary>
    /// Initializes a new <see cref="SqlFunctionCallExpression"/>.
    /// </summary>
    /// <param name="functionName">The name of the function.</param>
    /// <param name="arguments">The function arguments.</param>
    /// <param name="location">The source location.</param>
    /// <param name="quantifier">The set quantifier.</param>
    /// <param name="orderBy">The input ordering.</param>
    /// <param name="hasWithinGroupOrdering">Whether the ordering was written in WITHIN GROUP.</param>
    /// <param name="filter">The optional aggregate FILTER condition.</param>
    internal SqlFunctionCallExpression(string functionName, IReadOnlyList<SqlExpression> arguments, Location? location,
        SqlSetQuantifier quantifier = SqlSetQuantifier.Default, IReadOnlyList<SqlOrderByColumn>? orderBy = null,
        bool hasWithinGroupOrdering = false, SqlExpression? filter = null)
        : base(location)
    {
        FunctionName = functionName;
        _arguments = filter is null ? arguments : new FilteredArguments(arguments, filter);
        Quantifier = quantifier;
        OrderBy = orderBy ?? [];
        HasWithinGroupOrdering = hasWithinGroupOrdering;
        int depth = DepthOf(arguments);
        foreach (var key in OrderBy) { depth = Math.Max(depth, DepthOf(key.Expression)); }
        if (filter is not null) { depth = Math.Max(depth, DepthOf(filter)); }
        Depth = 1 + depth;
    }

    /// <summary>Gets the set quantifier written inside the call.</summary>
    public SqlSetQuantifier Quantifier { get; }

    /// <summary>Gets the condition evaluated before this aggregate's input expressions.</summary>
    public SqlExpression? Filter => (_arguments as FilteredArguments)?.Filter;

    /// <summary>Gets the ordered input keys, also used for LISTAGG WITHIN GROUP.</summary>
    public IReadOnlyList<SqlOrderByColumn> OrderBy { get; }

    /// <summary>Gets whether the input ordering was written after the call in WITHIN GROUP.</summary>
    public bool HasWithinGroupOrdering { get; }

    /// <summary>
    /// Gets the name of the function being called.
    /// </summary>
    public string FunctionName { get; }

    /// <summary>
    /// Gets the list of arguments passed to the function.
    /// </summary>
    public IReadOnlyList<SqlExpression> Arguments => _arguments is FilteredArguments filtered ? filtered.Arguments : (IReadOnlyList<SqlExpression>)_arguments;

    private sealed record FilteredArguments(IReadOnlyList<SqlExpression> Arguments, SqlExpression Filter);

    private sealed class DebugView
    {
        private readonly global::Assimalign.Cohesion.Database.Sql.Language.SqlFunctionCallExpression _target;

        public DebugView(global::Assimalign.Cohesion.Database.Sql.Language.SqlFunctionCallExpression value)
        {
            _target = value;
        }

        public global::Assimalign.Cohesion.Database.Sql.Language.SqlSetQuantifier Quantifier => _target.Quantifier;
        public SqlExpression? Filter => _target.Filter;
        public global::System.Collections.Generic.IReadOnlyList<global::Assimalign.Cohesion.Database.Sql.Language.SqlOrderByColumn> OrderBy => _target.OrderBy;
        public bool HasWithinGroupOrdering => _target.HasWithinGroupOrdering;
        public string FunctionName => _target.FunctionName;
        public global::System.Collections.Generic.IReadOnlyList<global::Assimalign.Cohesion.Database.Sql.Language.SqlExpression> Arguments => _target.Arguments;
    }
}
