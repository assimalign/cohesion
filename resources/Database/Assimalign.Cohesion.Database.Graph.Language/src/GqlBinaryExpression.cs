using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Graph.Language;

/// <summary>A comparison or conjunction in a match predicate.</summary>
/// <param name="left">The left operand.</param>
/// <param name="operator">The normalized operator: AND, =, !=, &lt;, &lt;=, &gt;, or &gt;=.</param>
/// <param name="right">The right operand.</param>
/// <param name="location">The source span.</param>
public sealed class GqlBinaryExpression(GqlExpression left, string @operator, GqlExpression right,
    Location? location = null) : GqlExpression(location)
{
    /// <summary>Gets the left operand.</summary>
    public GqlExpression Left { get; } = left;
    /// <summary>Gets the normalized operator.</summary>
    public string Operator { get; } = @operator;
    /// <summary>Gets the right operand.</summary>
    public GqlExpression Right { get; } = right;
}
