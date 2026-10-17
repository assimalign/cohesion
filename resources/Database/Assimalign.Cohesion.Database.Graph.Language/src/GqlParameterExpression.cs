using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Graph.Language;

/// <summary>A named scalar parameter referenced with $ or @.</summary>
public sealed class GqlParameterExpression : GqlExpression
{
    /// <summary>Initializes a named parameter expression.</summary>
    /// <param name="name">The bare, case-sensitive name without its sigil.</param>
    /// <param name="location">The complete reference's source span.</param>
    public GqlParameterExpression(string name, Location? location = null) : base(location)
    {
        Name = name;
    }

    /// <summary>Gets the bare, case-sensitive parameter name.</summary>
    public string Name { get; }
}
