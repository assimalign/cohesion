using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Sql.Language;

using Assimalign.Cohesion.Database.Language;

/// <summary>
/// Represents a function call such as <c>COUNT(*)</c> or <c>UPPER(name)</c>.
/// </summary>
public sealed class SqlFunctionCallExpression : SqlExpression
{
    /// <summary>
    /// Initializes a new <see cref="SqlFunctionCallExpression"/>.
    /// </summary>
    /// <param name="functionName">The name of the function.</param>
    /// <param name="arguments">The function arguments.</param>
    /// <param name="location">The source location.</param>
    /// <param name="quantifier">The set quantifier.</param>
    /// <param name="orderBy">The input ordering.</param>
    /// <param name="hasWithinGroupOrdering">Whether the ordering was written in WITHIN GROUP.</param>
    internal SqlFunctionCallExpression(string functionName, IReadOnlyList<SqlExpression> arguments, Location? location,
        SqlSetQuantifier quantifier = SqlSetQuantifier.Default, IReadOnlyList<SqlOrderByColumn>? orderBy = null,
        bool hasWithinGroupOrdering = false)
        : base(location)
    {
        FunctionName = functionName;
        Arguments = arguments;
        Quantifier = quantifier;
        OrderBy = orderBy ?? [];
        HasWithinGroupOrdering = hasWithinGroupOrdering;
        int depth = DepthOf(arguments);
        foreach (var key in OrderBy) { depth = Math.Max(depth, DepthOf(key.Expression)); }
        Depth = 1 + depth;
    }

    /// <summary>Gets the set quantifier written inside the call.</summary>
    public SqlSetQuantifier Quantifier { get; }

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
    public IReadOnlyList<SqlExpression> Arguments { get; }
}
