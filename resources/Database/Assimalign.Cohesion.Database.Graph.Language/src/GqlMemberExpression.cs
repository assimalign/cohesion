using System.Collections.Generic;
using System.Linq;
using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Graph.Language;

/// <summary>A field reference on a map-valued expression.</summary>
public sealed class GqlMemberExpression : GqlExpression
{
    /// <summary>Initializes the expression.</summary>
    /// <param name="operand">The map expression.</param>
    /// <param name="member">The ordinal field name.</param>
    /// <param name="location">The optional source span.</param>
    public GqlMemberExpression(GqlExpression operand, string member, Location? location = null) : base(location)
    {
        Operand = operand;
        Member = member;
    }

    /// <summary>Gets the map expression.</summary>
    public GqlExpression Operand { get; }
    /// <summary>Gets the field name.</summary>
    public string Member { get; }
}
