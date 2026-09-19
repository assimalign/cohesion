using System.Collections.Generic;
using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Documents.Language;

/// <summary>A supported aggregate function call.</summary>
/// <param name="name">The uppercase function name.</param>
/// <param name="arguments">The function arguments.</param>
/// <param name="location">The source span.</param>
public sealed class OqlCallExpression(string name, IReadOnlyList<OqlExpression> arguments, Location? location = null) : OqlExpression(location)
{
    /// <summary>Gets the uppercase function name.</summary>
    public string Name { get; } = name;
    /// <summary>Gets the function arguments.</summary>
    public IReadOnlyList<OqlExpression> Arguments { get; } = arguments;
}
