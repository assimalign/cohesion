using System.Collections.Generic;
using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Graph.Language;

/// <summary>A resolved scalar function call, including niladic transaction datetime functions.</summary>
public sealed class GqlFunctionCallExpression : GqlExpression
{
    /// <summary>Initializes a function call over a copy of its arguments.</summary>
    /// <param name="function">The resolved function identity.</param>
    /// <param name="arguments">The arguments in source order; empty for a niladic function.</param>
    /// <param name="location">The complete source span.</param>
    public GqlFunctionCallExpression(GqlScalarFunction function, IEnumerable<GqlExpression> arguments, Location? location = null) : base(location)
    {
        Function = function;
        Arguments = arguments is null ? null! : new List<GqlExpression>(arguments).AsReadOnly();
    }

    /// <summary>Gets the resolved function identity.</summary>
    public GqlScalarFunction Function { get; }
    /// <summary>Gets the arguments in source order.</summary>
    public IReadOnlyList<GqlExpression> Arguments { get; }
    /// <summary>Gets whether the function is written without parentheses.</summary>
    public bool IsNiladic => Function is GqlScalarFunction.CurrentDate or GqlScalarFunction.CurrentTimestamp;
}
