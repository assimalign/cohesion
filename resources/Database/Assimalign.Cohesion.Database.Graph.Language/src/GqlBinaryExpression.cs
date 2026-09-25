using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Graph.Language;

/// <summary>A comparison or conjunction in a match predicate.</summary>
public sealed class GqlBinaryExpression : GqlExpression
{
    /// <summary>Initializes a new instance of the <see cref="GqlBinaryExpression"/> class.</summary>
    /// <param name="left">The left operand.</param>
    /// <param name="operator">The normalized operator: AND, =, !=, &lt;, &lt;=, &gt;, or &gt;=.</param>
    /// <param name="right">The right operand.</param>
    /// <param name="location">The source span.</param>
    public GqlBinaryExpression(GqlExpression left, string @operator, GqlExpression right,
        Location? location = null) : base(location)
    {
        Left = left;
        Operator = @operator;
        Right = right;
    }

    /// <summary>Gets the left operand.</summary>
    public GqlExpression Left { get; }
    /// <summary>Gets the normalized operator.</summary>
    public string Operator { get; }
    /// <summary>Gets the right operand.</summary>
    public GqlExpression Right { get; }
}
