using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using Assimalign.Cohesion.Database.Graph.Language;

namespace Assimalign.Cohesion.Database.Graph.Internal;

/// <summary>Compares typed expression structure, excluding source text and spans.</summary>
internal sealed class GraphExpressionShape : IEqualityComparer<GqlExpression>
{
    private readonly Dictionary<GqlExpression, int> _hashes = new(ReferenceEqualityComparer.Instance);
    private readonly CancellationToken _token;

    internal GraphExpressionShape(CancellationToken token) => _token = token;

    public bool Equals(GqlExpression? left, GqlExpression? right) => Equivalent(left, right, _token);

    // Cache every subtree, so lookups while validating children never rewalk their ancestors.
    // Neo4j NormalizeWithAndReturnClauses.scala:259-263,306-317 likewise hashes AST structure.
    public int GetHashCode(GqlExpression expression)
    {
        if (_hashes.TryGetValue(expression, out int cached)) { return cached; }
        var pending = new Stack<(GqlExpression Expression, bool Visited)>();
        pending.Push((expression, false));
        while (pending.TryPop(out var step))
        {
            _token.ThrowIfCancellationRequested();
            if (_hashes.ContainsKey(step.Expression)) { continue; }
            // Leave malformed chains to the planner's located shape diagnostic. Hashing a key
            // precedes its scope validation, so it must not enumerate a missing operand list.
            if (step.Expression is GqlLogicalExpression { Operands: null })
            { _hashes.Add(step.Expression, 0); continue; }
            if (!step.Visited)
            {
                pending.Push((step.Expression, true));
                foreach (var child in GraphValueExpressions.Children(step.Expression))
                { if (child is not null && !_hashes.ContainsKey(child)) { pending.Push((child, false)); } }
                continue;
            }
            var hash = new HashCode();
            hash.Add(step.Expression.GetType());
            switch (step.Expression)
            {
                case GqlAggregateExpression aggregate: hash.Add(aggregate.Function); hash.Add(aggregate.Distinct); hash.Add(aggregate.Operand is null); break;
                case GqlLiteralExpression literal:
                    hash.Add(literal.Value?.GetType());
                    if (literal.Value is null or bool or string || GraphValueComparer.IsNumber(literal.Value!)) { hash.Add(literal.Value); }
                    break;
                case GqlParameterExpression parameter: hash.Add(parameter.Name, StringComparer.Ordinal); break;
                case GqlVariableExpression variable: hash.Add(variable.Variable, StringComparer.Ordinal); break;
                case GqlPropertyExpression property: hash.Add(property.Variable, StringComparer.Ordinal); hash.Add(property.Property, StringComparer.Ordinal); break;
                case GqlBinaryExpression binary: hash.Add(binary.Operator, StringComparer.Ordinal); break;
                case GqlLogicalExpression logical: hash.Add(logical.Operator); break;
                case GqlUnaryExpression unary: hash.Add(unary.Operator, StringComparer.Ordinal); break;
                case GqlIsNullExpression test: hash.Add(test.IsNegated); break;
                case GqlTruthTestExpression test: hash.Add(test.IsNegated); hash.Add(test.TruthValue); break;
                case GqlStringPredicateExpression predicate: hash.Add(predicate.Operator); break;
                case GqlLabeledPredicate labeled: hash.Add(labeled.Variable, StringComparer.Ordinal); hash.Add(labeled.IsNegated); hash.Add(labeled.LabelExpression); break;
                case GqlMapExpression map:
                    foreach (var entry in map.Entries) { _token.ThrowIfCancellationRequested(); hash.Add(entry.Key, StringComparer.Ordinal); }
                    break;
                case GqlCaseExpression conditional:
                    hash.Add(conditional.Operand is null); hash.Add(conditional.Otherwise is null);
                    foreach (var branch in conditional.Alternatives) { _token.ThrowIfCancellationRequested(); hash.Add(branch.Comparison, StringComparer.Ordinal); }
                    break;
                case GqlMemberExpression member: hash.Add(member.Member, StringComparer.Ordinal); break;
            }
            foreach (var child in GraphValueExpressions.Children(step.Expression))
            { _token.ThrowIfCancellationRequested(); hash.Add(child is null ? 0 : _hashes[child]); }
            _hashes.Add(step.Expression, hash.ToHashCode());
        }
        return _hashes[expression];
    }

    internal static bool Equivalent(GqlExpression? left, GqlExpression? right, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        RuntimeHelpers.EnsureSufficientExecutionStack();
        if (left is null || right is null) { return false; }
        if (ReferenceEquals(left, right)) { return true; }
        bool same = (left, right) switch
        {
            (GqlAggregateExpression a, GqlAggregateExpression b) => a.Function == b.Function && a.Distinct == b.Distinct && (a.Operand is null) == (b.Operand is null),
            (GqlLiteralExpression a, GqlLiteralExpression b) => a.Value?.GetType() == b.Value?.GetType() && object.Equals(a.Value, b.Value),
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
        { if (!second.MoveNext() || !Equivalent(first.Current, second.Current, token)) { return false; } }
        return !second.MoveNext();
    }
}
