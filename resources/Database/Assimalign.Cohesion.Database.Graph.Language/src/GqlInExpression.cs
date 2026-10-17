using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Graph.Language;

/// <summary>A list membership predicate with a bracketed right operand.</summary>
public sealed class GqlInExpression : GqlExpression
{
    /// <summary>Initializes an expression.</summary>
    /// <param name="operand">The value to find.</param>
    /// <param name="list">The bracketed list, supported only here.</param>
    /// <param name="location">The source span.</param>
    public GqlInExpression(GqlExpression operand, GqlListExpression list, Location? location = null) : base(location)
    {
        Operand = operand;
        List = list;
    }

    /// <summary>Gets the value to find.</summary>
    public GqlExpression Operand { get; }
    /// <summary>Gets the bracketed list, supported only here.</summary>
    public GqlListExpression List { get; }
}
