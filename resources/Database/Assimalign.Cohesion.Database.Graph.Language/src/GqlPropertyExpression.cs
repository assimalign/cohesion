using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Graph.Language;

/// <summary>A scalar property of a bound graph element.</summary>
public sealed class GqlPropertyExpression : GqlExpression
{
    /// <summary>Initializes a new instance of the <see cref="GqlPropertyExpression"/> class.</summary>
    /// <param name="variable">The bound node or relationship variable.</param>
    /// <param name="property">The property key.</param>
    /// <param name="location">The source span.</param>
    public GqlPropertyExpression(string variable, string property, Location? location = null) : base(location)
    {
        Variable = variable;
        Property = property;
    }

    /// <summary>Gets the binding name.</summary>
    public string Variable { get; }
    /// <summary>Gets the property key.</summary>
    public string Property { get; }
}
