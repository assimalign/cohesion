using System.Collections.Generic;
using System.Linq;
using Assimalign.Cohesion.Database.Graph.Language;

namespace Assimalign.Cohesion.Database.Graph.Internal;

internal static partial class GraphValueExpressions
{
    // Existence is a scalar leaf for aggregate/result-shape analysis, but its pattern variables
    // and inner expressions are inputs for lexical dependency analysis.
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
