using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using Assimalign.Cohesion.Database.Graph.Language;

namespace Assimalign.Cohesion.Database.Graph.Internal;

// Public ASTs may be DAGs. Give every correlated expression occurrence its own identity
// before binding, copying only ancestors that contain it. Other nodes retain their identity.
internal static class GraphLexicalOccurrences
{
    internal static IReadOnlyList<GqlStage> Bind(IReadOnlyList<GqlStage> stages, CancellationToken token)
        => Map(stages, Stage, token);

    private static IReadOnlyList<T> Map<T>(IReadOnlyList<T> source, Func<T, CancellationToken, T> bind, CancellationToken token) where T : class
    {
        if (source is null) { return null!; }
        T[]? result = null;
        for (int i = 0; i < source.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            T value = bind(source[i], token);
            if (ReferenceEquals(value, source[i]) && result is null) { continue; }
            if (result is null)
            {
                result = new T[source.Count];
                for (int j = 0; j < i; j++) { result[j] = source[j]; }
            }
            result[i] = value;
        }
        return result ?? source;
    }

    private static GqlExpression? Optional(GqlExpression? value, CancellationToken token)
        => value is null ? null : Expression(value, token);

    private static GqlExpression Expression(GqlExpression value, CancellationToken token)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        token.ThrowIfCancellationRequested();
        switch (value)
        {
            case GqlPatternComprehensionExpression pattern:
                return new GqlPatternComprehensionExpression(Path(pattern.Pattern, token), Optional(pattern.Predicate, token),
                    Expression(pattern.Projection, token), pattern.Location);
            case GqlExistsExpression exists:
                return new GqlExistsExpression(Map(exists.MatchClauses, Clause, token), exists.Location);
            case GqlBinaryExpression item:
            {
                var left = Expression(item.Left, token);
                var right = Expression(item.Right, token);
                return ReferenceEquals(left, item.Left) && ReferenceEquals(right, item.Right) ? item : new GqlBinaryExpression(left, item.Operator, right, item.Location);
            }
            case GqlUnaryExpression item:
            {
                var operand = Expression(item.Operand, token);
                return ReferenceEquals(operand, item.Operand) ? item : new GqlUnaryExpression(item.Operator, operand, item.Location);
            }
            case GqlLogicalExpression item:
            {
                var operands = Map(item.Operands, Expression, token);
                return ReferenceEquals(operands, item.Operands) ? item : new GqlLogicalExpression(item.Operator, operands, item.Location);
            }
            case GqlFunctionCallExpression item:
            {
                var arguments = Map(item.Arguments, Expression, token);
                return ReferenceEquals(arguments, item.Arguments) ? item : new GqlFunctionCallExpression(item.Function, arguments, item.Location);
            }
            case GqlNamedFunctionCallExpression item:
            {
                var arguments = Map(item.Arguments, Expression, token);
                return ReferenceEquals(arguments, item.Arguments) ? item : new GqlNamedFunctionCallExpression(item.Name, arguments, item.Distinct, item.Location);
            }
            case GqlAggregateExpression item:
            {
                var operand = Optional(item.Operand, token);
                return ReferenceEquals(operand, item.Operand) ? item : new GqlAggregateExpression(item.Function, operand, item.Distinct, item.Location);
            }
            case GqlCastExpression item:
            {
                var operand = Expression(item.Operand, token);
                return ReferenceEquals(operand, item.Operand) ? item : new GqlCastExpression(operand, item.Target, item.Location);
            }
            case GqlIsNullExpression item:
            {
                var operand = Expression(item.Operand, token);
                return ReferenceEquals(operand, item.Operand) ? item : new GqlIsNullExpression(operand, item.IsNegated, item.Location);
            }
            case GqlTruthTestExpression item:
            {
                var operand = Expression(item.Operand, token);
                return ReferenceEquals(operand, item.Operand) ? item : new GqlTruthTestExpression(operand, item.TruthValue, item.IsNegated, item.Location);
            }
            case GqlStringPredicateExpression item:
            {
                var left = Expression(item.Left, token);
                var right = Expression(item.Right, token);
                return ReferenceEquals(left, item.Left) && ReferenceEquals(right, item.Right) ? item : new GqlStringPredicateExpression(left, item.Operator, right, item.Location);
            }
            case GqlListExpression item:
            {
                var items = Map(item.Items, Expression, token);
                return ReferenceEquals(items, item.Items) ? item : new GqlListExpression(items, item.Location);
            }
            case GqlCoalesceExpression item:
            {
                var operands = Map(item.Operands, Expression, token);
                return ReferenceEquals(operands, item.Operands) ? item : new GqlCoalesceExpression(operands, item.Location);
            }
            case GqlNullIfExpression item:
            {
                var left = Expression(item.Left, token);
                var right = Expression(item.Right, token);
                return ReferenceEquals(left, item.Left) && ReferenceEquals(right, item.Right) ? item : new GqlNullIfExpression(left, right, item.Location);
            }
            case GqlMemberExpression item:
            {
                var operand = Expression(item.Operand, token);
                return ReferenceEquals(operand, item.Operand) ? item : new GqlMemberExpression(operand, item.Member, item.Location);
            }
            case GqlSubscriptExpression item:
            {
                var operand = Expression(item.Operand, token);
                var index = Expression(item.Index, token);
                return ReferenceEquals(operand, item.Operand) && ReferenceEquals(index, item.Index) ? item : new GqlSubscriptExpression(operand, index, item.Location);
            }
            case GqlInExpression item:
            {
                var operand = Expression(item.Operand, token);
                var list = Expression(item.List, token);
                return ReferenceEquals(operand, item.Operand) && ReferenceEquals(list, item.List) ? item : new GqlInExpression(operand, list, item.Location);
            }
            case GqlListSliceExpression item:
            {
                var operand = Expression(item.Operand, token);
                var from = Optional(item.From, token);
                var to = Optional(item.To, token);
                return ReferenceEquals(operand, item.Operand) && ReferenceEquals(from, item.From) && ReferenceEquals(to, item.To) ? item : new GqlListSliceExpression(operand, from, to, item.Location);
            }
            case GqlListComprehensionExpression item:
            {
                var list = Expression(item.List, token);
                var predicate = Optional(item.Predicate, token);
                var projection = Optional(item.Projection, token);
                return ReferenceEquals(list, item.List) && ReferenceEquals(predicate, item.Predicate) && ReferenceEquals(projection, item.Projection) ? item : new GqlListComprehensionExpression(item.Variable, list, predicate, projection, item.Location);
            }
            case GqlListPredicateExpression item:
            {
                var list = Expression(item.List, token);
                var predicate = Expression(item.Predicate, token);
                return ReferenceEquals(list, item.List) && ReferenceEquals(predicate, item.Predicate) ? item : new GqlListPredicateExpression(item.Kind, item.Variable, list, predicate, item.Location);
            }
            case GqlReduceExpression item:
            {
                var initial = Expression(item.Initial, token);
                var list = Expression(item.List, token);
                var body = Expression(item.Body, token);
                return ReferenceEquals(initial, item.Initial) && ReferenceEquals(list, item.List) && ReferenceEquals(body, item.Body) ? item : new GqlReduceExpression(item.Accumulator, initial, item.Variable, list, body, item.Location);
            }
            case GqlCaseExpression item:
            {
                var operand = Optional(item.Operand, token);
                var alternatives = Map(item.Alternatives, Alternative, token);
                var otherwise = Optional(item.Otherwise, token);
                return ReferenceEquals(operand, item.Operand) && ReferenceEquals(alternatives, item.Alternatives) && ReferenceEquals(otherwise, item.Otherwise) ? item : new GqlCaseExpression(operand, alternatives, otherwise, item.Location);
            }
            case GqlMapExpression item:
            {
                var entries = Entries(item.Entries, token);
                return ReferenceEquals(entries, item.Entries) ? item : new GqlMapExpression(entries, item.Location);
            }
            default: return value;
        }
    }

    private static GqlStage Stage(GqlStage value, CancellationToken token)
    {
        switch (value)
        {
            case GqlMatchStage item:
            {
                var clause = Clause(item.Clause, token);
                return ReferenceEquals(clause, item.Clause) ? item : item with { Clause = clause };
            }
            case GqlInsertStage item:
            {
                var paths = Map(item.Paths, Path, token);
                return ReferenceEquals(paths, item.Paths) ? item : item with { Paths = paths };
            }
            case GqlMergeStage item:
            {
                var path = Path(item.Path, token);
                var onCreate = Map(item.OnCreate, Set, token);
                var onMatch = Map(item.OnMatch, Set, token);
                return ReferenceEquals(path, item.Path) && ReferenceEquals(onCreate, item.OnCreate) && ReferenceEquals(onMatch, item.OnMatch) ? item : item with { Path = path, OnCreate = onCreate, OnMatch = onMatch };
            }
            case GqlSetStage item:
            {
                var items = Map(item.Items, Set, token);
                return ReferenceEquals(items, item.Items) ? item : item with { Items = items };
            }
            case GqlFilterStage item:
            {
                var predicate = Expression(item.Predicate, token);
                return ReferenceEquals(predicate, item.Predicate) ? item : item with { Predicate = predicate };
            }
            case GqlForStage item:
            {
                var list = Expression(item.List, token);
                return ReferenceEquals(list, item.List) ? item : item with { List = list };
            }
            case GqlLetStage item:
            {
                var bindings = Map(item.Bindings, Projection, token);
                return ReferenceEquals(bindings, item.Bindings) ? item : item with { Bindings = bindings };
            }
            case GqlReturnStage item:
            {
                var projections = Map(item.Projections, Projection, token);
                var groupBy = Map(item.GroupBy!, Expression, token);
                var orderBy = Map(item.OrderBy, Sort, token);
                var offset = Optional(item.Offset, token);
                var limit = Optional(item.Limit, token);
                var predicate = Optional(item.Predicate, token);
                return ReferenceEquals(projections, item.Projections) && ReferenceEquals(groupBy, item.GroupBy) && ReferenceEquals(orderBy, item.OrderBy) && ReferenceEquals(offset, item.Offset) && ReferenceEquals(limit, item.Limit) && ReferenceEquals(predicate, item.Predicate) ? item : item with { Projections = projections, GroupBy = groupBy, OrderBy = orderBy, Offset = offset, Limit = limit, Predicate = predicate };
            }
            default: return value;
        }
    }

    private static GqlSetItem Set(GqlSetItem value, CancellationToken token)
    {
        switch (value)
        {
            case GqlSetPropertyItem item:
            {
                var boundValue = Expression(item.Value, token);
                return ReferenceEquals(boundValue, item.Value) ? item : item with { Value = boundValue };
            }
            case GqlSetPropertiesItem item:
            {
                var properties = Properties(item.Properties, token);
                return ReferenceEquals(properties, item.Properties) ? item : item with { Properties = properties };
            }
            default: return value;
        }
    }

    private static GqlProjection Projection(GqlProjection item, CancellationToken token)
    {
        if (item is null) { return null!; }
        var expression = Expression(item.Expression, token);
        return ReferenceEquals(expression, item.Expression) ? item : item with { Expression = expression };
    }

    private static GqlSortItem Sort(GqlSortItem item, CancellationToken token)
    {
        if (item is null) { return null!; }
        var expression = Expression(item.Expression, token);
        return ReferenceEquals(expression, item.Expression) ? item : item with { Expression = expression };
    }

    private static GqlMatchClause Clause(GqlMatchClause item, CancellationToken token)
    {
        if (item is null) { return null!; }
        var paths = Map(item.Paths, Path, token);
        var predicate = Optional(item.Predicate, token);
        return ReferenceEquals(paths, item.Paths) && ReferenceEquals(predicate, item.Predicate) ? item : item with { Paths = paths, Predicate = predicate };
    }

    private static GqlCaseAlternative Alternative(GqlCaseAlternative item, CancellationToken token)
    {
        if (item is null) { return null!; }
        var when = Expression(item.When, token);
        var then = Expression(item.Then, token);
        return ReferenceEquals(when, item.When) && ReferenceEquals(then, item.Then) ? item : item with { When = when, Then = then };
    }

    private static GqlNodePattern Node(GqlNodePattern item, CancellationToken token)
    {
        if (item is null) { return null!; }
        var properties = Properties(item.Properties, token);
        var predicate = Optional(item.Predicate, token);
        return ReferenceEquals(properties, item.Properties) && ReferenceEquals(predicate, item.Predicate) ? item : item with { Properties = properties, Predicate = predicate };
    }

    private static GqlRelationshipPattern Edge(GqlRelationshipPattern item, CancellationToken token)
    {
        if (item is null) { return null!; }
        var properties = Properties(item.Properties, token);
        var predicate = Optional(item.Predicate, token);
        return ReferenceEquals(properties, item.Properties) && ReferenceEquals(predicate, item.Predicate) ? item : item with { Properties = properties, Predicate = predicate };
    }

    private static IReadOnlyList<KeyValuePair<string, GqlExpression>> Entries(IReadOnlyList<KeyValuePair<string, GqlExpression>> source, CancellationToken token)
    {
        if (source is null) { return null!; }
        KeyValuePair<string, GqlExpression>[]? result = null;
        for (int i = 0; i < source.Count; i++)
        {
            var value = Expression(source[i].Value, token);
            if (!ReferenceEquals(value, source[i].Value) && result is null)
            {
                result = new KeyValuePair<string, GqlExpression>[source.Count];
                for (int j = 0; j < i; j++) { result[j] = source[j]; }
            }
            if (result is not null) { result[i] = new(source[i].Key, value); }
        }
        return result ?? source;
    }

    private static IReadOnlyDictionary<string, GqlExpression> Properties(IReadOnlyDictionary<string, GqlExpression> source, CancellationToken token)
    {
        if (source is null) { return null!; }
        Dictionary<string, GqlExpression>? result = null;
        foreach (var pair in source)
        {
            var value = Expression(pair.Value, token);
            if (!ReferenceEquals(value, pair.Value))
            { result ??= new(source, StringComparer.Ordinal); result[pair.Key] = value; }
        }
        return result ?? source;
    }

    private static GqlPathPattern Path(GqlPathPattern item, CancellationToken token)
    {
        if (item is null) { return null!; }
        // Preserve the established identity/depth/shape refusals before copying caller DAGs.
        // This pass runs only for programmatic statements containing EXISTS.
        _ = GraphPathShape.Create(item);
        return PathCore(item, token);
    }

    private static GqlPathPattern PathCore(GqlPathPattern item, CancellationToken token)
    {
        if (item is null) { return null!; }
        RuntimeHelpers.EnsureSufficientExecutionStack();
        var nodes = Map(item.Nodes, Node, token);
        var edges = Map(item.Relationships, Edge, token);
        var predicate = Optional(item.Predicate, token);
        GqlPathElement[]? elements = null;
        int nodeIndex = 0, edgeIndex = 0;
        if (item.Elements is { } source)
        {
            for (int i = 0; i < source.Count; i++)
            {
                GqlPathElement element = source[i];
                switch (element)
                {
                    case GqlPathNode node:
                        var boundNode = item.Nodes is { } originalNodes && nodeIndex < originalNodes.Count && Equals(originalNodes[nodeIndex], node.Pattern)
                            ? nodes[nodeIndex] : Node(node.Pattern, token);
                        nodeIndex++;
                        if (!ReferenceEquals(boundNode, node.Pattern)) { element = node with { Pattern = boundNode }; }
                        break;
                    case GqlPathRelationship edge:
                        var boundEdge = item.Relationships is { } originalEdges && edgeIndex < originalEdges.Count && Equals(originalEdges[edgeIndex], edge.Pattern)
                            ? edges[edgeIndex] : Edge(edge.Pattern, token);
                        edgeIndex++;
                        if (!ReferenceEquals(boundEdge, edge.Pattern)) { element = edge with { Pattern = boundEdge }; }
                        break;
                    case GqlParenthesizedPathPattern nested:
                        var path = PathCore(nested.Pattern, token);
                        if (!ReferenceEquals(path, nested.Pattern)) { element = nested with { Pattern = path }; }
                        break;
                }
                if (!ReferenceEquals(element, source[i]) && elements is null)
                {
                    elements = new GqlPathElement[source.Count];
                    for (int j = 0; j < i; j++) { elements[j] = source[j]; }
                }
                if (elements is not null) { elements[i] = element; }
            }
        }
        return ReferenceEquals(nodes, item.Nodes) && ReferenceEquals(edges, item.Relationships) &&
            ReferenceEquals(predicate, item.Predicate) && elements is null ? item :
            item with { Nodes = nodes, Relationships = edges, Predicate = predicate, Elements = elements ?? item.Elements };
    }

}
