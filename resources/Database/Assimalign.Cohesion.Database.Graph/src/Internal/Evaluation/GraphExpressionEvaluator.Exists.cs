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
        // Invariant: every inner evaluation completes synchronously, including failures.
        // Before any read can suspend this seam needs a real async scalar evaluation path.
        // Count incomplete tasks at the actual join so cold/warm corpus tests guard it.
        var result = GraphPlanExecutor.ExistsAsync(context.Database, operation, subplan, bindings, parameters, operation.FunctionCancellation);
        registry.ObserveInnerEvaluation(result.IsCompleted);
        return result.IsCompletedSuccessfully ? result.Result : result.AsTask().GetAwaiter().GetResult();
    }
}
