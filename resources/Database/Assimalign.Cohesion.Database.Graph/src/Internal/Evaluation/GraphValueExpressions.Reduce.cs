using System;
using System.Collections.Generic;
using System.Threading;
using Assimalign.Cohesion.Database.Graph.Language;
using Assimalign.Cohesion.Database.Graph.Protocol;

namespace Assimalign.Cohesion.Database.Graph.Internal;

internal static partial class GraphValueExpressions
{
    // Accumulators may change type and container depth on every step. Compute a finite
    // conservative closure, including the seed for zero iterations. Beyond the existing
    // shape horizon, possible shapes remain unknown and positive proofs are withheld.
    private static bool ReduceFlags(GqlReduceExpression expression, int depth, bool proof,
        Func<GqlExpression, Func<string, int, bool>, int, bool> analyze, Func<string, int, bool> outer)
    {
        const int count = GraphValueCodec.MaxDepth + 2;
        if (depth >= count) { return !proof; }
        var flags = new bool[count];
        var demanded = new bool[count];
        bool added = false;
        bool Demand(int access)
        {
            if (access >= count) { return !proof; }
            if (!demanded[access])
            {
                demanded[access] = true;
                flags[access] = analyze(expression.Initial, outer, access);
                added = true;
            }
            return flags[access];
        }
        bool Inner(string name, int access) => name == expression.Accumulator ? Demand(access)
            : name == expression.Variable ? analyze(expression.List, outer, access + 1) : outer(name, access);
        _ = Demand(depth);
        bool changed;
        do
        {
            changed = false;
            added = false;
            for (int i = 0; i < count; i++)
            {
                if (!demanded[i]) { continue; }
                bool body = analyze(expression.Body, Inner, i);
                bool next = proof ? flags[i] && body : flags[i] || body;
                if (next != flags[i]) { flags[i] = next; changed = true; }
            }
        } while (changed || added);
        return flags[depth];
    }

    private static bool ReduceGroup(GqlReduceExpression expression, Func<string, int, bool> outer, int depth,
        Func<GqlNamedFunctionCallExpression, GqlExpression?>? native)
        => ReduceFlags(expression, depth, false, (value, scope, access) => IsGroup(value, scope, access, native), outer);

    private static bool ReduceMayReturn(GqlReduceExpression expression, IReadOnlyDictionary<string, object?>? parameters,
        bool entity, Func<string, int, bool, bool>? outer, int depth, Func<GqlNamedFunctionCallExpression, GraphFunctionType>? functions,
        Func<GqlNamedFunctionCallExpression, GqlExpression?>? native)
        => ReduceFlags(expression, depth, false,
            (value, scope, access) => MayReturn(value, parameters, entity, (name, at, _) => scope(name, at), access, functions, native),
            (name, access) => outer?.Invoke(name, access, entity) ?? entity && access == 0);

    private static bool ReduceScalar(GqlReduceExpression expression, IReadOnlyDictionary<string, object?>? parameters,
        Func<string, int, bool> outer, int depth, Func<GqlNamedFunctionCallExpression, GraphFunctionType>? functions,
        Func<GqlNamedFunctionCallExpression, GqlExpression?>? native)
        => ReduceFlags(expression, depth, true,
            (value, scope, access) => IsKnownScalar(value, parameters, scope, access, functions, native), outer);

    private static GraphEntityShape ReduceEntity(GqlReduceExpression expression, Func<string, int, GraphEntityShape> outer,
        int depth, CancellationToken token, Func<GqlNamedFunctionCallExpression, GraphFunctionType>? functions,
        Func<GqlNamedFunctionCallExpression, GqlExpression?>? native)
    {
        const int count = GraphValueCodec.MaxDepth + 2;
        if (depth >= count) { return default; }
        var shapes = new GraphEntityShape[count];
        var demanded = new bool[count];
        bool added = false;
        GraphEntityShape Demand(int access)
        {
            if (access >= count) { return default; }
            if (!demanded[access])
            {
                demanded[access] = true;
                shapes[access] = EntityShape(expression.Initial, outer, access, token, functions, native);
                added = true;
            }
            return shapes[access];
        }
        GraphEntityShape Inner(string name, int access) => name == expression.Accumulator ? Demand(access)
            : name == expression.Variable ? EntityShape(expression.List, outer, access + 1, token, functions, native) : outer(name, access);
        _ = Demand(depth);
        bool changed;
        do
        {
            changed = false;
            added = false;
            for (int i = 0; i < count; i++)
            {
                if (!demanded[i]) { continue; }
                token.ThrowIfCancellationRequested();
                var body = EntityShape(expression.Body, Inner, i, token, functions, native);
                var current = shapes[i];
                var next = current.Kind == body.Kind ? new GraphEntityShape(current.Kind, current.Optional || body.Optional) : default;
                if (next != current) { shapes[i] = next; changed = true; }
            }
        } while (changed || added);
        return shapes[depth];
    }
}
