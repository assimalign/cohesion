using System;
using System.Collections.Generic;
using System.Threading;
using Assimalign.Cohesion.Database.Graph.Language;
using Assimalign.Cohesion.Database.Graph.Protocol;

namespace Assimalign.Cohesion.Database.Graph.Internal;

internal static partial class GraphValueExpressions
{
    private static bool ReduceGroup(GqlReduceExpression expression, Func<string, int, bool> outer, int depth,
        Func<GqlNamedFunctionCallExpression, GqlExpression?>? native, GraphReductionAnalysis? analysis)
    {
        analysis ??= new(new(default));
        // Binary results have no container or entity shape feedback. Their scalar shape
        // is invariant even when their operand values depend on the accumulator.
        if (expression.Body is GqlBinaryExpression)
        { return depth >= GraphValueCodec.MaxDepth + 2 || IsGroup(expression.Initial, outer, depth, native, analysis); }
        return analysis.Closure(expression, 0, outer,
            (value, scope, access) => IsGroup(value, scope, access, native, analysis),
            static (current, body) => current || body, true).At(depth);
    }

    private static bool ReduceMayReturn(GqlReduceExpression expression, IReadOnlyDictionary<string, object?>? parameters,
        bool entity, Func<string, int, bool, bool>? outer, int depth, Func<GqlNamedFunctionCallExpression, GraphFunctionType>? functions,
        Func<GqlNamedFunctionCallExpression, GqlExpression?>? native, GraphReductionAnalysis? analysis)
    {
        analysis ??= new(new(default));
        if (expression.Body is GqlBinaryExpression)
        { return depth >= GraphValueCodec.MaxDepth + 2 || MayReturn(expression.Initial, parameters, entity, outer, depth, functions, native, analysis); }
        return analysis.Closure(expression, entity ? 1 : 2, (name, access) => outer?.Invoke(name, access, entity) ?? entity && access == 0,
            (value, scope, access) => MayReturn(value, parameters, entity, (name, at, _) => scope(name, at), access, functions, native, analysis),
            static (current, body) => current || body, true, outer).At(depth);
    }

    private static bool ReduceScalar(GqlReduceExpression expression, IReadOnlyDictionary<string, object?>? parameters,
        Func<string, int, bool> outer, int depth, Func<GqlNamedFunctionCallExpression, GraphFunctionType>? functions,
        Func<GqlNamedFunctionCallExpression, GqlExpression?>? native, GraphReductionAnalysis? analysis)
    {
        analysis ??= new(new(default));
        if (expression.Body is GqlBinaryExpression)
        { return depth == 0 && IsKnownScalar(expression.Initial, parameters, outer, depth, functions, native, analysis); }
        return analysis.Closure(expression, 3, outer,
            (value, scope, access) => IsKnownScalar(value, parameters, scope, access, functions, native, analysis),
            static (current, body) => current && body, false).At(depth);
    }

    private static GraphEntityShape ReduceEntity(GqlReduceExpression expression, Func<string, int, GraphEntityShape> outer,
        int depth, CancellationToken token, Func<GqlNamedFunctionCallExpression, GraphFunctionType>? functions,
        Func<GqlNamedFunctionCallExpression, GqlExpression?>? native, GraphReductionAnalysis? analysis)
    {
        analysis ??= new(new(token));
        if (expression.Body is GqlBinaryExpression) { return default; }
        return analysis.Closure(expression, 4, outer,
            (value, scope, access) => EntityShape(value, scope, access, token, functions, native, analysis),
            static (current, body) => current.Kind == body.Kind ? new GraphEntityShape(current.Kind, current.Optional || body.Optional) : default,
            default(GraphEntityShape)).At(depth);
    }
}
