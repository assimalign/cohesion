using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Graph.Language;

/// <summary>An IS [NOT] TRUE, FALSE or UNKNOWN test.</summary>
public sealed class GqlTruthTestExpression : GqlExpression
{
    /// <summary>Initializes an expression.</summary>
    /// <param name="operand">The Boolean value to test.</param>
    /// <param name="truthValue">The tested truth value; null means UNKNOWN.</param>
    /// <param name="isNegated">Whether NOT is present.</param>
    /// <param name="location">The source span.</param>
    public GqlTruthTestExpression(GqlExpression operand, bool? truthValue, bool isNegated, Location? location = null) : base(location)
    {
        Operand = operand;
        TruthValue = truthValue;
        IsNegated = isNegated;
    }

    /// <summary>Gets the Boolean value to test.</summary>
    public GqlExpression Operand { get; }
    /// <summary>Gets the tested truth value; null means UNKNOWN.</summary>
    public bool? TruthValue { get; }
    /// <summary>Gets whether NOT is present.</summary>
    public bool IsNegated { get; }
}
