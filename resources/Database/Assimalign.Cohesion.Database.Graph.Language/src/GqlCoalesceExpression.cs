using System.Collections.Generic;
using System.Linq;
using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Graph.Language;

/// <summary>The ISO COALESCE case abbreviation.</summary>
public sealed class GqlCoalesceExpression : GqlExpression
{
    /// <summary>Initializes the expression.</summary>
    /// <param name="operands">The expressions to test in order.</param>
    /// <param name="location">The optional source span.</param>
    public GqlCoalesceExpression(IEnumerable<GqlExpression> operands, Location? location = null) : base(location)
    {
        Operands = operands is null ? null! : System.Array.AsReadOnly(operands.ToArray());
    }

    /// <summary>Gets the expressions in evaluation order.</summary>
    public IReadOnlyList<GqlExpression> Operands { get; }
}
