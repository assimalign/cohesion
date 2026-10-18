using System;
using System.Linq;
using System.Runtime.CompilerServices;
using Assimalign.Cohesion.Database.Graph.Language;

namespace Assimalign.Cohesion.Database.Graph.Internal;

/// <summary>Compares projected expression structure independently of spelling and source spans.</summary>
internal static class GraphExpressionShape
{
    internal static bool Equivalent(GqlExpression? left, GqlExpression? right)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        if (left is null || right is null) { return false; }
        bool same = (left, right) switch
        {
            (GqlLiteralExpression a, GqlLiteralExpression b) => GraphValueComparer.Instance.Equals(a.Value, b.Value),
            (GqlParameterExpression a, GqlParameterExpression b) => a.Name == b.Name,
            (GqlVariableExpression a, GqlVariableExpression b) => a.Variable == b.Variable,
            (GqlPropertyExpression a, GqlPropertyExpression b) => a.Variable == b.Variable && a.Property == b.Property,
            (GqlBinaryExpression a, GqlBinaryExpression b) => a.Operator == b.Operator,
            (GqlLogicalExpression a, GqlLogicalExpression b) => a.Operands is not null && b.Operands is not null && a.Operator == b.Operator,
            (GqlUnaryExpression a, GqlUnaryExpression b) => a.Operator == b.Operator,
            (GqlIsNullExpression a, GqlIsNullExpression b) => a.IsNegated == b.IsNegated,
            (GqlTruthTestExpression a, GqlTruthTestExpression b) => a.IsNegated == b.IsNegated && a.TruthValue == b.TruthValue,
            (GqlStringPredicateExpression a, GqlStringPredicateExpression b) => a.Operator == b.Operator,
            (GqlLabeledPredicate a, GqlLabeledPredicate b) => a.Variable == b.Variable && a.IsNegated == b.IsNegated && a.LabelExpression is not null && a.LabelExpression.Equals(b.LabelExpression),
            (GqlMapExpression a, GqlMapExpression b) => a.Entries is not null && b.Entries is not null && a.Entries.Select(entry => entry.Key).SequenceEqual(b.Entries.Select(entry => entry.Key), StringComparer.Ordinal),
            (GqlCaseExpression a, GqlCaseExpression b) => a.Alternatives is not null && b.Alternatives is not null &&
                a.Alternatives.All(branch => branch is not null) && b.Alternatives.All(branch => branch is not null) && (a.Operand is null) == (b.Operand is null) &&
                (a.Otherwise is null) == (b.Otherwise is null) && a.Alternatives.Select(branch => branch.Comparison).SequenceEqual(b.Alternatives.Select(branch => branch.Comparison)),
            (GqlMemberExpression a, GqlMemberExpression b) => a.Member == b.Member,
            (GqlCoalesceExpression a, GqlCoalesceExpression b) => a.Operands is not null && b.Operands is not null,
            (GqlListExpression, GqlListExpression) or (GqlInExpression, GqlInExpression) or
                (GqlNullIfExpression, GqlNullIfExpression) or
                (GqlSubscriptExpression, GqlSubscriptExpression) => true,
            _ => false,
        };
        if (!same) { return false; }
        using var first = GraphValueExpressions.Children(left).GetEnumerator();
        using var second = GraphValueExpressions.Children(right).GetEnumerator();
        while (first.MoveNext())
        { if (!second.MoveNext() || !Equivalent(first.Current, second.Current)) { return false; } }
        return !second.MoveNext();
    }
}
