using System.Collections.Generic;
using Assimalign.Cohesion.Database.Graph.Language;

namespace Assimalign.Cohesion.Database.Graph.Internal;

internal static partial class GraphExpressionEvaluator
{
    private static bool Exists(GqlExistsExpression expression, IReadOnlyDictionary<string, object?> bindings,
        GraphEvaluationContext context, IReadOnlyDictionary<string, object?>? parameters)
    {
        if (context.Database is null || context.Operation is not { } operation ||
            operation.FunctionBindings is not GraphCorrelatedBindings registry || !registry.Subplans.TryGetValue(expression, out var subplan))
        { throw new GraphPlanningException("CG05027", ErrorMessages.CorrelatedPlanRequired, expression.Location); }
        // Scalar evaluation is synchronous throughout this engine. The inner stream's awaits
        // never capture a synchronization context. Consume a completed ValueTask directly;
        // only a genuinely asynchronous storage read needs a blocking join at this seam.
        var result = GraphPlanExecutor.ExistsAsync(context.Database, operation, subplan, bindings, parameters, operation.FunctionCancellation);
        return result.IsCompletedSuccessfully ? result.Result : result.AsTask().GetAwaiter().GetResult();
    }
}
