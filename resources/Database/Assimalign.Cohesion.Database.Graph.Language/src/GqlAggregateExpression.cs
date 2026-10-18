using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Graph.Language;

/// <summary>An ISO aggregate function, distinct from an ordinary scalar function call.</summary>
public sealed class GqlAggregateExpression : GqlExpression
{
    /// <summary>Initializes an aggregate expression.</summary>
    /// <param name="function">The aggregate function.</param>
    /// <param name="operand">The value expression, or null for COUNT(*).</param>
    /// <param name="distinct">Whether duplicate operand values are eliminated.</param>
    /// <param name="location">The complete source span.</param>
    public GqlAggregateExpression(GqlAggregateFunction function, GqlExpression? operand = null,
        bool distinct = false, Location? location = null) : base(location)
    {
        Function = function;
        Operand = operand;
        Distinct = distinct;
    }

    /// <summary>Gets the aggregate function.</summary>
    public GqlAggregateFunction Function { get; }
    /// <summary>Gets the operand, or null for COUNT(*).</summary>
    public GqlExpression? Operand { get; }
    /// <summary>Gets whether duplicate non-null values are eliminated before aggregation.</summary>
    public bool Distinct { get; }
}
