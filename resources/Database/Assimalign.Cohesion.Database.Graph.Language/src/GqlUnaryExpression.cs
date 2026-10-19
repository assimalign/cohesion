using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Graph.Language;

/// <summary>A unary Boolean NOT or numeric sign expression.</summary>
public sealed class GqlUnaryExpression : GqlExpression
{
    /// <summary>Initializes an expression.</summary>
    /// <param name="operator">The operator: NOT, + or -.</param>
    /// <param name="operand">The operand.</param>
    /// <param name="location">The source span.</param>
    public GqlUnaryExpression(string @operator, GqlExpression operand, Location? location = null) : base(location)
    {
        Operator = @operator;
        Operand = operand;
    }

    /// <summary>Gets the operator: NOT, + or -.</summary>
    public string Operator { get; }
    /// <summary>Gets the operand.</summary>
    public GqlExpression Operand { get; }
}
