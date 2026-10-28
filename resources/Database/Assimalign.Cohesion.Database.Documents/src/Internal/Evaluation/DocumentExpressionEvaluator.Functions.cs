using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

using Assimalign.Cohesion.Database.Documents.Language;

namespace Assimalign.Cohesion.Database.Documents.Internal;

internal sealed partial class DocumentExpressionEvaluator
{
    private object? Call(OqlCallExpression call, DocumentEvaluationContext document, IReadOnlyList<DocumentEvaluationContext>? group)
    {
        // These SQL++ grammar forms retain lazy argument evaluation, like SQL CASE.
        if (call.Name is "COALESCE" or "IFMISSING" or "IFNULL" or "IFMISSINGORNULL")
        {
            foreach (var argument in call.Arguments)
            {
                var value = Evaluate(argument, document, group);
                bool skip = SkipConditional(call.Name, value);
                if (!skip) { return value; }
            }
            return null;
        }
        var function = ResolveFunction(call);
        if (function is DocumentAggregateFunction aggregate)
        {
            return Aggregate(call, aggregate, group ?? throw new DocumentPlanningException("CD05001", ErrorMessages.AggregateContextMissing));
        }
        var values = call.Arguments.Select(argument => Evaluate(argument, document, group)).ToArray();
        return InvokeScalar((DocumentScalarFunction)function, values);
    }

    private static bool SkipConditional(string name, object? value) => name switch
    {
        "IFMISSING" => value is DocumentMissing,
        "IFNULL" => value is null,
        _ => value is null or DocumentMissing,
    };

    private object? InvokeScalar(DocumentScalarFunction function, object?[] values)
    {
        var context = new DocumentFunctionContext(_databaseName, _cancellationToken, _transactionStart);
        var arguments = new DocumentArguments(values, in context);
        return function.Invoke(in arguments).Value;
    }

    private DocumentFunction ResolveFunction(OqlCallExpression call)
    {
        if (_resolvedFunctions is not null && _resolvedFunctions.TryGetValue(call, out var cached)) { return cached; }
        int count = call.Arguments.Count == 1 && call.Arguments[0] is OqlStarExpression ? 0 : call.Arguments.Count;
        var function = _functions.Find(call.Name, count) ?? throw new DocumentPlanningException("CD05001", ErrorMessages.FunctionArgumentsInvalid, call.Location);
        (_resolvedFunctions ??= new(ReferenceEqualityComparer.Instance)).Add(call, function);
        return function;
    }

    internal static JsonValueKind FunctionKind(object? value) => DocumentBuiltinCall.FunctionKind(value);
}
