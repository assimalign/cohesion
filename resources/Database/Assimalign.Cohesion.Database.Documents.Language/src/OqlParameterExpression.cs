using System.Collections.Generic;
using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Documents.Language;

/// <summary>A named or positional query parameter.</summary>
/// <param name="name">The parameter name without its dollar or at-sign prefix.</param>
/// <param name="location">The source span.</param>
public sealed class OqlParameterExpression(string name, Location? location = null) : OqlExpression(location)
{
    /// <summary>Gets the parameter name without its prefix.</summary>
    public string Name { get; } = name;
}
