using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Graph.Language.Tests.TestObjects;

// A complete structural projection, deliberately independent of GQL rendering. Only source
// locations/text and the redundant flat Elements representation are normalized away.
internal static class GqlSemanticTree
{
    internal static object?[] Path(GqlPathPattern path)
    {
        var result = new List<object?> { "path", path.Variable, path.Mode, path.Selector?.Kind, path.Selector?.Count, Expression(path.Predicate) };
        if (path.Elements is { } elements)
        {
            foreach (var element in elements)
            {
                result.Add(element switch
                {
                    GqlPathNode node => Node(node.Pattern),
                    GqlPathRelationship edge => Edge(edge.Pattern),
                    GqlParenthesizedPathPattern group => new object?[] { "group", group.Quantifier, Path(group.Pattern) },
                    _ => throw new InvalidOperationException(),
                });
            }
        }
        else
        {
            for (int i = 0; i < path.Nodes.Count; i++)
            { result.Add(Node(path.Nodes[i])); if (i < path.Relationships.Count) { result.Add(Edge(path.Relationships[i])); } }
        }
        return result.ToArray();
    }

    private static object?[] Node(GqlNodePattern node) => ["node", node.Variable, node.Labels, node.LabelExpression, Properties(node.Properties), Expression(node.Predicate)];
    private static object?[] Edge(GqlRelationshipPattern edge) => ["edge", edge.Variable, edge.Type, edge.LabelExpression, edge.Direction, edge.Quantifier, Properties(edge.Properties), Expression(edge.Predicate)];
    private static object?[] Properties(IEnumerable<KeyValuePair<string, GqlExpression>> properties)
    {
        var result = new List<object?>();
        foreach (var pair in properties) { result.Add(new object?[] { pair.Key, Expression(pair.Value) }); }
        return result.ToArray();
    }

    private static object?[] Expressions(IEnumerable<GqlExpression> expressions)
    {
        var result = new List<object?>();
        foreach (var expression in expressions) { result.Add(Expression(expression)); }
        return result.ToArray();
    }

    private static object? Expression(GqlExpression? expression) => expression switch
    {
        null => null,
        GqlLiteralExpression value => new object?[] { "literal", value.Value switch
            { null => "null", bool => "Boolean", long => "Int64", double => "Double", string => "String", _ => throw new InvalidOperationException() }, value.Value },
        GqlVariableExpression value => new object?[] { "variable", value.Variable },
        GqlParameterExpression value => new object?[] { "parameter", value.Name },
        GqlPropertyExpression value => new object?[] { "property", value.Variable, value.Property },
        GqlMemberExpression value => new object?[] { "member", Expression(value.Operand), value.Member },
        GqlSubscriptExpression value => new object?[] { "subscript", Expression(value.Operand), Expression(value.Index) },
        GqlBinaryExpression value => new object?[] { "binary", value.Operator, Expression(value.Left), Expression(value.Right) },
        GqlLogicalExpression value => new object?[] { "logical", value.Operator, Expressions(value.Operands) },
        GqlUnaryExpression value => new object?[] { "unary", value.Operator, Expression(value.Operand) },
        GqlIsNullExpression value => new object?[] { "null-test", value.IsNegated, Expression(value.Operand) },
        GqlTruthTestExpression value => new object?[] { "truth-test", value.IsNegated, value.TruthValue, Expression(value.Operand) },
        GqlInExpression value => new object?[] { "in", Expression(value.Operand), Expression(value.List) },
        GqlStringPredicateExpression value => new object?[] { "string-test", value.Operator, Expression(value.Left), Expression(value.Right) },
        GqlLabeledPredicate value => new object?[] { "labeled", value.Variable, value.LabelExpression, value.IsNegated },
        GqlListExpression value => new object?[] { "list", Expressions(value.Items) },
        GqlMapExpression value => new object?[] { "map", Properties(value.Entries) },
        GqlFunctionCallExpression value => new object?[] { "function", value.Function, Expressions(value.Arguments) },
        GqlNamedFunctionCallExpression value => new object?[] { "named-function", value.Name, value.Distinct, Expressions(value.Arguments) },
        GqlAggregateExpression value => new object?[] { "aggregate", value.Function, value.Distinct, Expression(value.Operand) },
        GqlCastExpression value => new object?[] { "cast", value.Target, Expression(value.Operand) },
        GqlCoalesceExpression value => new object?[] { "coalesce", Expressions(value.Operands) },
        GqlNullIfExpression value => new object?[] { "nullif", Expression(value.Left), Expression(value.Right) },
        GqlCaseExpression value => Case(value),
        _ => throw new InvalidOperationException(),
    };

    private static object?[] Case(GqlCaseExpression expression)
    {
        var alternatives = new List<object?>();
        foreach (var item in expression.Alternatives)
        { alternatives.Add(new object?[] { item.Comparison, Expression(item.When), Expression(item.Then) }); }
        return ["case", Expression(expression.Operand), alternatives.ToArray(), Expression(expression.Otherwise)];
    }
}
