using System.Collections.Generic;
using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Documents.Language;

/// <summary>A supported aggregate function call.</summary>
public sealed class OqlCallExpression : OqlExpression
{
    /// <summary>Initializes a new instance of the <see cref="OqlCallExpression"/> class.</summary>
    /// <param name="name">The uppercase function name.</param>
    /// <param name="arguments">The function arguments.</param>
    /// <param name="location">The source span.</param>
    public OqlCallExpression(string name, IReadOnlyList<OqlExpression> arguments, Location? location = null) : base(location)
    {
        Name = name;
        Arguments = arguments;
    }

    /// <summary>Gets the uppercase function name.</summary>
    public string Name { get; }
    /// <summary>Gets the function arguments.</summary>
    public IReadOnlyList<OqlExpression> Arguments { get; }
}
