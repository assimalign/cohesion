using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Graph.Language;

/// <summary>A reference to a bound node, relationship or path.</summary>
public sealed class GqlVariableExpression : GqlExpression
{
    /// <summary>Initializes a binding reference.</summary>
    /// <param name="variable">The case-sensitive binding name.</param>
    /// <param name="location">The optional source span.</param>
    public GqlVariableExpression(string variable, Location? location = null) : base(location)
    {
        Variable = variable;
    }

    /// <summary>Gets the binding name.</summary>
    public string Variable { get; }
}
