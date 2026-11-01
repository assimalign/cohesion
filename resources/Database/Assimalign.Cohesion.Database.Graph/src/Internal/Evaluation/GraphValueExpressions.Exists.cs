using System.Collections.Generic;
using System.Linq;
using Assimalign.Cohesion.Database.Graph.Language;

namespace Assimalign.Cohesion.Database.Graph.Internal;

internal static partial class GraphValueExpressions
{
    // Existence is a scalar leaf for aggregate/result-shape analysis, but its pattern variables
    // and inner expressions are inputs for lexical dependency analysis.
    internal static GraphPlanningException InvalidPattern(GqlExpression expression)
        => new("CG05028", ErrorMessages.PatternComprehensionInvalid, expression.Location);

    internal static IEnumerable<GqlExpression> CorrelatedInputs(GqlExpression expression)
    {
        if (expression is GqlPatternComprehensionExpression pattern)
        {
            if (pattern.Pattern is null || pattern.Projection is null) { throw InvalidPattern(pattern); }
            foreach (var input in ExistsInputs(new GqlExistsExpression([new GqlMatchClause([pattern.Pattern], pattern.Predicate, false)])))
            { yield return input; }
            yield return pattern.Projection;
        }
        else if (expression is GqlExistsExpression exists)
        { foreach (var input in ExistsInputs(exists)) { yield return input; } }
    }

    private static IEnumerable<GqlExpression> PatternChildren(GqlPatternComprehensionExpression pattern)
    {
        if (pattern.Pattern is null || pattern.Projection is null) { throw InvalidPattern(pattern); }
        // Keep source child identity stable for the expression hash's enter/exit walks.
        // Only this new iterator pays the pattern-comprehension traversal state.
        if (pattern.Predicate is not null) { yield return pattern.Predicate; }
        if (pattern.Pattern.Predicate is not null) { yield return pattern.Pattern.Predicate; }
        foreach (var node in pattern.Pattern.Nodes)
        {
            foreach (var value in node.Properties.Values) { yield return value; }
            if (node.Predicate is not null) { yield return node.Predicate; }
        }
        foreach (var edge in pattern.Pattern.Relationships)
        {
            foreach (var value in edge.Properties.Values) { yield return value; }
            if (edge.Predicate is not null) { yield return edge.Predicate; }
        }
        yield return pattern.Projection;
    }

    internal static IEnumerable<GqlExpression> ExistsInputs(GqlExistsExpression expression)
    {
        foreach (var clause in expression.MatchClauses)
        {
            if (clause.Predicate is not null) { yield return clause.Predicate; }
            var pending = new Stack<GqlPathPattern>(clause.Paths);
            while (pending.TryPop(out var path))
            {
                if (path.Variable is { } name) { yield return new GqlVariableExpression(name); }
                if (path.Predicate is not null) { yield return path.Predicate; }
                foreach (var node in path.Elements is { } nodes ? nodes.OfType<GqlPathNode>().Select(item => item.Pattern) : path.Nodes)
                {
                    if (node.Variable is { } variable) { yield return new GqlVariableExpression(variable); }
                    foreach (var value in node.Properties.Values) { yield return value; }
                    if (node.Predicate is not null) { yield return node.Predicate; }
                }
                foreach (var edge in path.Elements is { } edges ? edges.OfType<GqlPathRelationship>().Select(item => item.Pattern) : path.Relationships)
                {
                    if (edge.Variable is { } variable) { yield return new GqlVariableExpression(variable); }
                    foreach (var value in edge.Properties.Values) { yield return value; }
                    if (edge.Predicate is not null) { yield return edge.Predicate; }
                }
                if (path.Elements is not null)
                { foreach (var group in path.Elements.OfType<GqlParenthesizedPathPattern>()) { pending.Push(group.Pattern); } }
            }
        }
    }
}
