using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Graph.Language;

/// <summary>A scalar property of a bound graph element.</summary>
/// <param name="variable">The bound node or relationship variable.</param>
/// <param name="property">The property key.</param>
/// <param name="location">The source span.</param>
public sealed class GqlPropertyExpression(string variable, string property, Location? location = null) : GqlExpression(location)
{
    /// <summary>Gets the binding name.</summary>
    public string Variable { get; } = variable;
    /// <summary>Gets the property key.</summary>
    public string Property { get; } = property;
}
