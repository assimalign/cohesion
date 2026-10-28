using System;

using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Sql.Language;

/// <summary>A function evaluated over an ordered partition of the query's result.</summary>
public sealed class SqlWindowExpression : SqlExpression
{
    internal SqlWindowExpression(SqlFunctionCallExpression function, string? name, SqlWindowSpecification? specification, Location? location)
        : base(location)
    {
        Function = function;
        WindowName = name;
        Specification = specification;
        Depth = 1 + Math.Max(function.Depth, specification?.ExpressionDepth ?? 0);
    }

    /// <summary>Gets the function and its arguments.</summary>
    public SqlFunctionCallExpression Function { get; }
    /// <summary>Gets the directly referenced name in OVER name.</summary>
    public string? WindowName { get; }
    /// <summary>Gets the parenthesized specification, including any inherited name.</summary>
    public SqlWindowSpecification? Specification { get; }
}
