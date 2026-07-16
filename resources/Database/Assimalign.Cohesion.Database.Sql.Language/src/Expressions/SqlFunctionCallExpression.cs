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
    internal SqlFunctionCallExpression(string functionName, IReadOnlyList<SqlExpression> arguments, Location? location)
        : base(location)
    {
        FunctionName = functionName;
        Arguments = arguments;
    }

    /// <summary>
    /// Gets the name of the function being called.
    /// </summary>
    public string FunctionName { get; }

    /// <summary>
    /// Gets the list of arguments passed to the function.
    /// </summary>
    public IReadOnlyList<SqlExpression> Arguments { get; }
}
