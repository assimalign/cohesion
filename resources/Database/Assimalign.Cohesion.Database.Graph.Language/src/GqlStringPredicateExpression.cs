using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Graph.Language;

/// <summary>An ordinal, case-sensitive string predicate.</summary>
public sealed class GqlStringPredicateExpression : GqlExpression
{
    /// <summary>Initializes an expression.</summary>
    /// <param name="left">The string to search.</param>
    /// <param name="operator">The predicate operator.</param>
    /// <param name="right">The search string.</param>
    /// <param name="location">The source span.</param>
    public GqlStringPredicateExpression(GqlExpression left, GqlStringPredicateOperator @operator, GqlExpression right, Location? location = null) : base(location)
    {
        Left = left;
        Operator = @operator;
        Right = right;
    }

    /// <summary>Gets the string to search.</summary>
    public GqlExpression Left { get; }
    /// <summary>Gets the predicate operator.</summary>
    public GqlStringPredicateOperator Operator { get; }
    /// <summary>Gets the search string.</summary>
    public GqlExpression Right { get; }
}
