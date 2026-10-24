using System;
using System.Collections.Generic;
using Assimalign.Cohesion.Database.Graph.Language;

namespace Assimalign.Cohesion.Database.Graph.Internal;

internal static partial class GraphExpressionEvaluator
{
    private static object? NamedFunction(GqlNamedFunctionCallExpression call, IReadOnlyDictionary<string, object?> bindings,
        GraphEvaluationContext context, IReadOnlyDictionary<string, object?>? parameters)
    {
        GraphFunction function = context.Operation is { } operation && operation.FunctionBindings.TryGetValue(call, out var bound)
            ? bound : throw new GraphPlanningException("CG05001", ErrorMessages.FunctionUnboundCall, call.Location);
        if (function is not GraphScalarFunction scalar) { throw new GraphPlanningException("CG05001", ErrorMessages.AggregatePlacement, call.Location); }
        GraphInlineArguments buffer = default;
        Span<object?> values = call.Arguments.Count <= 3 ? ((Span<object?>)buffer)[..call.Arguments.Count] : new object?[call.Arguments.Count];
        for (int i = 0; i < values.Length; i++) { values[i] = Evaluate(call.Arguments[i], bindings, context, parameters); }
        var arguments = new GraphArguments(values, new GraphFunctionContext(context));
        try { return scalar.Invoke(in arguments); }
        catch (GraphPlanningException error) { throw error.At(call.Location); }
    }
}
