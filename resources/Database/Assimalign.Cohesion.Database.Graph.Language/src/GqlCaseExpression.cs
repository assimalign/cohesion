using System.Collections.Generic;
using System.Linq;
using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Graph.Language;

/// <summary>A simple or searched CASE with ordered alternatives.</summary>
public sealed class GqlCaseExpression : GqlExpression
{
    /// <summary>Initializes the expression.</summary>
    /// <param name="operand">The comparison operand, or null for searched CASE.</param>
    /// <param name="alternatives">The WHEN/THEN alternatives.</param>
    /// <param name="otherwise">The ELSE expression, or null.</param>
    /// <param name="location">The optional source span.</param>
    public GqlCaseExpression(GqlExpression? operand, IEnumerable<GqlCaseAlternative> alternatives, GqlExpression? otherwise, Location? location = null) : base(location)
    {
        Operand = operand;
        Alternatives = alternatives is null ? null! : System.Array.AsReadOnly(alternatives.ToArray());
        Otherwise = otherwise;
    }

    /// <summary>Gets the simple CASE operand.</summary>
    public GqlExpression? Operand { get; }
    /// <summary>Gets the ordered alternatives.</summary>
    public IReadOnlyList<GqlCaseAlternative> Alternatives { get; }
    /// <summary>Gets the optional ELSE expression.</summary>
    public GqlExpression? Otherwise { get; }
}
