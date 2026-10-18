using System.Collections.Generic;
using System.Linq;
using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Graph.Language;

/// <summary>A list element or map field reference.</summary>
public sealed class GqlSubscriptExpression : GqlExpression
{
    /// <summary>Initializes the expression.</summary>
    /// <param name="operand">The list or map.</param>
    /// <param name="index">The integer index or string key.</param>
    /// <param name="location">The optional source span.</param>
    public GqlSubscriptExpression(GqlExpression operand, GqlExpression index, Location? location = null) : base(location)
    {
        Operand = operand;
        Index = index;
    }

    /// <summary>Gets the container.</summary>
    public GqlExpression Operand { get; }
    /// <summary>Gets the index expression.</summary>
    public GqlExpression Index { get; }
}
