using System;
using System.Collections.Generic;
using System.Threading;
using Assimalign.Cohesion.Database.Graph.Language;

namespace Assimalign.Cohesion.Database.Graph.Internal;

internal static partial class GraphValueExpressions
{
    // This expression accepts relationship chains. Node/edge declarations have fixed entity
    // kinds; bracketed quantified edges bind relationship groups. Only the path name shadows.
    // Keep outer callbacks live: reduce may ask about an accumulator's changing shape here.
    private static (GraphProjectionKind Kind, bool Group, bool Empty)? PatternBinding(GqlPatternComprehensionExpression expression, string name)
    {
        if (expression.Pattern?.Nodes is null || expression.Pattern.Relationships is null || expression.Projection is null) { throw InvalidPattern(expression); }
        if (name == expression.Pattern.Variable) { return (GraphProjectionKind.Path, false, false); }
        foreach (var node in expression.Pattern.Nodes)
        { if (name == node.Variable) { return (GraphProjectionKind.Node, false, false); } }
        foreach (var edge in expression.Pattern.Relationships)
        { if (name == edge.Variable) { return (GraphProjectionKind.Relationship, edge.Quantifier is not null, edge.Quantifier?.Max == 0); } }
        return null;
    }

    private static bool PatternGroup(GqlPatternComprehensionExpression expression, Func<string, int, bool> outer,
        int depth, Func<GqlNamedFunctionCallExpression, GqlExpression?>? native, GraphReductionAnalysis? analysis)
        => IsGroup(expression.Projection, (name, access) => PatternBinding(expression, name) is { } binding
            ? binding.Group && access == 0 : outer(name, access), depth, native, analysis);

    private static bool PatternMayReturn(GqlPatternComprehensionExpression expression, IReadOnlyDictionary<string, object?>? parameters,
        bool entity, Func<string, int, bool, bool>? outer, int depth, Func<GqlNamedFunctionCallExpression, GraphFunctionType>? functions,
        Func<GqlNamedFunctionCallExpression, GqlExpression?>? native, GraphReductionAnalysis? analysis)
        => MayReturn(expression.Projection, parameters, entity, (name, access, entities) => PatternBinding(expression, name) is { } binding
            ? entities ? !binding.Empty && (access == 0 || binding.Group && access == 1) : binding.Group && access == 0
            : outer?.Invoke(name, access, entities) ?? entities && access == 0, depth, functions, native, analysis);

    private static GraphEntityShape PatternEntity(GqlPatternComprehensionExpression expression, Func<string, int, GraphEntityShape> outer,
        int depth, CancellationToken token, Func<GqlNamedFunctionCallExpression, GraphFunctionType>? functions,
        Func<GqlNamedFunctionCallExpression, GqlExpression?>? native, GraphReductionAnalysis? analysis)
        => EntityShape(expression.Projection, (name, access) => PatternBinding(expression, name) is { } binding
            ? !binding.Empty && access == (binding.Group ? 1 : 0) ? new GraphEntityShape(binding.Kind, false) : default
            : outer(name, access), depth, token, functions, native, analysis);

    private static bool PatternScalar(GqlPatternComprehensionExpression expression, IReadOnlyDictionary<string, object?>? parameters,
        Func<string, int, bool> outer, int depth, Func<GqlNamedFunctionCallExpression, GraphFunctionType>? functions,
        Func<GqlNamedFunctionCallExpression, GqlExpression?>? native, GraphReductionAnalysis? analysis)
        => IsKnownScalar(expression.Projection, parameters, (name, access) => PatternBinding(expression, name) is { } binding
            ? binding.Kind is GraphProjectionKind.Node or GraphProjectionKind.Relationship && access == (binding.Group ? 2 : 1)
            : outer(name, access), depth, functions, native, analysis);
}
